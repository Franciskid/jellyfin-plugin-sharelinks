using Jellyfin.Plugin.ShareLinks.Lifecycle;
using Jellyfin.Plugin.ShareLinks.Security;
using Jellyfin.Plugin.ShareLinks.Services;
using Jellyfin.Plugin.ShareLinks.Storage;
using Jellyfin.Plugin.ShareLinks.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ShareLinks;

/// <summary>
/// Registers the foundational ShareLinks services used by later API and web
/// workers.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        _ = applicationHost;

        // Registered as a global MVC filter so it sees every plugin's controllers,
        // not just this one's. Options are materialised after all plugin registrators
        // have run, so adding to the collection here is in time.
        serviceCollection.AddSingleton<GuestPluginApiGuard>();
        serviceCollection.Configure<MvcOptions>(options => options.Filters.AddService<GuestPluginApiGuard>());

        serviceCollection.AddHostedService<WebInjectionHostedService>();
        serviceCollection.AddSingleton<ShareLinkStore>();
        serviceCollection.AddSingleton<ShareTokenService>();
        serviceCollection.AddSingleton<ItemTagService>();
        serviceCollection.AddSingleton<JellyfinGuestUserService>();
        serviceCollection.AddSingleton<IAuthenticationProvider, GuestAuthenticationProvider>();
        serviceCollection.AddSingleton<ShareLinkCreationService>();
        serviceCollection.AddSingleton<ShareLinkRedemptionService>();
        serviceCollection.AddSingleton<ShareLinkCleanupService>();
        serviceCollection.AddSingleton<IShareLinkCleanupService>(provider => provider.GetRequiredService<ShareLinkCleanupService>());
        serviceCollection.AddHostedService<StartupCleanupHostedService>();
    }
}
