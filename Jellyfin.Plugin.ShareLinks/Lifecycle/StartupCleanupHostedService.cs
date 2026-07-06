using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ShareLinks.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShareLinks.Lifecycle;

/// <summary>
/// Runs one cleanup pass at startup so stale records do not linger forever.
/// </summary>
public sealed class StartupCleanupHostedService : BackgroundService
{
    private readonly IShareLinkCleanupService _cleanupService;
    private readonly ILogger<StartupCleanupHostedService> _logger;

    /// <summary>Initializes a new instance of the <see cref="StartupCleanupHostedService"/> class.</summary>
    public StartupCleanupHostedService(
        IShareLinkCleanupService cleanupService,
        ILogger<StartupCleanupHostedService> logger)
    {
        _cleanupService = cleanupService;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _cleanupService.CleanupAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ShareLinks: startup cleanup failed.");
        }
    }
}
