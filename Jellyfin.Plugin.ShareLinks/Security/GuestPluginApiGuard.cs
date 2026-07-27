using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Plugin.ShareLinks.Services;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
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
/// </summary>
public sealed class GuestPluginApiGuard : IAsyncActionFilter
{
    private const string CoreApiAssemblyName = "Jellyfin.Api";

    private static readonly Assembly OwnAssembly = typeof(GuestPluginApiGuard).Assembly;

    private readonly IUserManager _userManager;
    private readonly IPluginManager _pluginManager;
    private readonly ILogger<GuestPluginApiGuard> _logger;

    /// <summary>Initializes a new instance of the <see cref="GuestPluginApiGuard"/> class.</summary>
    public GuestPluginApiGuard(
        IUserManager userManager,
        IPluginManager pluginManager,
        ILogger<GuestPluginApiGuard> logger)
    {
        _userManager = userManager;
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

        if (IsBlocked(context))
        {
            context.Result = new StatusCodeResult(403);
            return;
        }

        await next().ConfigureAwait(false);
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

    private bool IsBlocked(ActionExecutingContext context)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.Enabled || !config.GuestPluginApiGuardEnabled)
        {
            return false;
        }

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

        var userId = GetUserId(context.HttpContext.User);
        if (userId == Guid.Empty)
        {
            return false;
        }

        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return false;
        }

        // The marker is written onto the account by JellyfinGuestUserService, and a
        // guest cannot clear it: changing a policy needs admin, which they are not.
        if (!string.Equals(user.AuthenticationProviderId, GuestAuthenticationProvider.ProviderId, StringComparison.Ordinal))
        {
            return false;
        }

        var pluginId = FindOwningPluginId(assembly);
        if (pluginId.HasValue && IsAllowedPlugin(config.GuestAllowedPluginIds, pluginId.Value))
        {
            return false;
        }

        _logger.LogInformation(
            "ShareLinks: refused guest {UserName} access to {Controller}.{Action} ({Assembly}).",
            user.Username,
            descriptor.ControllerName,
            descriptor.ActionName,
            assembly.GetName().Name);
        return true;
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
