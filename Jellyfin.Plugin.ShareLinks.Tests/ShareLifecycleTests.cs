using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Data.Queries;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Entities.Security;
using Jellyfin.Plugin.ShareLinks.Models;
using Jellyfin.Plugin.ShareLinks.Services;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.SyncPlay;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.ShareLinks.Tests;

/// <summary>Creation, redemption and teardown wired together, with Jellyfin mocked out.</summary>
public sealed class ShareLifecycleTests : IDisposable
{
    private readonly TempStore _temp = new();
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<IUserManager> _users = new();
    private readonly Mock<IDeviceManager> _devices = new();
    private readonly Mock<ISessionManager> _sessions = new();
    private readonly Mock<ISyncPlayManager> _syncPlay = new();
    private readonly FakeTree _tree = new();
    private readonly User _host = TestSupport.Regular();
    private readonly ShareTokenService _tokens;
    private readonly ItemTagService _tags;
    private readonly JellyfinGuestUserService _guests;
    private readonly WatchPartyService _watchParty;
    private readonly ShareLinkCleanupService _cleanup;
    private readonly ShareLinkCreationService _creation;
    private readonly ShareLinkRedemptionService _redemption;
    private readonly Dictionary<string, User> _usersByName = new();

    public ShareLifecycleTests()
    {
        TestSupport.EnsurePlugin();
        _users.Setup(u => u.GetUserById(_host.Id)).Returns(_host);
        _users.Setup(u => u.GetUserByName(It.IsAny<string>())).Returns((string name) => _usersByName.GetValueOrDefault(name));
        _users.Setup(u => u.CreateUserAsync(It.IsAny<string>())).ReturnsAsync((string name) =>
        {
            var user = new User(name, "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider", "reset") { Id = Guid.NewGuid() };
            _usersByName[name] = user;
            _users.Setup(u => u.GetUserById(user.Id)).Returns(user);
            return user;
        });
        _devices.Setup(d => d.GetDevices(It.IsAny<DeviceQuery>())).Returns(new QueryResult<Device>(Array.Empty<Device>()));
        _sessions.SetupGet(s => s.Sessions).Returns(Array.Empty<SessionInfo>());
        _sessions.Setup(s => s.AuthenticateDirect(It.IsAny<AuthenticationRequest>())).ReturnsAsync((AuthenticationRequest request) => new AuthenticationResult
        {
            AccessToken = "token-" + Guid.NewGuid().ToString("N"),
            User = new UserDto { Id = request.UserId }
        });

        _tokens = new ShareTokenService(_temp.Paths, NullLogger<ShareTokenService>.Instance);
        _tags = new ItemTagService(_library.Object, _tree, NullLogger<ItemTagService>.Instance);
        _guests = new JellyfinGuestUserService(_users.Object, _devices.Object, _sessions.Object, _library.Object, NullLogger<JellyfinGuestUserService>.Instance);
        _watchParty = new WatchPartyService(_syncPlay.Object, _sessions.Object, _users.Object, _temp.Store, NullLogger<WatchPartyService>.Instance);
        _cleanup = new ShareLinkCleanupService(_temp.Store, _library.Object, _tags, _guests, _watchParty, NullLogger<ShareLinkCleanupService>.Instance);
        _creation = new ShareLinkCreationService(_temp.Store, _tokens, _tags, NullLogger<ShareLinkCreationService>.Instance);
        _redemption = new ShareLinkRedemptionService(_library.Object, _temp.Store, _tokens, _tags, _guests, _cleanup, _watchParty, _sessions.Object, NullLogger<ShareLinkRedemptionService>.Instance);
    }

    [Fact]
    public async Task CreationRecordsTheKindAndPartySettings()
    {
        var movie = Known(TestSupport.Item<Movie>());

        var (plain, _) = await _creation.CreateAsync(movie, _host.Id, 4, true, CancellationToken.None);
        Assert.Equal(ShareKind.Video, plain.Kind);
        Assert.False(plain.WatchParty);
        Assert.False(plain.WatchPartyHostOnly);

        var (party, _) = await _creation.CreateAsync(movie, _host.Id, 4, false, true, true, CancellationToken.None);
        Assert.True(party.WatchParty);
        Assert.True(party.WatchPartyHostOnly);
        Assert.Null(party.WatchPartyGroupId);

        // Host-only means nothing without a party, so it is never stored on its own.
        var (notParty, _) = await _creation.CreateAsync(movie, _host.Id, 4, false, false, true, CancellationToken.None);
        Assert.False(notParty.WatchPartyHostOnly);
    }

    [Fact]
    public async Task CreationRefusesWhatCannotBeSharedOrWatchedTogether()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _creation.CreateAsync(InLibrary(new Person()), _host.Id, 4, true, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => _creation.CreateAsync(InLibrary(new CollectionFolder()), _host.Id, 4, true, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => _creation.CreateAsync(InLibrary(new Photo()), _host.Id, 4, false, true, false, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => _creation.CreateAsync(new LiveTvChannel { Id = Guid.NewGuid() }, _host.Id, 4, false, true, false, CancellationToken.None));
        Assert.Empty(await _temp.Store.ListAsync());
    }

    [Fact]
    public async Task APlaylistGuestIsAddedToThePlaylistAndRemovedAgain()
    {
        var playlist = Known(InLibrary(new Playlist()));
        var song = Known(TestSupport.Item<MediaBrowser.Controller.Entities.Audio.Audio>());
        _tree.Linked(playlist, song);
        var (record, token) = await _creation.CreateAsync(playlist, _host.Id, 4, true, CancellationToken.None);
        Assert.Equal(ShareKind.Playlist, record.Kind);

        var result = await _redemption.RedeemAsync(token, Request(), CancellationToken.None);

        Assert.NotNull(result.Html);
        var saved = (await _temp.Store.GetByIdAsync(record.Id))!;
        var share = Assert.Single(playlist.Shares);
        Assert.Equal(saved.GuestUserId, share.UserId);
        Assert.False(share.CanEdit);
        Assert.Contains(record.AllowedTag!, song.Tags);

        _tree.Tagged.AddRange(new BaseItem[] { playlist, song });
        await _cleanup.RevokeAsync(record.Id, CancellationToken.None);

        Assert.Empty(playlist.Shares);
        Assert.DoesNotContain(record.AllowedTag!, song.Tags);
        Assert.DoesNotContain(record.AllowedTag!, playlist.Tags);
    }

    [Fact]
    public async Task AFolderOpensAsAListAndATitleOnItsPage()
    {
        var folder = Known(InLibrary(new Folder()));
        var (_, folderToken) = await _creation.CreateAsync(folder, _host.Id, 4, true, CancellationToken.None);
        Assert.StartsWith("/jf/web/index.html#/list?parentId=" + folder.Id.ToString("D"), Redirect((await _redemption.RedeemAsync(folderToken, Request(), CancellationToken.None)).Html!));

        var movie = Known(TestSupport.Item<Movie>());
        var (_, movieToken) = await _creation.CreateAsync(movie, _host.Id, 4, true, CancellationToken.None);
        Assert.StartsWith("/jf/web/index.html#/details?id=" + movie.Id.ToString("D"), Redirect((await _redemption.RedeemAsync(movieToken, Request(), CancellationToken.None)).Html!));
    }

    [Fact]
    public async Task APartyThatEndedCannotBeRedeemed()
    {
        var movie = Known(TestSupport.Item<Movie>());
        var (record, token) = await _creation.CreateAsync(movie, _host.Id, 4, false, true, false, CancellationToken.None);
        record.WatchPartyGroupId = Guid.NewGuid();
        await _temp.Store.UpdateAsync(record);
        _syncPlay.Setup(s => s.GetGroup(It.IsAny<SessionInfo>(), record.WatchPartyGroupId.Value)).Returns((GroupInfoDto)null!);

        var result = await _redemption.RedeemAsync(token, Request(), CancellationToken.None);

        Assert.Null(result.Html);
        Assert.Equal(ShareLinkStatus.Revoked, (await _temp.Store.GetByIdAsync(record.Id))!.Status);
        _sessions.Verify(s => s.AuthenticateDirect(It.IsAny<AuthenticationRequest>()), Times.Never);
    }

    [Fact]
    public async Task APartyWithoutAGroupIdIsNeverRedeemed()
    {
        var movie = Known(TestSupport.Item<Movie>());
        var (_, token) = await _creation.CreateAsync(movie, _host.Id, 4, false, true, false, CancellationToken.None);

        Assert.Null((await _redemption.RedeemAsync(token, Request(), CancellationToken.None)).Html);
    }

    [Fact]
    public async Task ALivePartyIsRedeemedAsAJoinOnlyGuest()
    {
        var movie = Known(TestSupport.Item<Movie>());
        var group = Guid.NewGuid();
        var (record, token) = await _creation.CreateAsync(movie, _host.Id, 4, false, true, true, CancellationToken.None);
        record.WatchPartyGroupId = group;
        await _temp.Store.UpdateAsync(record);
        _syncPlay.Setup(s => s.GetGroup(It.Is<SessionInfo>(p => p.UserId == _host.Id), group)).Returns(new GroupInfoDto(group, "p", GroupStateType.Idle, new[] { "host" }, DateTime.UtcNow));
        Jellyfin.Database.Implementations.Enums.SyncPlayUserAccessType? access = null;
        _users.Setup(u => u.UpdatePolicyAsync(It.IsAny<Guid>(), It.IsAny<MediaBrowser.Model.Users.UserPolicy>()))
            .Callback((Guid _, MediaBrowser.Model.Users.UserPolicy policy) => access = policy.SyncPlayAccess)
            .Returns(Task.CompletedTask);

        var result = await _redemption.RedeemAsync(token, Request(), CancellationToken.None);

        Assert.NotNull(result.Html);
        Assert.Equal(Jellyfin.Database.Implementations.Enums.SyncPlayUserAccessType.JoinGroups, access);
    }

    [Fact]
    public async Task OnlyPartiesWhoseGroupClosedAreEnded()
    {
        var open = Guid.NewGuid();
        var closed = Guid.NewGuid();
        _syncPlay.Setup(s => s.GetGroup(It.Is<SessionInfo>(p => p.UserId == _host.Id), open)).Returns(new GroupInfoDto(open, "p", GroupStateType.Playing, new[] { "host" }, DateTime.UtcNow));
        var running = Party(open);
        var finished = Party(closed);
        var ordinary = TestSupport.ActiveRecord(Guid.NewGuid(), r => r.CreatedByUserId = _host.Id);
        var expired = Party(closed, r => r.ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5));
        foreach (var record in new[] { running, finished, ordinary, expired })
        {
            await _temp.Store.UpsertAsync(record);
        }

        var ended = await _cleanup.EndFinishedWatchPartiesAsync(CancellationToken.None);

        Assert.Equal(1, ended);
        Assert.Equal(ShareLinkStatus.Revoked, (await _temp.Store.GetByIdAsync(finished.Id))!.Status);
        Assert.Equal(ShareLinkStatus.Redeemed, (await _temp.Store.GetByIdAsync(running.Id))!.Status);
        Assert.Equal(ShareLinkStatus.Redeemed, (await _temp.Store.GetByIdAsync(ordinary.Id))!.Status);
        Assert.Null((await _temp.Store.GetByIdAsync(finished.Id))!.ShareUrl);
    }

    [Fact]
    public async Task AChannelGetsItsTagBackAfterAGuideRefresh()
    {
        var channel = Known(new LiveTvChannel { Id = Guid.NewGuid(), Tags = new[] { "news" } });
        var record = TestSupport.ActiveRecord(Guid.NewGuid(), r =>
        {
            r.Kind = ShareKind.LiveTv;
            r.ItemId = channel.Id.ToString("D");
        });

        await _cleanup.HealLiveTvTagAsync(record, CancellationToken.None);
        Assert.Contains(record.AllowedTag!, channel.Tags);
        Assert.Contains("news", channel.Tags);

        var movie = Known(TestSupport.Item<Movie>());
        await _cleanup.HealLiveTvTagAsync(TestSupport.ActiveRecord(Guid.NewGuid(), r => r.ItemId = movie.Id.ToString("D")), CancellationToken.None);
        Assert.Empty(movie.Tags);

        var endedChannel = Known(new LiveTvChannel { Id = Guid.NewGuid() });
        await _cleanup.HealLiveTvTagAsync(
            TestSupport.ActiveRecord(Guid.NewGuid(), r =>
            {
                r.Kind = ShareKind.LiveTv;
                r.ItemId = endedChannel.Id.ToString("D");
                r.Status = ShareLinkStatus.Revoked;
            }),
            CancellationToken.None);
        Assert.Empty(endedChannel.Tags);
    }

    [Fact]
    public async Task TagsComeOffEvenWhenTheCollectionWasDeleted()
    {
        var collection = InLibrary(new BoxSet());
        var movie = Known(TestSupport.Item<Movie>());
        _tree.Linked(collection, movie);
        Known(collection);
        var (record, _) = await _creation.CreateAsync(collection, _host.Id, 4, true, CancellationToken.None);
        Assert.Contains(record.AllowedTag!, movie.Tags);

        _library.Setup(l => l.GetItemById(collection.Id)).Returns((BaseItem?)null);
        _tree.Tagged.Add(movie);
        await _cleanup.RevokeAsync(record.Id, CancellationToken.None);

        Assert.DoesNotContain(record.AllowedTag!, movie.Tags);
    }

    public void Dispose() => _temp.Dispose();

    private ShareLinkRecord Party(Guid group, Action<ShareLinkRecord>? configure = null)
    {
        return TestSupport.ActiveRecord(Guid.NewGuid(), r =>
        {
            r.CreatedByUserId = _host.Id;
            r.WatchParty = true;
            r.WatchPartyGroupId = group;
            r.ShareUrl = "https://example.invalid/ShareLinks/Redeem?t=x";
            configure?.Invoke(r);
        });
    }

    private T Known<T>(T item)
        where T : BaseItem
    {
        _library.Setup(l => l.GetItemById(item.Id)).Returns(item);
        return item;
    }

    private static T InLibrary<T>(T item)
        where T : BaseItem
    {
        item.Id = Guid.NewGuid();
        item.ParentId = Guid.NewGuid();
        return item;
    }

    private static HttpRequest Request()
    {
        var context = new DefaultHttpContext();
        context.Request.PathBase = "/jf";
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        return context.Request;
    }

    private static string Redirect(string html)
    {
        return JsonSerializer.Deserialize<string>(Regex.Match(html, @"const redirectUrl = (.+);").Groups[1].Value)!;
    }
}
