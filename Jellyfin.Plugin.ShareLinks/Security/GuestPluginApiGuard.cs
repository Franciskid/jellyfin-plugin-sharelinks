using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ShareLinks.Models;
using Jellyfin.Plugin.ShareLinks.Services;
using Jellyfin.Plugin.ShareLinks.Storage;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.SyncPlay;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShareLinks.Security;

/// <summary>
/// Refuses share-guest accounts access to any plugin's API surface.
///
/// The web-client lockdown in sharelinks.js can only hide things from a browser
/// that chooses to run it. A guest holds a real Jellyfin access token, so curl or
/// a native client sees everything the CSS was hiding. This filter is the part
/// that actually holds: it runs server side on every MVC action, so the caller's
/// choice of client is irrelevant.
///
/// The rule is structural rather than a curated route list. Jellyfin's own API
/// lives in one assembly and is already bounded for guests by the share tag
/// policy, so it is allowed wholesale. Everything else is by definition a
/// plugin's controller and is refused unless the admin opted that plugin in.
/// A plugin installed next month is therefore covered on the day it lands.
///
/// It also keeps a guest's play queue from being handed media the guest cannot
/// open. Intro providers (Local Intros, cinema mode trailers) return videos that
/// do not carry the share tag; the client queues them ahead of the movie, the
/// first PlaybackInfo call 404s and the whole queue fails. The intro route always
/// answers a guest with an empty list, and as a net for any route or plugin that
/// does the same thing some other way, every playable item in a guest's response
/// is checked against the guest's own access and dropped if they could not open it.
///
/// Jellyfin's own API gets a few extra rules on top of the tag policy, for the parts
/// the policy cannot reach (see <see cref="GuestCoreRules"/>): guests only see and
/// join their own share's SyncPlay groups, a host-only watch party keeps playback
/// controls with the host, Live TV is limited to watching a channel, and a live
/// stream can only be opened for an item the guest can open.
/// </summary>
public sealed class GuestPluginApiGuard : IAsyncActionFilter
{
    private const string CoreApiAssemblyName = "Jellyfin.Api";
    private const string UserLibraryControllerName = "UserLibrary";
    private const string DeviceIdClaim = "Jellyfin-DeviceId";

    /// <summary>Text arguments that can name an item, and so a Live TV channel.</summary>
    private static readonly HashSet<string> ItemTextArguments = new(StringComparer.OrdinalIgnoreCase)
    {
        "openToken",
        "liveStreamId",
        "mediaSourceId",
        "programId",
        "channelId"
    };

    private static readonly Assembly OwnAssembly = typeof(GuestPluginApiGuard).Assembly;

    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IPluginManager _pluginManager;
    private readonly ShareLinkStore _store;
    private readonly WatchPartyService _watchPartyService;
    private readonly ILogger<GuestPluginApiGuard> _logger;

    /// <summary>Initializes a new instance of the <see cref="GuestPluginApiGuard"/> class.</summary>
    public GuestPluginApiGuard(
        IUserManager userManager,
        ILibraryManager libraryManager,
        IPluginManager pluginManager,
        ShareLinkStore store,
        WatchPartyService watchPartyService,
        ILogger<GuestPluginApiGuard> logger)
    {
        _userManager = userManager;
        _libraryManager = libraryManager;
        _pluginManager = pluginManager;
        _store = store;
        _watchPartyService = watchPartyService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        if (next is null)
        {
            throw new ArgumentNullException(nameof(next));
        }

        var config = Plugin.Instance?.Configuration;
        var guest = config is { Enabled: true } ? FindGuest(context) : null;
        if (guest is null)
        {
            await next().ConfigureAwait(false);
            return;
        }

        if (IsIntroRequest(context))
        {
            context.Result = new OkObjectResult(new QueryResult<BaseItemDto>(Array.Empty<BaseItemDto>()));
            return;
        }

        if (config!.GuestPluginApiGuardEnabled && IsBlocked(context, config.GuestAllowedPluginIds, guest))
        {
            context.Result = new StatusCodeResult(403);
            return;
        }

        // Not tied to the plugin guard setting: these rules keep guests inside the
        // share on Jellyfin's own API, the same way the tag policy does.
        var core = IsCoreAction(context) ? (ControllerActionDescriptor)context.ActionDescriptor : null;
        ShareLinkRecord? share = null;
        if (core is not null)
        {
            var isSyncPlay = string.Equals(core.ControllerName, "SyncPlay", StringComparison.Ordinal);
            var liveTvItems = FindLiveTvItems(context, guest);
            if (isSyncPlay || liveTvItems.Count > 0)
            {
                share = await _store.GetActiveByGuestUserIdAsync(guest.Id, context.HttpContext.RequestAborted).ConfigureAwait(false);
            }

            if (liveTvItems.Any(item => GuestCoreRules.IsOutsideLiveTvShare(item, share)))
            {
                _logger.LogInformation(
                    "ShareLinks: refused guest {UserName} a Live TV channel it was not sent, via {Controller}.{Action}.",
                    guest.Username,
                    core.ControllerName,
                    core.ActionName);
                context.Result = new StatusCodeResult(403);
                return;
            }

            var call = new GuestCall(
                core.ControllerName,
                core.ActionName,
                share,
                isSyncPlay ? ReadGroupId(context) : null,
                context.ActionArguments.TryGetValue("userId", out var routeUser) && routeUser is Guid userId ? userId : null,
                guest.Id);
            var decision = GuestCoreRules.Evaluate(call, _watchPartyService.IsGroupAllowed);

            if (!await ApplyDecisionAsync(context, decision, core, guest).ConfigureAwait(false))
            {
                return;
            }
        }

        var executed = await next().ConfigureAwait(false);
        try
        {
            DropItemsGuestCannotOpen(executed, guest);
            if (core is not null)
            {
                AnswerAlbumOfSharedTrack(context, executed, core, guest);
                KeepToOwnSessionAndUser(executed, core, guest);
                if (share is not null)
                {
                    FollowSyncPlayResult(executed, core, share);
                }
            }
        }
        catch (Exception ex)
        {
            // Never turn a working response into a failed one over the safety net.
            _logger.LogWarning(ex, "ShareLinks: could not filter a response for guest {UserName}.", guest.Username);
        }
    }

    /// <summary>Carries out a core-rule decision. Returns true when the call should go ahead.</summary>
    private async Task<bool> ApplyDecisionAsync(ActionExecutingContext context, GuestCoreDecision decision, ControllerActionDescriptor core, User guest)
    {
        switch (decision)
        {
            case GuestCoreDecision.Allow:
                return true;

            case GuestCoreDecision.CheckLiveStream:
                if (CanOpenLiveStream(context, guest))
                {
                    return true;
                }

                break;

            case GuestCoreDecision.NotFound:
                context.Result = new NotFoundResult();
                return false;

            case GuestCoreDecision.LeaveGroup:
                await _watchPartyService.LeaveAsync(context.HttpContext, guest.Id, context.HttpContext.RequestAborted).ConfigureAwait(false);
                context.Result = new NoContentResult();
                _logger.LogInformation("ShareLinks: guest {UserName} stopped a host-only watch party; took them out of the group instead.", guest.Username);
                return false;
        }

        _logger.LogInformation(
            "ShareLinks: refused guest {UserName} access to {Controller}.{Action}.",
            guest.Username,
            core.ControllerName,
            core.ActionName);
        context.Result = new StatusCodeResult(403);
        return false;
    }

    /// <summary>
    /// Before it plays a track, the web client fetches the track's album for its
    /// loudness gain, and gives up on playback when that fails. A shared track, or a
    /// playlist or collection of them, does not include the album, and tagging the
    /// album would hand over every other track on it. So when a guest is refused an
    /// album holding a track they can play, they get a stand-in that carries only
    /// what the track's own details already name (id and title) and the gain. Not
    /// its tracks, artwork, people or anything else.
    /// </summary>
    private void AnswerAlbumOfSharedTrack(ActionExecutingContext context, ActionExecutedContext executed, ControllerActionDescriptor core, User guest)
    {
        if (!string.Equals(core.ControllerName, UserLibraryControllerName, StringComparison.Ordinal)
            || !core.ActionName.StartsWith("GetItem", StringComparison.Ordinal)
            || core.ActionName.StartsWith("GetItems", StringComparison.Ordinal)
            || !IsNotFound(executed.Result)
            || !context.ActionArguments.TryGetValue("itemId", out var value)
            || value is not Guid albumId
            || TryGetItem(albumId) is not MusicAlbum album)
        {
            return;
        }

        var playable = _libraryManager.GetItemList(new InternalItemsQuery(guest)
        {
            ParentId = album.Id,
            IncludeItemTypes = new[] { BaseItemKind.Audio },
            Recursive = true,
            Limit = 1,
            DtoOptions = new DtoOptions(false)
        });
        if (playable.Count == 0)
        {
            return;
        }

        executed.Result = new OkObjectResult(new BaseItemDto
        {
            Id = album.Id,
            Name = album.Name,
            Type = BaseItemKind.MusicAlbum,
            IsFolder = true,
            NormalizationGain = album.NormalizationGain
        });
    }

    private static bool IsNotFound(IActionResult? result)
    {
        return result switch
        {
            StatusCodeResult status => status.StatusCode == 404,
            ObjectResult value => value.StatusCode == 404,
            _ => false
        };
    }

    /// <summary>
    /// Cuts the session list down to the caller's own session, and the user list
    /// down to the caller. Without it every viewer of a multi-use link sees the
    /// others' sessions, address included, and any guest can list every account.
    /// </summary>
    private static void KeepToOwnSessionAndUser(ActionExecutedContext executed, ControllerActionDescriptor core, User guest)
    {
        if (executed.Result is not ObjectResult result)
        {
            return;
        }

        if (string.Equals(core.ControllerName, "Session", StringComparison.Ordinal)
            && string.Equals(core.ActionName, "GetSessions", StringComparison.Ordinal)
            && result.Value is IEnumerable<SessionInfoDto> sessions)
        {
            var deviceId = executed.HttpContext.User.FindFirst(DeviceIdClaim)?.Value;
            result.Value = sessions
                .Where(session => !string.IsNullOrEmpty(deviceId)
                    && string.Equals(session.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)
                    && session.UserId.Equals(guest.Id))
                .ToList();
            return;
        }

        if (string.Equals(core.ControllerName, "User", StringComparison.Ordinal)
            && string.Equals(core.ActionName, "GetUsers", StringComparison.Ordinal)
            && result.Value is IEnumerable<UserDto> users)
        {
            result.Value = users.Where(user => user.Id.Equals(guest.Id)).ToList();
        }
    }

    /// <summary>
    /// Keeps the guard's view of SyncPlay current after a guest's call went through:
    /// a group list is cut down to the share's own groups, and a group a guest just
    /// created is remembered as theirs.
    /// </summary>
    private void FollowSyncPlayResult(ActionExecutedContext executed, ControllerActionDescriptor core, ShareLinkRecord share)
    {
        if (!string.Equals(core.ControllerName, "SyncPlay", StringComparison.Ordinal) || executed.Result is not ObjectResult result)
        {
            return;
        }

        if (string.Equals(core.ActionName, "SyncPlayCreateGroup", StringComparison.Ordinal) && result.Value is GroupInfoDto created)
        {
            _watchPartyService.RegisterGuestGroup(share, created.GroupId);
            return;
        }

        if (string.Equals(core.ActionName, "SyncPlayGetGroups", StringComparison.Ordinal) && result.Value is IEnumerable<GroupInfoDto> groups)
        {
            result.Value = groups.Where(group => _watchPartyService.IsGroupAllowed(share, group.GroupId)).ToList();
        }
    }

    /// <summary>
    /// A live stream is opened from a token, not an item id, and Jellyfin does not
    /// check the caller can open the item behind it. Every item the request names,
    /// in the token or beside it, has to be one the guest can open, and there has to
    /// be at least one. Live TV channels were already held to the share before this
    /// runs, since the tag check Jellyfin makes here lets every channel through.
    /// </summary>
    private bool CanOpenLiveStream(ActionExecutingContext context, User guest)
    {
        var named = NamedIds(context, guest).Select(TryGetItem).OfType<BaseItem>().ToList();
        return named.Count > 0 && named.All(item => _libraryManager.GetItemById<BaseItem>(item.Id, guest) is not null);
    }

    /// <summary>The Live TV channels and programmes a call names anywhere in its arguments.</summary>
    private List<BaseItem> FindLiveTvItems(ActionExecutingContext context, User guest)
    {
        return NamedIds(context, guest)
            .Select(TryGetItem)
            .Where(item => item is LiveTvChannel or LiveTvProgram)
            .Cast<BaseItem>()
            .ToList();
    }

    /// <summary>
    /// Every id a call names: plain and optional ids and lists of them, the ids in a
    /// live stream token or media source id, and the ItemId and OpenToken of a posted
    /// body. Other text arguments are left alone: a segment request carries a
    /// GUID-shaped playlist id on every fetch, and none of them name an item.
    /// The guest's own user id is left out too, since it is never an item.
    /// </summary>
    private static HashSet<Guid> NamedIds(ActionExecutingContext context, User guest)
    {
        var ids = new HashSet<Guid>();
        foreach (var (name, argument) in context.ActionArguments)
        {
            switch (argument)
            {
                case Guid id:
                    ids.Add(id);
                    break;
                case IEnumerable<Guid> list:
                    ids.UnionWith(list);
                    break;
                case string text when ItemTextArguments.Contains(name):
                    ids.UnionWith(GuestCoreRules.ExtractTokenGuids(text));
                    break;
                case string:
                    break;
                case not null when !argument.GetType().IsPrimitive && !argument.GetType().IsEnum:
                    ids.UnionWith(ReadBodyIds(argument));
                    break;
            }
        }

        ids.Remove(Guid.Empty);
        ids.Remove(guest.Id);
        return ids;
    }

    private BaseItem? TryGetItem(Guid id)
    {
        try
        {
            return _libraryManager.GetItemById(id);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ShareLinks: could not look up {Id} for a guest request.", id);
            return null;
        }
    }

    /// <summary>
    /// The item id and open token of a posted body, such as a live stream request.
    /// A body that cannot be read names nothing; where that matters (opening a live
    /// stream) naming nothing is refused.
    /// </summary>
    private static List<Guid> ReadBodyIds(object body)
    {
        var ids = new List<Guid>();
        try
        {
            var type = body.GetType();
            if (type.GetProperty("ItemId")?.GetValue(body) is Guid itemId)
            {
                ids.Add(itemId);
            }

            if (type.GetProperty("OpenToken")?.GetValue(body) is string token)
            {
                ids.AddRange(GuestCoreRules.ExtractTokenGuids(token));
            }
        }
        catch (Exception ex) when (ex is AmbiguousMatchException or TargetInvocationException or TargetParameterCountException)
        {
            // Nothing readable; see above.
        }

        return ids;
    }

    /// <summary>The SyncPlay group a call names: the route id, or the GroupId of its body.</summary>
    private static Guid? ReadGroupId(ActionExecutingContext context)
    {
        if (context.ActionArguments.TryGetValue("id", out var routeId) && routeId is Guid id)
        {
            return id;
        }

        foreach (var argument in context.ActionArguments.Values)
        {
            try
            {
                if (argument is not null && argument.GetType().GetProperty("GroupId")?.GetValue(argument) is Guid groupId)
                {
                    return groupId;
                }
            }
            catch (Exception ex) when (ex is AmbiguousMatchException or TargetInvocationException or TargetParameterCountException)
            {
                // Not a body we can read. Naming no group means joining none.
            }
        }

        return null;
    }

    private static bool IsCoreAction(ActionExecutingContext context)
    {
        return context.ActionDescriptor is ControllerActionDescriptor descriptor
            && string.Equals(descriptor.ControllerTypeInfo.Assembly.GetName().Name, CoreApiAssemblyName, StringComparison.Ordinal);
    }

    /// <summary>Maps a controller assembly back to the plugin that shipped it.</summary>
    public Guid? FindOwningPluginId(Assembly assembly)
    {
        foreach (var plugin in _pluginManager.Plugins)
        {
            var instanceAssembly = plugin.Instance?.GetType().Assembly;
            if (instanceAssembly is not null && instanceAssembly == assembly)
            {
                return plugin.Id;
            }
        }

        return null;
    }

    private bool IsBlocked(ActionExecutingContext context, IReadOnlyList<string>? allowedPluginIds, User guest)
    {
        // Only controller actions carry an assembly we can reason about. Anything
        // else (Razor pages, raw endpoints) is left alone rather than guessed at.
        if (context.ActionDescriptor is not ControllerActionDescriptor descriptor)
        {
            return false;
        }

        var assembly = descriptor.ControllerTypeInfo.Assembly;

        // Jellyfin's own API: the share tag policy is the boundary here, and it is
        // the same boundary playback depends on. Blocking any of it would break the
        // guest's ability to watch what they were sent.
        if (string.Equals(assembly.GetName().Name, CoreApiAssemblyName, StringComparison.Ordinal))
        {
            return false;
        }

        // ShareLinks' own routes must stay reachable: the guest's browser fetches
        // the lockdown script and its guest state from here. The admin routes on
        // this controller do their own Administrator check and already refuse a guest.
        if (assembly == OwnAssembly)
        {
            return false;
        }

        var pluginId = FindOwningPluginId(assembly);
        if (pluginId.HasValue && IsAllowedPlugin(allowedPluginIds, pluginId.Value))
        {
            return false;
        }

        _logger.LogInformation(
            "ShareLinks: refused guest {UserName} access to {Controller}.{Action} ({Assembly}).",
            guest.Username,
            descriptor.ControllerName,
            descriptor.ActionName,
            assembly.GetName().Name);
        return true;
    }

    /// <summary>GetIntros and its legacy /Users/{userId}/... twin.</summary>
    private static bool IsIntroRequest(ActionExecutingContext context)
    {
        return context.ActionDescriptor is ControllerActionDescriptor descriptor
            && string.Equals(descriptor.ControllerName, UserLibraryControllerName, StringComparison.Ordinal)
            && descriptor.ActionName.StartsWith("GetIntros", StringComparison.Ordinal)
            && string.Equals(descriptor.ControllerTypeInfo.Assembly.GetName().Name, CoreApiAssemblyName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Removes playable items the guest has no access to from a list response.
    /// The check is the one PlaybackInfo makes, so whatever survives it will play.
    /// Folders, views, people and the like are never touched: they are not queued
    /// for playback, and a guest's home screen depends on some of them.
    /// </summary>
    private void DropItemsGuestCannotOpen(ActionExecutedContext executed, User guest)
    {
        if (executed.Result is not ObjectResult result)
        {
            return;
        }

        switch (result.Value)
        {
            case QueryResult<BaseItemDto> page when page.Items is not null:
                var kept = page.Items.Where(dto => GuestCanOpen(dto, guest)).ToArray();
                var removed = page.Items.Count - kept.Length;
                if (removed > 0)
                {
                    page.Items = kept;
                    page.TotalRecordCount = Math.Max(0, page.TotalRecordCount - removed);
                    LogDropped(executed, guest, removed);
                }

                break;
            case List<BaseItemDto> list:
                var dropped = list.RemoveAll(dto => !GuestCanOpen(dto, guest));
                if (dropped > 0)
                {
                    LogDropped(executed, guest, dropped);
                }

                break;
            case BaseItemDto[] array:
                var filtered = array.Where(dto => GuestCanOpen(dto, guest)).ToArray();
                if (filtered.Length != array.Length)
                {
                    result.Value = filtered;
                    LogDropped(executed, guest, array.Length - filtered.Length);
                }

                break;
        }
    }

    private bool GuestCanOpen(BaseItemDto? dto, User guest)
    {
        if (dto is null || dto.MediaType == MediaType.Unknown)
        {
            return true;
        }

        return _libraryManager.GetItemById<BaseItem>(dto.Id, guest) is not null;
    }

    private void LogDropped(ActionExecutedContext executed, User guest, int count)
    {
        _logger.LogInformation(
            "ShareLinks: dropped {Count} item(s) guest {UserName} cannot open from {Action}.",
            count,
            guest.Username,
            executed.ActionDescriptor.DisplayName);
    }

    private User? FindGuest(ActionExecutingContext context)
    {
        var userId = GetUserId(context.HttpContext.User);
        if (userId == Guid.Empty)
        {
            return null;
        }

        var user = _userManager.GetUserById(userId);

        // The marker is written onto the account by JellyfinGuestUserService, and a
        // guest cannot clear it: changing a policy needs admin, which they are not.
        return user is not null
            && string.Equals(user.AuthenticationProviderId, GuestAuthenticationProvider.ProviderId, StringComparison.Ordinal)
            ? user
            : null;
    }

    private static bool IsAllowedPlugin(IReadOnlyList<string>? allowed, Guid pluginId)
    {
        if (allowed is null || allowed.Count == 0)
        {
            return false;
        }

        return allowed.Any(value => Guid.TryParse(value, out var parsed) && parsed == pluginId);
    }

    private static Guid GetUserId(ClaimsPrincipal? principal)
    {
        if (principal is null)
        {
            return Guid.Empty;
        }

        var claim = principal.FindFirst("Jellyfin-UserId")?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(claim, out var id) ? id : Guid.Empty;
    }
}
