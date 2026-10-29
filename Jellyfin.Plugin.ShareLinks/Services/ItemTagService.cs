using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShareLinks.Services;

/// <summary>Applies and removes temporary tags on shared items.</summary>
public sealed class ItemTagService
{
    private readonly ILibraryManager _libraryManager;
    private readonly IItemTreeSource _treeSource;
    private readonly ILogger<ItemTagService> _logger;

    /// <summary>Initializes a new instance of the <see cref="ItemTagService"/> class.</summary>
    public ItemTagService(ILibraryManager libraryManager, IItemTreeSource treeSource, ILogger<ItemTagService> logger)
    {
        _libraryManager = libraryManager;
        _treeSource = treeSource;
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
    /// Ensures the supplied tag is present on the item and on everything the share
    /// covers below it (see <see cref="BuildTagTreeTargets"/>), so a guest can
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
    /// Removes the supplied tag from the item, from everything
    /// <see cref="EnsureTagTreeAsync"/> would have tagged, and from any other item
    /// still carrying it. The item may be null when it was deleted from the library.
    /// </summary>
    public async Task<bool> RemoveTagTreeAsync(BaseItem? item, string tag, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            throw new ArgumentException("Tag cannot be empty.", nameof(tag));
        }

        var targets = BuildTagRemovalTargets(item, tag);
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
            item?.Id,
            item?.Name);
        return changed;
    }

    /// <summary>
    /// The items that carry a share's tag: the shared item, everything physically
    /// under it, and for a collection, playlist or artist the items it links to and
    /// everything under those in turn.
    /// </summary>
    /// <remarks>
    /// Never the parents. Jellyfin's <c>GetInheritedTags</c> is an item's own tags
    /// plus every ancestor's, and the guest's AllowedTags policy is matched against
    /// that, so tagging a season's parent series would hand the guest every other
    /// season of that series as well. For the same reason a library root, a view or
    /// a plugin folder is never tagged, even when a collection links to one.
    /// </remarks>
    public List<BaseItem> BuildTagTreeTargets(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var targets = new List<BaseItem>();
        var seen = new HashSet<Guid>();
        var pending = new Stack<BaseItem>();
        pending.Push(item);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (ShareableItemPolicy.IsStructural(current) || !seen.Add(current.Id))
            {
                continue;
            }

            targets.Add(current);

            if (current is MusicArtist artist)
            {
                foreach (var media in _treeSource.GetArtistMedia(artist))
                {
                    pending.Push(media);
                }
            }

            if (current is not Folder folder)
            {
                continue;
            }

            foreach (var child in _treeSource.GetPhysicalDescendants(folder))
            {
                if (!ShareableItemPolicy.IsStructuralType(child) && seen.Add(child.Id))
                {
                    targets.Add(child);
                }
            }

            // Linked children are not below the collection or playlist in the
            // library, so nothing reaches them through inheritance. Each one is
            // walked in full: a series in a collection needs its episodes too, and
            // a collection nested in another one its own members. The seen set
            // stops a collection that links back to itself.
            foreach (var linked in _treeSource.GetLinkedChildren(folder))
            {
                pending.Push(linked);
            }
        }

        return targets;
    }

    /// <summary>
    /// Removal reaches further than tagging does. Taking a tag off can only ever
    /// remove access, so it goes everywhere the tag is found: items dropped from a
    /// collection or playlist since it was shared, and the parent series that
    /// builds before 1.0.3 tagged along with a shared season.
    /// </summary>
    private List<BaseItem> BuildTagRemovalTargets(BaseItem? item, string tag)
    {
        var targets = item is null ? new List<BaseItem>() : BuildTagTreeTargets(item);
        var seen = new HashSet<Guid>(targets.Select(target => target.Id));

        if (item is Season season)
        {
            var series = season.Series ?? season.GetParent() as Series;
            if (series is not null && seen.Add(series.Id))
            {
                targets.Add(series);
            }
        }

        foreach (var tagged in _treeSource.GetItemsWithTag(tag))
        {
            if (seen.Add(tagged.Id))
            {
                targets.Add(tagged);
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
