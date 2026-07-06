using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ShareLinks.Services;

/// <summary>
/// Cleanup seam for later workers. The initial implementation is a no-op.
/// </summary>
public interface IShareLinkCleanupService
{
    /// <summary>Runs one cleanup pass.</summary>
    Task CleanupAsync(CancellationToken cancellationToken);
}
