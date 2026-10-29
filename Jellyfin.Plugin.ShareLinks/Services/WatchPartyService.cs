using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ShareLinks.Models;
using Jellyfin.Plugin.ShareLinks.Storage;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay;
using MediaBrowser.Controller.SyncPlay.Requests;
using MediaBrowser.Model.SyncPlay;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShareLinks.Services;

/// <summary>Outcome of a guest asking to join their watch party.</summary>
public enum WatchPartyJoinResult
{
    /// <summary>The guest's session is in the party's group.</summary>
    Joined,

    /// <summary>The caller is not a guest of a live watch-party share.</summary>
    NotAParty,

    /// <summary>The party's group is gone, so the party is over.</summary>
    Ended,

    /// <summary>The group is playing something outside the share.</summary>
    NotShared,

    /// <summary>The guest's browser has no open WebSocket yet, so it would never hear it joined.</summary>
    NotConnected
}

/// <summary>
/// Binds share links to SyncPlay groups. A watch party is an ordinary share whose
/// guests are put into the group the host created with it, and which ends when that
/// group closes.
/// </summary>
/// <remarks>
/// The group id never comes from a guest. It is written onto the record when the
/// host creates the link, and joining reads it from the record of the account the
/// request is signed in as. The guard (<see cref="Security.GuestPluginApiGuard"/>)
/// uses <see cref="IsGroupAllowed"/> to keep guests out of every other group.
/// </remarks>
public sealed class WatchPartyService
{
    private const string ClientClaim = "Jellyfin-Client";
    private const string VersionClaim = "Jellyfin-Version";
    private const string DeviceIdClaim = "Jellyfin-DeviceId";
    private const string DeviceClaim = "Jellyfin-Device";

    private readonly ISyncPlayManager _syncPlayManager;
    private readonly ISessionManager _sessionManager;
    private readonly IUserManager _userManager;
    private readonly ShareLinkStore _store;
    private readonly ILogger<WatchPartyService> _logger;

    // Groups the guests of an ordinary (non-party) share created themselves, so
    // several viewers of one multi-use link can still watch together. In memory
    // only: SyncPlay groups do not survive a restart either.
    private readonly ConcurrentDictionary<Guid, Guid> _guestCreatedGroups = new();

    /// <summary>Initializes a new instance of the <see cref="WatchPartyService"/> class.</summary>
    public WatchPartyService(
        ISyncPlayManager syncPlayManager,
        ISessionManager sessionManager,
        IUserManager userManager,
        ShareLinkStore store,
        ILogger<WatchPartyService> logger)
    {
        _syncPlayManager = syncPlayManager;
        _sessionManager = sessionManager;
        _userManager = userManager;
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Gets the session a request belongs to, found the same way Jellyfin's own
    /// SyncPlay endpoints find it: by the client, version and device id the access
    /// token was issued for.
    /// </summary>
    public async Task<SessionInfo?> GetRequestSessionAsync(HttpContext httpContext, Guid userId)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return null;
        }

        var principal = httpContext.User;
        var deviceId = Claim(principal, DeviceIdClaim);
        if (string.IsNullOrEmpty(deviceId))
        {
            return null;
        }

        return await _sessionManager.LogSessionActivity(
            Claim(principal, ClientClaim) ?? string.Empty,
            Claim(principal, VersionClaim) ?? string.Empty,
            deviceId,
            Claim(principal, DeviceClaim) ?? string.Empty,
            httpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            user).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens the party's SyncPlay group with the host's session in it. Jellyfin tells
    /// the host's browser it joined over its WebSocket, so the host's client switches
    /// to SyncPlay without doing anything.
    /// </summary>
    public Guid StartGroup(SessionInfo hostSession, string itemName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hostSession);

        var name = string.IsNullOrWhiteSpace(itemName) ? "Watch party" : "Watch party: " + itemName.Trim();
        if (name.Length > 120)
        {
            name = name[..120];
        }

        var group = _syncPlayManager.NewGroup(hostSession, new NewGroupRequest(name), cancellationToken);
        _logger.LogInformation(
            "ShareLinks: opened SyncPlay group {GroupId} for a watch party hosted by session {SessionId}.",
            group.GroupId,
            hostSession.Id);
        return group.GroupId;
    }

    /// <summary>Puts a guest's own session into their party's group.</summary>
    public async Task<WatchPartyJoinResult> JoinAsync(Guid guestUserId, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var record = await _store.GetActiveByGuestUserIdAsync(guestUserId, cancellationToken).ConfigureAwait(false);
        if (record is null || !record.WatchParty || !record.WatchPartyGroupId.HasValue)
        {
            return WatchPartyJoinResult.NotAParty;
        }

        var groupId = record.WatchPartyGroupId.Value;
        if (!GroupExists(groupId, record.CreatedByUserId))
        {
            return WatchPartyJoinResult.Ended;
        }

        // SessionInfo.IsActive is true for a session with no controllers at all, so
        // it cannot tell "no socket yet" apart from "socket open". A live controller can.
        var session = await GetRequestSessionAsync(httpContext, guestUserId).ConfigureAwait(false);
        if (session is null || !session.SessionControllers.Any(controller => controller.IsSessionActive))
        {
            return WatchPartyJoinResult.NotConnected;
        }

        // Asked as the guest: Jellyfin answers null when the group's queue holds
        // anything the guest cannot open, which is the check JoinGroup makes too.
        // Asking first lets the guest be told why instead of being silently refused.
        if (_syncPlayManager.GetGroup(session, groupId) is null)
        {
            return WatchPartyJoinResult.NotShared;
        }

        _syncPlayManager.JoinGroup(session, new JoinGroupRequest(groupId), cancellationToken);
        _logger.LogInformation(
            "ShareLinks: guest session {SessionId} joined watch party {GroupId} of record {RecordId}.",
            session.Id,
            groupId,
            record.Id);
        return WatchPartyJoinResult.Joined;
    }

    /// <summary>Takes the calling guest's session out of whatever SyncPlay group it is in.</summary>
    public async Task LeaveAsync(HttpContext httpContext, Guid guestUserId, CancellationToken cancellationToken)
    {
        var session = await GetRequestSessionAsync(httpContext, guestUserId).ConfigureAwait(false);
        if (session is not null)
        {
            _syncPlayManager.LeaveGroup(session, new LeaveGroupRequest(), cancellationToken);
        }
    }

    /// <summary>
    /// Gets a value indicating whether a SyncPlay group still exists. Asked as the
    /// host, who can open everything the party plays.
    /// </summary>
    public bool GroupExists(Guid groupId, Guid? hostUserId)
    {
        if (!hostUserId.HasValue || _userManager.GetUserById(hostUserId.Value) is null)
        {
            return false;
        }

        try
        {
            var probe = new SessionInfo(_sessionManager, _logger) { UserId = hostUserId.Value };
            return _syncPlayManager.GetGroup(probe, groupId) is not null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ShareLinks: could not look up SyncPlay group {GroupId}.", groupId);
            return false;
        }
    }

    /// <summary>
    /// Gets a value indicating whether a guest of this share may see or join a group:
    /// a party's own group, or for an ordinary share a group one of its own guests created.
    /// </summary>
    public bool IsGroupAllowed(ShareLinkRecord record, Guid groupId)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.WatchParty)
        {
            return record.WatchPartyGroupId.HasValue && record.WatchPartyGroupId.Value.Equals(groupId);
        }

        return _guestCreatedGroups.TryGetValue(groupId, out var owner) && owner.Equals(record.Id);
    }

    /// <summary>Remembers a group a guest of an ordinary share created.</summary>
    public void RegisterGuestGroup(ShareLinkRecord record, Guid groupId)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (!record.WatchParty)
        {
            _guestCreatedGroups[groupId] = record.Id;
        }
    }

    /// <summary>Forgets the groups of a share being torn down.</summary>
    public void ForgetShare(Guid recordId)
    {
        foreach (var entry in _guestCreatedGroups.Where(entry => entry.Value.Equals(recordId)).ToArray())
        {
            _guestCreatedGroups.TryRemove(entry.Key, out _);
        }
    }

    private static string? Claim(ClaimsPrincipal principal, string type)
    {
        return principal.Claims.FirstOrDefault(claim => claim.Type.Equals(type, StringComparison.OrdinalIgnoreCase))?.Value;
    }
}
