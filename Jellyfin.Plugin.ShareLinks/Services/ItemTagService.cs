using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShareLinks.Services;

/// <summary>Applies and removes temporary tags on shared items.</summary>
public sealed class ItemTagService
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ItemTagService> _logger;

    /// <summary>Initializes a new instance of the <see cref="ItemTagService"/> class.</summary>
    public ItemTagService(ILibraryManager libraryManager, ILogger<ItemTagService> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>Ensures the supplied tag is present on the item and persisted.</summary>
    public async Task<bool> EnsureTagAsync(BaseItem item, string tag, CancellationToken cancellationToken)
    {
        if (item is null)
        {
            throw new ArgumentNullException(nameof(item));
        }

        if (string.IsNullOrWhiteSpace(tag))
        {
            throw new ArgumentException("Tag cannot be empty.", nameof(tag));
        }

        var tags = item.Tags?.ToList() ?? new List<string>();
        if (tags.Any(existing => string.Equals(existing, tag, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        tags.Add(tag);
        item.Tags = tags.ToArray();
        await PersistAsync(item, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("ShareLinks: applied temporary tag {Tag} to item {ItemId}.", tag, item.Id);
        return true;
    }

    /// <summary>Removes the supplied tag from the item and persists the change.</summary>
    public async Task<bool> RemoveTagAsync(BaseItem item, string tag, CancellationToken cancellationToken)
    {
        if (item is null)
        {
            throw new ArgumentNullException(nameof(item));
        }

        if (string.IsNullOrWhiteSpace(tag))
        {
            throw new ArgumentException("Tag cannot be empty.", nameof(tag));
        }

        var tags = item.Tags?.ToList() ?? new List<string>();
        var removed = tags.RemoveAll(existing => string.Equals(existing, tag, StringComparison.OrdinalIgnoreCase)) > 0;
        if (!removed)
        {
            return false;
        }

        item.Tags = tags.ToArray();
        await PersistAsync(item, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("ShareLinks: removed temporary tag {Tag} from item {ItemId}.", tag, item.Id);
        return true;
    }

    /// <summary>
    /// Ensures the supplied tag is present on the item and, when the item is a folder
    /// such as a series or a season, on everything underneath it, so a guest can
    /// browse down through the shared branch instead of only seeing the single node
    /// the link was created on.
    /// </summary>
    public async Task<bool> EnsureTagTreeAsync(BaseItem item, string tag, CancellationToken cancellationToken)
    {
        if (item is null)
        {
            throw new ArgumentNullException(nameof(item));
        }

        var targets = BuildTagTreeTargets(item);
        var changed = false;
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            changed |= await EnsureTagAsync(target, tag, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "ShareLinks: ensured tag {Tag} across {Count} item(s) rooted at {ItemId} \"{ItemName}\".",
            tag,
            targets.Count,
            item.Id,
            item.Name);
        return changed;
    }

    /// <summary>
    /// Removes the supplied tag from the item and, when it is a folder, from
    /// everything underneath it (mirrors <see cref="EnsureTagTreeAsync"/>).
    /// </summary>
    public async Task<bool> RemoveTagTreeAsync(BaseItem item, string tag, CancellationToken cancellationToken)
    {
        if (item is null)
        {
            throw new ArgumentNullException(nameof(item));
        }

        var targets = BuildTagRemovalTargets(item);
        var changed = false;
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            changed |= await RemoveTagAsync(target, tag, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "ShareLinks: removed tag {Tag} across {Count} item(s) rooted at {ItemId} \"{ItemName}\".",
            tag,
            targets.Count,
            item.Id,
            item.Name);
        return changed;
    }

    /// <summary>
    /// The items that carry a share's tag: the shared item itself and, when it is a
    /// folder, everything under it.
    /// </summary>
    /// <remarks>
    /// Never the parents. Jellyfin's <c>GetInheritedTags</c> is an item's own tags
    /// plus every ancestor's, and the guest's AllowedTags policy is matched against
    /// that, so tagging a season's parent series would hand the guest every other
    /// season of that series as well.
    /// </remarks>
    private static List<BaseItem> BuildTagTreeTargets(BaseItem item)
    {
        var targets = new List<BaseItem> { item };

        if (item is Folder folder)
        {
            targets.AddRange(folder.GetRecursiveChildren());
        }

        return targets;
    }

    /// <summary>
    /// Removal deliberately reaches one level further than tagging does: up to a
    /// season's parent series. Builds before this fix put the share's tag on that
    /// series, and taking a tag off can only ever remove access, so cleaning those
    /// up is safe where applying them was not.
    /// </summary>
    private static List<BaseItem> BuildTagRemovalTargets(BaseItem item)
    {
        var targets = BuildTagTreeTargets(item);

        if (item is Season season)
        {
            var series = season.Series ?? season.GetParent() as Series;
            if (series is not null)
            {
                targets.Add(series);
            }
        }

        return targets;
    }

    private async Task PersistAsync(BaseItem item, CancellationToken cancellationToken)
    {
        var parent = item.DisplayParent ?? item;
        await _libraryManager.UpdateItemAsync(item, parent, ItemUpdateType.None, cancellationToken).ConfigureAwait(false);
    }
}
