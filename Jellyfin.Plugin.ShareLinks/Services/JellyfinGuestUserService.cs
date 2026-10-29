using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Data.Queries;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Entities.Security;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.ShareLinks.Models;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShareLinks.Services;

/// <summary>Creates and tears down temporary Jellyfin guest users.</summary>
public sealed class JellyfinGuestUserService
{
    /// <summary>
    /// The app name builds before 1.0.6.2 stamped on a guest session. Only used to
    /// recognise device rows those builds left behind.
    /// </summary>
    public const string LegacyGuestAppName = "ShareLinks";

    /// <summary>
    /// The app name a guest session is minted under. It has to be the one jellyfin-web
    /// sends, because Jellyfin keys a session on app name plus device id and the
    /// WebSocket, which can't send headers, falls back to the app name stored with the
    /// token. Any other name splits the guest across two sessions and SyncPlay messages
    /// go to the one without a socket.
    /// </summary>
    public const string GuestAppName = "Jellyfin Web";

    /// <summary>
    /// Prefix of every device id redemption hands out. Marks this plugin's device rows
    /// now that the app name is the web client's own.
    /// </summary>
    public const string GuestDeviceIdPrefix = "sharelinks-";

    /// <summary>The highest guest bitrate limit the config page offers, in Mbit/s.</summary>
    public const int MaxGuestRemoteBitrateLimitMbps = 2000;

    private readonly IUserManager _userManager;
    private readonly IDeviceManager _deviceManager;
    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<JellyfinGuestUserService> _logger;

    /// <summary>Initializes a new instance of the <see cref="JellyfinGuestUserService"/> class.</summary>
    public JellyfinGuestUserService(
        IUserManager userManager,
        IDeviceManager deviceManager,
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        ILogger<JellyfinGuestUserService> logger)
    {
        _userManager = userManager;
        _deviceManager = deviceManager;
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>Builds the temporary guest username for a share record.</summary>
    public static string BuildGuestUsername(ShareLinkRecord record)
    {
        var prefix = Plugin.Instance?.Configuration.GuestUsernamePrefix ?? "share-";
        return $"{prefix}{record.Id:N}";
    }

    /// <summary>Generates a strong random password suitable for a temporary guest user.</summary>
    public static string GeneratePassword()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    /// <summary>Ensures the temporary guest user exists and has the correct policy and password.</summary>
    public async Task<User> EnsureGuestUserAsync(ShareLinkRecord record, string password, CancellationToken cancellationToken)
    {
        if (record is null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new ArgumentException("Password cannot be empty.", nameof(password));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var username = record.GuestUserName;
        if (string.IsNullOrWhiteSpace(username))
        {
            username = BuildGuestUsername(record);
            record.GuestUserName = username;
        }

        var user = _userManager.GetUserByName(username);
        if (user is null)
        {
            user = await _userManager.CreateUserAsync(username).ConfigureAwait(false);
            if (user is null)
            {
                throw new InvalidOperationException($"Unable to create temporary guest user '{username}'.");
            }
        }

        // The password must be set before the policy update: UpdatePolicyAsync bumps the
        // user's EF concurrency token server side, and ChangePassword with a stale instance
        // then throws DbUpdateConcurrencyException. The password is only a fallback - the
        // policy hands the account to GuestAuthenticationProvider, which refuses every
        // interactive sign-in - but it means the account is never reachable with a blank
        // password either.
        await _userManager.ChangePasswordAsync(user, password).ConfigureAwait(false);
        await ApplyPolicyAsync(user, record, disabled: false).ConfigureAwait(false);

        user = _userManager.GetUserById(user.Id) ?? user;
        _logger.LogInformation("ShareLinks: ensured guest user {UserName} for record {RecordId}.", user.Username, record.Id);
        return user;
    }

    /// <summary>Disables a temporary guest user before deletion.</summary>
    public async Task DisableGuestUserAsync(ShareLinkRecord record, CancellationToken cancellationToken)
    {
        var user = FindRecordUser(record);
        if (user is null)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await ApplyPolicyAsync(user, record, disabled: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ShareLinks: failed to disable guest user {UserName} for record {RecordId}.", user.Username, record.Id);
        }
    }

    /// <summary>Deletes a temporary guest user if it exists.</summary>
    public async Task DeleteGuestUserAsync(ShareLinkRecord record, CancellationToken cancellationToken)
    {
        var user = FindRecordUser(record);
        if (user is null)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // Devices first. Jellyfin does not cascade a user delete to the Device
            // rows that redemption created, and a Device whose user is gone makes
            // DeviceManager.ToDeviceInfo throw for the WHOLE listing, so one leftover
            // guest 404s the admin dashboard's devices page entirely.
            await DeleteDevicesForUserAsync(user.Id).ConfigureAwait(false);

            // Then the live sessions. A device delete revokes the token but leaves
            // the session in memory, and a session still sitting in a SyncPlay group
            // after its user is gone makes the group's access check fail for everyone
            // else in it: the host could no longer change what is playing. Ending the
            // session takes it out of its group while the user still resolves.
            await EndSessionsForUserAsync(user.Id).ConfigureAwait(false);
            await _userManager.DeleteUserAsync(user.Id).ConfigureAwait(false);
            _logger.LogInformation("ShareLinks: deleted guest user {UserName} for record {RecordId}.", user.Username, record.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ShareLinks: failed to delete guest user {UserName} for record {RecordId}.", user.Username, record.Id);
            throw;
        }
    }

    /// <summary>Ends every live session of a guest account.</summary>
    public async Task EndSessionsForUserAsync(Guid userId)
    {
        var sessionIds = _sessionManager.Sessions
            .Where(session => session.UserId.Equals(userId))
            .Select(session => session.Id)
            .ToArray();

        foreach (var sessionId in sessionIds)
        {
            try
            {
                await _sessionManager.ReportSessionEnded(sessionId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ShareLinks: failed to end session {SessionId} of guest {UserId}.", sessionId, userId);
            }
        }
    }

    /// <summary>
    /// Lets the guest open a shared playlist. Jellyfin decides who can see a playlist
    /// from its share list, not from tags, so the tag alone is not enough. The guest
    /// is added read-only. Saved as a metadata edit, so the playlist file keeps it
    /// across a library scan, the same way a share made in the web client would.
    /// </summary>
    public async Task GrantPlaylistAsync(Playlist playlist, Guid guestUserId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(playlist);

        var shares = (playlist.Shares ?? Array.Empty<PlaylistUserPermissions>()).ToList();
        if (shares.Any(share => share.UserId.Equals(guestUserId) && !share.CanEdit))
        {
            return;
        }

        shares.RemoveAll(share => share.UserId.Equals(guestUserId));
        shares.Add(new PlaylistUserPermissions(guestUserId, false));
        playlist.Shares = shares;
        await _libraryManager.UpdateItemAsync(playlist, playlist.GetParent() ?? playlist, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("ShareLinks: gave guest {UserId} read access to playlist {PlaylistId}.", guestUserId, playlist.Id);
    }

    /// <summary>Takes a guest back off a playlist's share list.</summary>
    public async Task RevokePlaylistAsync(Playlist playlist, Guid guestUserId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(playlist);

        var shares = (playlist.Shares ?? Array.Empty<PlaylistUserPermissions>()).ToList();
        if (shares.RemoveAll(share => share.UserId.Equals(guestUserId)) == 0)
        {
            return;
        }

        playlist.Shares = shares;
        await _libraryManager.UpdateItemAsync(playlist, playlist.GetParent() ?? playlist, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("ShareLinks: removed guest {UserId} from playlist {PlaylistId}.", guestUserId, playlist.Id);
    }

    /// <summary>Removes every device row belonging to a guest account.</summary>
    private async Task DeleteDevicesForUserAsync(Guid userId)
    {
        // GetDevices returns the raw entities. GetDevicesForUser/GetDevice project to
        // DTOs and resolve the owning user on the way, which is exactly what throws
        // once the user is gone, so neither of those can be used to clean up after it.
        var devices = _deviceManager.GetDevices(new DeviceQuery { UserId = userId });
        foreach (var device in devices.Items)
        {
            try
            {
                await _deviceManager.DeleteDevice(device).ConfigureAwait(false);
                _logger.LogInformation(
                    "ShareLinks: deleted device {DeviceId} ({AppName}) for guest {UserId}.",
                    device.DeviceId,
                    device.AppName,
                    userId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ShareLinks: failed to delete device {DeviceId}.", device.DeviceId);
            }
        }
    }

    /// <summary>
    /// Deletes device rows left behind by guests that were removed before this
    /// cleanup existed. Scoped to devices this plugin created, so a stale row from
    /// anything else on the server is left for its owner to deal with.
    /// </summary>
    public async Task<int> PurgeOrphanedGuestDevicesAsync(CancellationToken cancellationToken)
    {
        var removed = 0;
        var devices = _deviceManager.GetDevices(new DeviceQuery());
        foreach (var device in devices.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsGuestDevice(device))
            {
                continue;
            }

            if (_userManager.GetUserById(device.UserId) is not null)
            {
                continue;
            }

            try
            {
                await _deviceManager.DeleteDevice(device).ConfigureAwait(false);
                removed++;
                _logger.LogInformation(
                    "ShareLinks: removed orphaned guest device {DeviceId} whose user {UserId} no longer exists.",
                    device.DeviceId,
                    device.UserId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ShareLinks: failed to remove orphaned device {DeviceId}.", device.DeviceId);
            }
        }

        return removed;
    }

    private static bool IsGuestDevice(Device device)
    {
        return (device.DeviceId?.StartsWith(GuestDeviceIdPrefix, StringComparison.Ordinal) ?? false)
            || string.Equals(device.AppName, LegacyGuestAppName, StringComparison.Ordinal);
    }

    private User? FindRecordUser(ShareLinkRecord record)
    {
        if (record.GuestUserId.HasValue)
        {
            var user = _userManager.GetUserById(record.GuestUserId.Value);
            if (user is not null)
            {
                return user;
            }
        }

        return string.IsNullOrWhiteSpace(record.GuestUserName)
            ? null
            : _userManager.GetUserByName(record.GuestUserName);
    }

    private async Task ApplyPolicyAsync(User user, ShareLinkRecord record, bool disabled)
    {
        var policy = BuildPolicy(record, disabled, user.PasswordResetProviderId);
        await _userManager.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);
    }

    /// <summary>
    /// The guest account's policy. Everything is off unless the share needs it, and
    /// what a share needs depends on its kind: Live TV access only for a channel
    /// share, downloads only for a book, which the reader opens as a file.
    /// </summary>
    public static UserPolicy BuildPolicy(ShareLinkRecord record, bool disabled, string? passwordResetProviderId)
    {
        ArgumentNullException.ThrowIfNull(record);

        var config = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
        return new UserPolicy
        {
            // Hand the account to a provider that refuses interactive sign-in. If the
            // plugin is ever disabled the id stops resolving and Jellyfin falls back to
            // its own InvalidAuthProvider, which also refuses, so this fails closed.
            AuthenticationProviderId = GuestAuthenticationProvider.ProviderId,
            PasswordResetProviderId = passwordResetProviderId ?? string.Empty,
            AllowedTags = string.IsNullOrWhiteSpace(record.AllowedTag)
                ? Array.Empty<string>()
                : new[] { record.AllowedTag! },
            BlockedTags = Array.Empty<string>(),
            IsAdministrator = false,
            IsHidden = true,
            IsDisabled = disabled,
            EnableCollectionManagement = false,
            EnableSubtitleManagement = false,
            EnableLyricManagement = false,
            EnableUserPreferenceAccess = false,
            EnableSharedDeviceControl = false,
            EnableRemoteAccess = true,
            EnableRemoteControlOfOtherUsers = false,
            EnableLiveTvManagement = false,
            EnableLiveTvAccess = record.Kind == ShareKind.LiveTv,
            EnableMediaPlayback = true,
            EnableAudioPlaybackTranscoding = config.AllowTranscoding,
            EnableVideoPlaybackTranscoding = config.AllowTranscoding,
            EnablePlaybackRemuxing = config.AllowRemuxing,
            RemoteClientBitrateLimit = RemoteBitrateLimit(config),
            ForceRemoteSourceTranscoding = false,
            EnableContentDeletion = false,
            EnableContentDeletionFromFolders = Array.Empty<string>(),
            EnableContentDownloading = record.Kind == ShareKind.Book,
            EnableSyncTranscoding = false,
            EnableMediaConversion = false,
            EnableAllChannels = false,
            EnabledChannels = Array.Empty<Guid>(),
            EnableAllDevices = true,
            EnabledDevices = Array.Empty<string>(),
            EnableAllFolders = true,
            EnabledFolders = Array.Empty<Guid>(),
            EnablePublicSharing = false,
            LoginAttemptsBeforeLockout = -1,
            // One viewer for a one-use link. A multi-use link gets the configured
            // ceiling, where 0 is how Jellyfin spells "no limit" in its session check.
            MaxActiveSessions = record.OneUse ? 1 : Math.Max(config.MaxConcurrentViewers, 0),
            BlockUnratedItems = Array.Empty<UnratedItem>(),
            // A watch-party guest joins the host's group and nothing else, so it can
            // not create one. Other guests of a playable share keep SyncPlay; the
            // guard keeps them to groups their own share created. Shares with
            // nothing SyncPlay can play get none.
            SyncPlayAccess = record.WatchParty
                ? SyncPlayUserAccessType.JoinGroups
                : ShareableItemPolicy.SupportsWatchParty(record.Kind)
                    ? SyncPlayUserAccessType.CreateAndJoinGroups
                    : SyncPlayUserAccessType.None
        };
    }

    /// <summary>
    /// The guests' remote bitrate limit in bit/s, as Jellyfin's user policy takes it.
    /// 0 when transcoding is off: Jellyfin couldn't bring a stream under the cap and
    /// would refuse to play it instead. 0 also leaves the server's own Internet
    /// streaming limit in charge.
    /// </summary>
    public static int RemoteBitrateLimit(Configuration.PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (!config.AllowTranscoding)
        {
            return 0;
        }

        // Clamped to what the config page offers, which also keeps the product in an int.
        return Math.Clamp(config.GuestRemoteBitrateLimitMbps, 0, MaxGuestRemoteBitrateLimitMbps) * 1_000_000;
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
