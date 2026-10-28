using Jellyfin.Plugin.ShareLinks.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.ShareLinks.Tests;

public sealed class ItemTagServiceTests
{
    private const string Tag = "sharelinks-0123456789abcdef0123456789abcdef";

    private readonly Mock<ILibraryManager> _library = new();
    private readonly FakeTree _tree = new();
    private readonly ItemTagService _service;

    public ItemTagServiceTests()
    {
        TestSupport.EnsurePlugin();
        _service = new ItemTagService(_library.Object, _tree, NullLogger<ItemTagService>.Instance);
    }

    [Fact]
    public void ASeasonTagsItsEpisodesButNeverItsSeries()
    {
        var series = TestSupport.Item<Series>();
        var season = TestSupport.Item<Season>();
        var otherSeason = TestSupport.Item<Season>();
        var episodes = new[] { TestSupport.Item<Episode>(), TestSupport.Item<Episode>() };
        _tree.Physical(series, season, otherSeason).Physical(series, episodes).Physical(season, episodes);

        var targets = _service.BuildTagTreeTargets(season).Select(item => item.Id).ToHashSet();

        Assert.Contains(season.Id, targets);
        Assert.All(episodes, episode => Assert.Contains(episode.Id, targets));
        Assert.DoesNotContain(series.Id, targets);
        Assert.DoesNotContain(otherSeason.Id, targets);
    }

    [Fact]
    public void ACollectionReachesIntoEverySeriesItHolds()
    {
        var collection = TestSupport.Item<BoxSet>();
        var movie = TestSupport.Item<Movie>();
        var series = TestSupport.Item<Series>();
        var season = TestSupport.Item<Season>();
        var episode = TestSupport.Item<Episode>();
        _tree.Linked(collection, movie, series).Physical(series, season, episode);

        var targets = _service.BuildTagTreeTargets(collection).Select(item => item.Id).ToList();

        Assert.Equal(new[] { collection.Id, movie.Id, series.Id, season.Id, episode.Id }.OrderBy(id => id), targets.OrderBy(id => id));
    }

    [Fact]
    public void NestedAndSelfReferencingCollectionsAreWalkedOnce()
    {
        var outer = TestSupport.Item<BoxSet>();
        var inner = TestSupport.Item<BoxSet>();
        var movie = TestSupport.Item<Movie>();
        _tree.Linked(outer, inner, movie).Linked(inner, outer, movie, inner);

        var targets = _service.BuildTagTreeTargets(outer);

        Assert.Equal(3, targets.Count);
        Assert.Equal(targets.Count, targets.Select(item => item.Id).Distinct().Count());
    }

    [Fact]
    public void ALibraryOrViewIsNeverTaggedEvenWhenLinked()
    {
        var collection = TestSupport.Item<BoxSet>();
        var library = TestSupport.Item<CollectionFolder>();
        var view = TestSupport.Item<UserView>();
        var root = TestSupport.Item<AggregateFolder>();
        var hidden = TestSupport.Item<Movie>();
        var movie = TestSupport.Item<Movie>();
        _tree.Linked(collection, library, view, root, movie)
            .Physical(library, hidden)
            .Physical(collection, TestSupport.Item<UserRootFolder>());

        var targets = _service.BuildTagTreeTargets(collection).Select(item => item.Id).ToHashSet();

        Assert.Equal(new HashSet<Guid> { collection.Id, movie.Id }, targets);
    }

    [Fact]
    public void APlaylistTagsItsEntriesAndWhatIsBelowThem()
    {
        var playlist = TestSupport.Item<Playlist>();
        var track = TestSupport.Item<Audio>();
        var album = TestSupport.Item<MusicAlbum>();
        var albumTrack = TestSupport.Item<Audio>();
        _tree.Linked(playlist, track, album).Physical(album, albumTrack);

        var targets = _service.BuildTagTreeTargets(playlist).Select(item => item.Id).ToHashSet();

        Assert.Equal(new HashSet<Guid> { playlist.Id, track.Id, album.Id, albumTrack.Id }, targets);
    }

    [Fact]
    public void AnArtistTagsTheirMediaAndTheirAlbumsTracks()
    {
        var artist = TestSupport.Item<MusicArtist>();
        var album = TestSupport.Item<MusicAlbum>();
        var albumTrack = TestSupport.Item<Audio>();
        var featuredOn = TestSupport.Item<Audio>();
        _tree.ArtistMedia(artist, album, featuredOn).Physical(album, albumTrack);

        var targets = _service.BuildTagTreeTargets(artist).Select(item => item.Id).ToHashSet();

        Assert.Equal(new HashSet<Guid> { artist.Id, album.Id, albumTrack.Id, featuredOn.Id }, targets);
    }

    [Fact]
    public void ASingleItemTagsOnlyItself()
    {
        var movie = TestSupport.Item<Movie>();
        Assert.Equal(new[] { movie.Id }, _service.BuildTagTreeTargets(movie).Select(item => item.Id));
    }

    [Fact]
    public async Task TaggingWritesOnlyWhatChanged()
    {
        var season = TestSupport.Item<Season>();
        var tagged = TestSupport.Item<Episode>();
        tagged.Tags = new[] { Tag.ToUpperInvariant() };
        var untagged = TestSupport.Item<Episode>();
        _tree.Physical(season, tagged, untagged);

        var changed = await _service.EnsureTagTreeAsync(season, Tag, CancellationToken.None);

        Assert.True(changed);
        Assert.Contains(Tag, season.Tags);
        Assert.Contains(Tag, untagged.Tags);
        Assert.Single(tagged.Tags);
        _library.Verify(l => l.UpdateItemAsync(tagged, It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()), Times.Never);
        _library.Verify(l => l.UpdateItemAsync(untagged, It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemovalFindsTheTagWhereverItWentAndKeepsOtherTags()
    {
        var collection = TestSupport.Item<BoxSet>();
        var stillMember = TestSupport.Item<Movie>();
        var removedSinceSharing = TestSupport.Item<Movie>();
        foreach (var item in new BaseItem[] { collection, stillMember, removedSinceSharing })
        {
            item.Tags = new[] { "family", Tag };
        }

        _tree.Linked(collection, stillMember);
        _tree.Tagged.AddRange(new BaseItem[] { collection, stillMember, removedSinceSharing });

        await _service.RemoveTagTreeAsync(collection, Tag, CancellationToken.None);

        foreach (var item in new BaseItem[] { collection, stillMember, removedSinceSharing })
        {
            Assert.Equal(new[] { "family" }, item.Tags);
        }
    }

    [Fact]
    public async Task RemovalStillWorksWhenTheSharedItemIsGone()
    {
        var orphan = TestSupport.Item<Movie>();
        orphan.Tags = new[] { Tag };
        _tree.Tagged.Add(orphan);

        await _service.RemoveTagTreeAsync(null, Tag, CancellationToken.None);

        Assert.Empty(orphan.Tags);
    }

    [Fact]
    public async Task RemovalCleansTheSeriesOldBuildsTaggedForASeason()
    {
        var series = TestSupport.Item<Series>();
        series.Tags = new[] { Tag };
        var season = TestSupport.Item<Season>();
        season.SeriesId = series.Id;
        TestSupport.StaticLibrary.Setup(l => l.GetItemById(series.Id)).Returns(series);

        await _service.RemoveTagTreeAsync(season, Tag, CancellationToken.None);

        Assert.Empty(series.Tags);
    }

    [Fact]
    public async Task AnEmptyTagIsRefused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.RemoveTagTreeAsync(TestSupport.Item<Movie>(), " ", CancellationToken.None));
    }
}
