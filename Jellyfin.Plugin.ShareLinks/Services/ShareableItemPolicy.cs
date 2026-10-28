using System;
using Jellyfin.Plugin.ShareLinks.Models;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ShareLinks.Services;

/// <summary>
/// Decides which items can be shared and what kind of share they make.
/// </summary>
/// <remarks>
/// Anything not named here is refused, including types Jellyfin adds later. The
/// structural checks come first: a library root, a user view or a plugin channel
/// folder would put the share tag on something whose tag every item below it
/// inherits, which hands over a whole library.
/// </remarks>
public static class ShareableItemPolicy
{
    /// <summary>Returns the kind of share an item makes, or null when it cannot be shared.</summary>
    public static ShareKind? Classify(BaseItem? item)
    {
        if (item is null || item.IsVirtualItem)
        {
            return null;
        }

        // Live TV channels are not library items, so they are let through before the
        // library check below. Programmes are not: share the channel instead.
        if (item is LiveTvChannel)
        {
            return ShareKind.LiveTv;
        }

        if (IsStructural(item) || item.SourceType != SourceType.Library)
        {
            return null;
        }

        return item switch
        {
            Playlist => ShareKind.Playlist,
            BoxSet => ShareKind.Collection,
            Movie or Series or Season or Episode or Video => ShareKind.Video,
            Audio or MusicAlbum or MusicArtist => ShareKind.Music,
            Photo or PhotoAlbum => ShareKind.Photo,
            Book => ShareKind.Book,
            // Only a plain folder. Subclasses of Folder are roots, views and
            // virtual containers, and the structural check is the only thing
            // standing between them and a whole-library share.
            Folder folder when folder.GetType() == typeof(Folder) && !folder.ParentId.Equals(Guid.Empty) => ShareKind.Folder,
            _ => null
        };
    }

    /// <summary>Gets a value indicating whether an item can be shared at all.</summary>
    public static bool IsShareable(BaseItem? item) => Classify(item).HasValue;

    /// <summary>
    /// Gets a value indicating whether a kind can be watched together. SyncPlay
    /// plays queues of audio and video, so photos, books and live channels are out.
    /// </summary>
    public static bool SupportsWatchParty(ShareKind kind)
    {
        return kind is ShareKind.Video or ShareKind.Music or ShareKind.Collection or ShareKind.Playlist or ShareKind.Folder;
    }

    /// <summary>
    /// Gets a value indicating whether an item must never carry a share tag, even
    /// when it turns up inside a collection or playlist being shared. Every item
    /// under one of these inherits its tags. That includes a library's own folder on
    /// disk: it is a plain Folder, but it sits right under the server's root and
    /// holds the whole library.
    /// </summary>
    public static bool IsStructural(BaseItem item)
    {
        return IsStructuralType(item) || item.IsTopParent;
    }

    /// <summary>
    /// The type-only half of <see cref="IsStructural"/>, for items already known to
    /// sit below something else, where looking the parent up again would be wasted.
    /// </summary>
    public static bool IsStructuralType(BaseItem item)
    {
        return item is ICollectionFolder
            or UserRootFolder
            or AggregateFolder
            or UserView
            or BasePluginFolder
            or Channel;
    }
}
