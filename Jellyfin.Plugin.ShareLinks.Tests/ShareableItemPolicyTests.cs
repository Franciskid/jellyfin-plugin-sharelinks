using Jellyfin.Plugin.ShareLinks.Models;
using Jellyfin.Plugin.ShareLinks.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Playlists;
using Xunit;

namespace Jellyfin.Plugin.ShareLinks.Tests;

public sealed class ShareableItemPolicyTests
{
    public ShareableItemPolicyTests()
    {
        TestSupport.EnsurePlugin();
    }

    public static TheoryData<BaseItem, ShareKind> Shareable() => new()
    {
        { InLibrary(new Movie()), ShareKind.Video },
        { InLibrary(new Series()), ShareKind.Video },
        { InLibrary(new Season()), ShareKind.Video },
        { InLibrary(new Episode()), ShareKind.Video },
        { InLibrary(new Video()), ShareKind.Video },
        { InLibrary(new MusicVideo()), ShareKind.Video },
        { InLibrary(new Trailer()), ShareKind.Video },
        { InLibrary(new Audio()), ShareKind.Music },
        { InLibrary(new MusicAlbum()), ShareKind.Music },
        { new MusicArtist { Id = Guid.NewGuid() }, ShareKind.Music },
        { InLibrary(new AudioBook()), ShareKind.Music },
        { InLibrary(new Photo()), ShareKind.Photo },
        { InLibrary(new PhotoAlbum()), ShareKind.Photo },
        { InLibrary(new Book()), ShareKind.Book },
        { InLibrary(new BoxSet()), ShareKind.Collection },
        { InLibrary(new Playlist()), ShareKind.Playlist },
        { InLibrary(new Folder()), ShareKind.Folder },
        { new LiveTvChannel { Id = Guid.NewGuid() }, ShareKind.LiveTv }
    };

    [Theory]
    [MemberData(nameof(Shareable))]
    public void ClassifiesShareableItems(BaseItem item, ShareKind expected)
    {
        Assert.Equal(expected, ShareableItemPolicy.Classify(item));
    }

    public static TheoryData<BaseItem> NotShareable() => new()
    {
        // Containers whose tag every item below them would inherit.
        InLibrary(new CollectionFolder()),
        InLibrary(new UserRootFolder()),
        new AggregateFolder { Id = Guid.NewGuid() },
        InLibrary(new UserView()),
        InLibrary(new TestPluginFolder()),
        InLibrary(new MediaBrowser.Controller.Channels.Channel()),
        // A Folder subclass nobody has looked at yet is refused, not treated as a folder.
        InLibrary(new UnknownFolder()),
        // A plain folder with no parent is a root of some kind.
        new Folder { Id = Guid.NewGuid() },
        // Things that are not media.
        InLibrary(new Person()),
        InLibrary(new Genre()),
        InLibrary(new MusicGenre()),
        InLibrary(new Studio()),
        InLibrary(new Year()),
        // A programme, rather than its channel.
        new LiveTvProgram { Id = Guid.NewGuid() },
    };

    [Theory]
    [MemberData(nameof(NotShareable))]
    public void RefusesEverythingElse(BaseItem item)
    {
        Assert.Null(ShareableItemPolicy.Classify(item));
        Assert.False(ShareableItemPolicy.IsShareable(item));
    }

    [Fact]
    public void RefusesMissingItemsAndPluginChannelContent()
    {
        Assert.Null(ShareableItemPolicy.Classify(null));

        var missing = InLibrary(new Episode());
        missing.IsVirtualItem = true;
        Assert.Null(ShareableItemPolicy.Classify(missing));

        // A movie served by a plugin channel is fetched on demand from outside the
        // library, so the tag policy has nothing to hold on to.
        var fromChannel = InLibrary(new Movie());
        fromChannel.ChannelId = Guid.NewGuid();
        Assert.Null(ShareableItemPolicy.Classify(fromChannel));
    }

    [Fact]
    public void ALibrarysOwnFolderIsRefused()
    {
        // On disk a library is a plain Folder whose parent is the server's root.
        var root = new AggregateFolder { Id = Guid.NewGuid() };
        TestSupport.StaticLibrary.Setup(l => l.GetItemById(root.Id)).Returns(root);
        var libraryFolder = new Folder { Id = Guid.NewGuid(), ParentId = root.Id };

        Assert.Null(ShareableItemPolicy.Classify(libraryFolder));
        Assert.True(ShareableItemPolicy.IsStructural(libraryFolder));

        var subFolder = new Folder { Id = Guid.NewGuid(), ParentId = libraryFolder.Id };
        TestSupport.StaticLibrary.Setup(l => l.GetItemById(libraryFolder.Id)).Returns(libraryFolder);
        Assert.Equal(ShareKind.Folder, ShareableItemPolicy.Classify(subFolder));
    }

    [Theory]
    [InlineData(ShareKind.Video, true)]
    [InlineData(ShareKind.Music, true)]
    [InlineData(ShareKind.Collection, true)]
    [InlineData(ShareKind.Playlist, true)]
    [InlineData(ShareKind.Folder, true)]
    [InlineData(ShareKind.Photo, false)]
    [InlineData(ShareKind.Book, false)]
    [InlineData(ShareKind.LiveTv, false)]
    public void OnlyPlayableKindsCanBeWatchedTogether(ShareKind kind, bool expected)
    {
        Assert.Equal(expected, ShareableItemPolicy.SupportsWatchParty(kind));
    }

    [Fact]
    public void StructuralItemsAreRecognised()
    {
        Assert.True(ShareableItemPolicy.IsStructural(new CollectionFolder()));
        Assert.True(ShareableItemPolicy.IsStructural(new UserView()));
        Assert.True(ShareableItemPolicy.IsStructural(new TestPluginFolder()));
        Assert.False(ShareableItemPolicy.IsStructural(new Folder()));
        Assert.False(ShareableItemPolicy.IsStructural(new BoxSet()));
    }

    private static T InLibrary<T>(T item)
        where T : BaseItem
    {
        item.Id = Guid.NewGuid();
        item.ParentId = Guid.NewGuid();
        return item;
    }

    private sealed class TestPluginFolder : BasePluginFolder
    {
    }

    private sealed class UnknownFolder : Folder
    {
    }
}
