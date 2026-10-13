using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ShareLinks.Services;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
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
/// </summary>
public sealed class GuestPluginApiGuard : IAsyncActionFilter
{
    private const string CoreApiAssemblyName = "Jellyfin.Api";
    private const string IntrosControllerName = "UserLibrary";

    private static readonly Assembly OwnAssembly = typeof(GuestPluginApiGuard).Assembly;

    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IPluginManager _pluginManager;
    private readonly ILogger<GuestPluginApiGuard> _logger;

    /// <summary>Initializes a new instance of the <see cref="GuestPluginApiGuard"/> class.</summary>
    public GuestPluginApiGuard(
        IUserManager userManager,
        ILibraryManager libraryManager,
        IPluginManager pluginManager,
        ILogger<GuestPluginApiGuard> logger)
    {
        _userManager = userManager;
        _libraryManager = libraryManager;
        _pluginManager = pluginManager;
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

        var executed = await next().ConfigureAwait(false);
        try
        {
            DropItemsGuestCannotOpen(executed, guest);
        }
        catch (Exception ex)
        {
            // Never turn a working response into a failed one over the safety net.
            _logger.LogWarning(ex, "ShareLinks: could not filter a response for guest {UserName}.", guest.Username);
        }
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
            && string.Equals(descriptor.ControllerName, IntrosControllerName, StringComparison.Ordinal)
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
