using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Jellyfin.Plugin.ShareLinks.Configuration;

namespace Jellyfin.Plugin.ShareLinks;

/// <summary>
/// ShareLinks plugin. Creates expiring guest-share links for Jellyfin items
/// without persisting raw tokens.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Initializes a new instance of the <see cref="Plugin"/> class.</summary>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>Gets the current plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "ShareLinks";

    /// <inheritdoc />
    public override string Description =>
        "Secure expiring share links for Jellyfin items with guest-user lockdown.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("68540b76-ee74-436d-85ff-2abc884bbea6");

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages() => new[]
    {
        new PluginPageInfo
        {
            Name = "ShareLinks",
            EmbeddedResourcePath = GetType().Namespace + ".Web.configPage.html"
        }
    };
}
