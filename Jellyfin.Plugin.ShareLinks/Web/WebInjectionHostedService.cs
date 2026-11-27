using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShareLinks.Web;

/// <summary>
/// Gets the client script into the web client at startup, two ways. It writes the
/// tag into Jellyfin Web's index.html on disk, between explicit markers so the edit
/// can be applied repeatedly without drift, and it registers the tag with File
/// Transformation when that plugin is installed. <see cref="IndexHtmlScriptMiddleware"/>
/// adds the same tag while Jellyfin serves the page. Any one of the three is enough,
/// and they never add it twice.
/// </summary>
public sealed class WebInjectionHostedService : IHostedService, IDisposable
{
    private const int FileTransformationAttempts = 6;
    private static readonly TimeSpan FileTransformationRetryDelay = TimeSpan.FromSeconds(5);

    private readonly IServerApplicationPaths _paths;
    private readonly ILogger<WebInjectionHostedService> _logger;
    private readonly CancellationTokenSource _stopping = new();

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
            // Common in containers with a read-only web folder, and fine: the tag is
            // also added while the page is served. Logged where an admin will see it,
            // because when that fails too this line is the first clue.
            _logger.LogInformation("ShareLinks: could not write the script tag into {Path} ({Reason}); it is added when the page is served instead.", IndexPath, ex.Message);
        }

        _ = RegisterWithFileTransformationAsync(_stopping.Token);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => _stopping.Dispose();

    /// <summary>
    /// File Transformation may not be ready the moment this starts, so a failed
    /// registration is tried again a few times before giving up.
    /// </summary>
    private async Task RegisterWithFileTransformationAsync(CancellationToken cancellationToken)
    {
        try
        {
            for (var attempt = 1; attempt <= FileTransformationAttempts; attempt++)
            {
                if (FileTransformationRegistration.TryRegister(_logger) != FileTransformationRegistration.Outcome.Failed)
                {
                    return;
                }

                await Task.Delay(FileTransformationRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ShareLinks: registering with File Transformation stopped.");
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();

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
        if (IndexHtmlScript.IsPresent(html))
        {
            return;
        }

        TryBackup(path, path + ".sharelinks.bak");
        File.WriteAllText(path, IndexHtmlScript.AddTo(html));
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
