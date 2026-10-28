using System;
using System.Collections.Generic;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.ShareLinks.Services;

/// <summary>
/// The item relationships a share's tag follows. Kept behind an interface so the
/// rules about where the tag may and may not go can be tested without a library.
/// </summary>
public interface IItemTreeSource
{
    /// <summary>Everything physically below a folder, without its linked children.</summary>
    IEnumerable<BaseItem> GetPhysicalDescendants(Folder folder);

    /// <summary>The items a collection or playlist links to.</summary>
    IEnumerable<BaseItem> GetLinkedChildren(Folder folder);

    /// <summary>An artist's own albums and every track or music video credited to them.</summary>
    IEnumerable<BaseItem> GetArtistMedia(MusicArtist artist);

    /// <summary>Every item carrying a tag, wherever it is.</summary>
    IEnumerable<BaseItem> GetItemsWithTag(string tag);
}

/// <summary>Reads item relationships from Jellyfin's library.</summary>
public sealed class ItemTreeSource : IItemTreeSource
{
    private readonly ILibraryManager _libraryManager;

    /// <summary>Initializes a new instance of the <see cref="ItemTreeSource"/> class.</summary>
    public ItemTreeSource(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    /// <inheritdoc />
    public IEnumerable<BaseItem> GetPhysicalDescendants(Folder folder)
    {
        return folder.GetRecursiveChildren(null, false);
    }

    /// <inheritdoc />
    public IEnumerable<BaseItem> GetLinkedChildren(Folder folder)
    {
        return folder.GetLinkedChildren();
    }

    /// <inheritdoc />
    public IEnumerable<BaseItem> GetArtistMedia(MusicArtist artist)
    {
        // Albums only where the artist is the album artist. A compilation they
        // appear on once would otherwise carry the tag, and every other artist's
        // track on it would inherit it from the album.
        var albums = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.MusicAlbum },
            AlbumArtistIds = new[] { artist.Id },
            Recursive = true,
            DtoOptions = new DtoOptions(false)
        });

        var tracks = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Audio, BaseItemKind.MusicVideo },
            ArtistIds = new[] { artist.Id },
            Recursive = true,
            DtoOptions = new DtoOptions(false)
        });

        var all = new List<BaseItem>(albums.Count + tracks.Count);
        all.AddRange(albums);
        all.AddRange(tracks);
        return all;
    }

    /// <inheritdoc />
    public IEnumerable<BaseItem> GetItemsWithTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return Array.Empty<BaseItem>();
        }

        return _libraryManager.GetItemList(new InternalItemsQuery
        {
            Tags = new[] { tag },
            Recursive = true,
            DtoOptions = new DtoOptions(false)
        });
    }
}
