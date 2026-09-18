using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShareLinks.Web;

/// <summary>
/// Best-effort extra: injects the ShareLinks client script into Jellyfin Web's
/// index.html on disk at startup, using explicit markers so the edit can be
/// applied and removed repeatedly without drift. <see cref="IndexHtmlScriptMiddleware"/>
/// adds the same tag while Jellyfin serves the page, which is what makes the
/// guest flow work even when this cannot write to disk.
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
            _logger.LogDebug(ex, "ShareLinks: could not write the script tag into index.html, it is added when the page is served instead.");
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

        TryBackup(path, path + ".sharelinks.bak");

        var snippet = "\n" + Begin + "\n<script src=\"../ShareLinks/ClientScript\" defer></script>\n" + End + "\n";
        var bodyIndex = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        html = bodyIndex >= 0 ? html.Insert(bodyIndex, snippet) : html + snippet;

        File.WriteAllText(path, html);
        _logger.LogInformation("ShareLinks: injected client script into {Path}.", path);
    }

    private void TryBackup(string path, string backup)
    {
        // The backup is only a convenience. Some images (linuxserver) make the
        // web folder root-owned while index.html itself is writable, so a
        // failed copy must not stop the injection.
        try
        {
            if (!File.Exists(backup))
            {
                File.Copy(path, backup);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _logger.LogDebug(ex, "ShareLinks: could not back up {Path}, injecting without a backup.", path);
        }
    }
}
