using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShareLinks.Web;

/// <summary>
/// Injects the ShareLinks client script into Jellyfin Web's index.html using
/// explicit markers so the edit can be applied and removed repeatedly without
/// drift.
/// </summary>
public sealed class WebInjectionHostedService : IHostedService
{
    private const string Begin = "<!-- ShareLinks:begin -->";
    private const string End = "<!-- ShareLinks:end -->";

    private readonly IServerApplicationPaths _paths;
    private readonly ILogger<WebInjectionHostedService> _logger;

    /// <summary>Initializes a new instance of the <see cref="WebInjectionHostedService"/> class.</summary>
    public WebInjectionHostedService(IServerApplicationPaths paths, ILogger<WebInjectionHostedService> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    private string IndexPath => Path.Combine(_paths.WebPath, "index.html");

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Inject();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ShareLinks: could not inject client script into web index.html.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Leave the marker in place. Some Jellyfin containers expose the web
        // client as root-owned files: startup injection may need a deployment
        // helper, and removing the marker on shutdown would make it vanish on
        // every restart.
        return Task.CompletedTask;
    }

    private void Inject()
    {
        var path = IndexPath;
        if (!File.Exists(path))
        {
            _logger.LogWarning("ShareLinks: web index.html not found at {Path}.", path);
            return;
        }

        var html = File.ReadAllText(path);
        if (html.Contains(Begin, StringComparison.Ordinal))
        {
            return;
        }

        var backup = path + ".sharelinks.bak";
        if (!File.Exists(backup))
        {
            File.Copy(path, backup);
        }

        var snippet = "\n" + Begin + "\n<script src=\"/ShareLinks/ClientScript\" defer></script>\n" + End + "\n";
        var bodyIndex = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        html = bodyIndex >= 0 ? html.Insert(bodyIndex, snippet) : html + snippet;

        File.WriteAllText(path, html);
        _logger.LogInformation("ShareLinks: injected client script into {Path}.", path);
    }

}
