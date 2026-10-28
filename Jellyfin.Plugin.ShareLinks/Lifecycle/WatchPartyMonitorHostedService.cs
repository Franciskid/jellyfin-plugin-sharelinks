using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ShareLinks.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShareLinks.Lifecycle;

/// <summary>
/// Ends a watch party soon after its SyncPlay group closes. The hourly cleanup task
/// would get there too, but a party link should stop working when the party does,
/// not up to an hour later.
/// </summary>
public sealed class WatchPartyMonitorHostedService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    private readonly ShareLinkCleanupService _cleanupService;
    private readonly ILogger<WatchPartyMonitorHostedService> _logger;

    /// <summary>Initializes a new instance of the <see cref="WatchPartyMonitorHostedService"/> class.</summary>
    public WatchPartyMonitorHostedService(ShareLinkCleanupService cleanupService, ILogger<WatchPartyMonitorHostedService> logger)
    {
        _cleanupService = cleanupService;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                if (Plugin.Instance?.Configuration.Enabled == false)
                {
                    continue;
                }

                try
                {
                    await _cleanupService.EndFinishedWatchPartiesAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ShareLinks: watch party check failed.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
