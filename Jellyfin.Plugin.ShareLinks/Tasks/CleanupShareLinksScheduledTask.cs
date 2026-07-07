using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ShareLinks.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.ShareLinks.Tasks;

/// <summary>
/// Scheduled task that expires share links past their <c>ExpiresAtUtc</c> and tears down
/// the associated guest users and tags.
/// </summary>
public sealed class CleanupShareLinksScheduledTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly IShareLinkCleanupService _cleanupService;

    /// <summary>Initializes a new instance of the <see cref="CleanupShareLinksScheduledTask"/> class.</summary>
    public CleanupShareLinksScheduledTask(IShareLinkCleanupService cleanupService)
    {
        _cleanupService = cleanupService;
    }

    /// <inheritdoc />
    public string Name => "Clean up ShareLinks";

    /// <inheritdoc />
    public string Key => "ShareLinksCleanup";

    /// <inheritdoc />
    public string Description => "Expires share links past their expiry time and removes their guest users and tags.";

    /// <inheritdoc />
    public string Category => "ShareLinks";

    /// <inheritdoc />
    public bool IsHidden => false;

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public bool IsLogged => true;

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _ = progress;
        await _cleanupService.CleanupAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromMinutes(30).Ticks,
        };
    }
}
