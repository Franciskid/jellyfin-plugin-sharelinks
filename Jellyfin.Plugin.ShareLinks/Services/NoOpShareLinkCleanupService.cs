using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ShareLinks.Services;

/// <summary>
/// Temporary cleanup implementation used until the real cleanup pipeline lands.
/// </summary>
public sealed class NoOpShareLinkCleanupService : IShareLinkCleanupService
{
    /// <inheritdoc />
    public Task CleanupAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return Task.CompletedTask;
    }
}
