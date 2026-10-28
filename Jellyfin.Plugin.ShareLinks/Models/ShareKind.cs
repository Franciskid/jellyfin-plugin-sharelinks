namespace Jellyfin.Plugin.ShareLinks.Models;

/// <summary>
/// What a share hands out. Decides the guest's policy (Live TV, downloads, SyncPlay)
/// and how the share's tag is spread. Stored on the record, so the values are part
/// of the on-disk format: records written before this existed read back as Video.
/// </summary>
public enum ShareKind
{
    /// <summary>A movie, series, season, episode, home video, music video, trailer or recording.</summary>
    Video = 0,

    /// <summary>A track, album, artist or audiobook.</summary>
    Music = 1,

    /// <summary>A photo or photo album.</summary>
    Photo = 2,

    /// <summary>A book. The reader needs the file itself, so these guests may download.</summary>
    Book = 3,

    /// <summary>A collection (BoxSet) and everything in it.</summary>
    Collection = 4,

    /// <summary>A playlist and everything in it.</summary>
    Playlist = 5,

    /// <summary>A plain folder inside a library, such as a home videos folder.</summary>
    Folder = 6,

    /// <summary>One Live TV channel.</summary>
    LiveTv = 7
}
