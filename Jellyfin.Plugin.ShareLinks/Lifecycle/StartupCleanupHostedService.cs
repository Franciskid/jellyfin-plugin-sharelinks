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
    private readonly JellyfinGuestUserService _guestUserService;
    private readonly ILogger<StartupCleanupHostedService> _logger;

    /// <summary>Initializes a new instance of the <see cref="StartupCleanupHostedService"/> class.</summary>
    public StartupCleanupHostedService(
        IShareLinkCleanupService cleanupService,
        JellyfinGuestUserService guestUserService,
        ILogger<StartupCleanupHostedService> logger)
    {
        _cleanupService = cleanupService;
        _guestUserService = guestUserService;
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

        // Separate try: a device row stranded by an older build breaks the admin
        // devices page until it goes, so this should still run when the record
        // cleanup above fails for its own reasons.
        try
        {
            var removed = await _guestUserService.PurgeOrphanedGuestDevicesAsync(stoppingToken).ConfigureAwait(false);
            if (removed > 0)
            {
                _logger.LogInformation("ShareLinks: removed {Count} orphaned guest device(s) at startup.", removed);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ShareLinks: orphaned guest device sweep failed.");
        }
    }
}
