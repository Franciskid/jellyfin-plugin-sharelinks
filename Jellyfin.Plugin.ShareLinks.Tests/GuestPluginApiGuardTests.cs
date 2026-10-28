using System.Net;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Claims;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ShareLinks.Models;
using Jellyfin.Plugin.ShareLinks.Security;
using Jellyfin.Plugin.ShareLinks.Services;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay;
using MediaBrowser.Controller.SyncPlay.Requests;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.SyncPlay;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.ShareLinks.Tests;

/// <summary>
/// Runs the real MVC filter. Jellyfin's API is told apart from plugins by its
/// assembly name, so the controllers here live in a generated assembly that has it.
/// </summary>
public sealed class GuestPluginApiGuardTests : IDisposable
{
    private static readonly TypeInfo CoreController = MakeController("Jellyfin.Api");
    private static readonly TypeInfo PluginController = MakeController("Some.Other.Plugin");

    private readonly TempStore _temp = new();
    private readonly Mock<IUserManager> _users = new();
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<IPluginManager> _plugins = new();
    private readonly Mock<ISyncPlayManager> _syncPlay = new();
    private readonly Mock<ISessionManager> _sessions = new();
    private readonly WatchPartyService _watchParty;
    private readonly GuestPluginApiGuard _guard;
    private readonly User _guest = TestSupport.Guest();
    private readonly User _regular = TestSupport.Regular();
    private readonly Guid _partyGroup = Guid.NewGuid();

    public GuestPluginApiGuardTests()
    {
        TestSupport.EnsurePlugin();
        _users.Setup(u => u.GetUserById(_guest.Id)).Returns(_guest);
        _users.Setup(u => u.GetUserById(_regular.Id)).Returns(_regular);
        _plugins.SetupGet(p => p.Plugins).Returns(Array.Empty<LocalPlugin>());
        _sessions
            .Setup(s => s.LogSessionActivity(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<User>()))
            .ReturnsAsync((string _, string _, string _, string _, string _, User user) => new SessionInfo(_sessions.Object, NullLogger.Instance) { Id = "s-" + user.Id, UserId = user.Id });
        _watchParty = new WatchPartyService(_syncPlay.Object, _sessions.Object, _users.Object, _temp.Store, NullLogger<WatchPartyService>.Instance);
        _guard = new GuestPluginApiGuard(_users.Object, _library.Object, _plugins.Object, _temp.Store, _watchParty, NullLogger<GuestPluginApiGuard>.Instance);
    }

    [Fact]
    public async Task OrdinaryUsersAreNeverTouched()
    {
        var run = await Run(_regular, "SyncPlay", "SyncPlayJoinGroup", Args(("requestData", new JoinBody(Guid.NewGuid()))));
        Assert.True(run.NextCalled);

        run = await Run(_regular, "LiveTv", "GetTimers");
        Assert.True(run.NextCalled);

        run = await Run(_regular, "Session", "GetSessions", result: new OkObjectResult(new List<SessionInfoDto> { new() { DeviceId = "a" }, new() { DeviceId = "b" } }));
        Assert.Equal(2, ((IEnumerable<SessionInfoDto>)((ObjectResult)run.Result!).Value!).Count());
    }

    [Fact]
    public async Task APartyGuestJoinsOnlyThePartyGroup()
    {
        await SaveShare(party: true, hostOnly: false);

        var own = await Run(_guest, "SyncPlay", "SyncPlayJoinGroup", Args(("requestData", new JoinBody(_partyGroup))));
        Assert.True(own.NextCalled);

        var other = await Run(_guest, "SyncPlay", "SyncPlayJoinGroup", Args(("requestData", new JoinBody(Guid.NewGuid()))));
        Assert.False(other.NextCalled);
        Assert.Equal(403, Status(other));

        var peek = await Run(_guest, "SyncPlay", "SyncPlayGetGroup", Args(("id", Guid.NewGuid())));
        Assert.False(peek.NextCalled);
        Assert.IsType<NotFoundResult>(peek.Result);
    }

    [Fact]
    public async Task TheGroupListOnlyShowsTheGuestsOwnGroups()
    {
        await SaveShare(party: true, hostOnly: false);
        var groups = new List<GroupInfoDto> { Group(_partyGroup), Group(Guid.NewGuid()), Group(Guid.NewGuid()) };

        var run = await Run(_guest, "SyncPlay", "SyncPlayGetGroups", result: new OkObjectResult(groups));

        var shown = Assert.IsAssignableFrom<IEnumerable<GroupInfoDto>>(((ObjectResult)run.Result!).Value);
        Assert.Equal(_partyGroup, Assert.Single(shown).GroupId);
    }

    [Fact]
    public async Task AGroupAGuestCreatesIsOpenToTheirShareOnly()
    {
        var share = await SaveShare(party: false, hostOnly: false);
        var created = Guid.NewGuid();

        var create = await Run(_guest, "SyncPlay", "SyncPlayCreateGroup", result: new OkObjectResult(Group(created)));
        Assert.True(create.NextCalled);

        // A second viewer of the same multi-use link is the same account.
        var join = await Run(_guest, "SyncPlay", "SyncPlayJoinGroup", Args(("requestData", new JoinBody(created))));
        Assert.True(join.NextCalled);

        // A guest of another share cannot reach it.
        var stranger = TestSupport.Guest();
        _users.Setup(u => u.GetUserById(stranger.Id)).Returns(stranger);
        await _temp.Store.UpsertAsync(TestSupport.ActiveRecord(stranger.Id));
        var intrude = await Run(stranger, "SyncPlay", "SyncPlayJoinGroup", Args(("requestData", new JoinBody(created))));
        Assert.False(intrude.NextCalled);
        Assert.Equal(403, Status(intrude));

        Assert.True(_watchParty.IsGroupAllowed(share, created));
    }

    [Fact]
    public async Task APartyGuestCannotOpenAGroup()
    {
        await SaveShare(party: true, hostOnly: false);
        var run = await Run(_guest, "SyncPlay", "SyncPlayCreateGroup");
        Assert.False(run.NextCalled);
        Assert.Equal(403, Status(run));
    }

    [Fact]
    public async Task HostOnlyGuestsFollowButDoNotSteer()
    {
        await SaveShare(party: true, hostOnly: true);

        var pause = await Run(_guest, "SyncPlay", "SyncPlayPause");
        Assert.False(pause.NextCalled);
        Assert.Equal(403, Status(pause));

        var seek = await Run(_guest, "SyncPlay", "SyncPlaySeek");
        Assert.Equal(403, Status(seek));

        var ready = await Run(_guest, "SyncPlay", "SyncPlayReady");
        Assert.True(ready.NextCalled);

        var stop = await Run(_guest, "SyncPlay", "SyncPlayStop");
        Assert.False(stop.NextCalled);
        Assert.IsType<NoContentResult>(stop.Result);
        _syncPlay.Verify(s => s.LeaveGroup(It.Is<SessionInfo>(x => x.UserId == _guest.Id), It.IsAny<LeaveGroupRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AGuestWhoseShareEndedIsKeptOutOfSyncPlay()
    {
        await SaveShare(party: true, hostOnly: false, r => r.Status = ShareLinkStatus.Revoked);

        var join = await Run(_guest, "SyncPlay", "SyncPlayJoinGroup", Args(("requestData", new JoinBody(_partyGroup))));
        Assert.Equal(403, Status(join));

        var leave = await Run(_guest, "SyncPlay", "SyncPlayLeaveGroup");
        Assert.True(leave.NextCalled);
    }

    [Fact]
    public async Task AGuestOnlySeesItsOwnSession()
    {
        await SaveShare(party: false, hostOnly: false);
        var sessions = new List<SessionInfoDto>
        {
            new() { DeviceId = "sharelinks-me", UserId = _guest.Id, RemoteEndPoint = "203.0.113.1" },
            new() { DeviceId = "sharelinks-other-viewer", UserId = _guest.Id, RemoteEndPoint = "198.51.100.7" },
            new() { DeviceId = "anonymous", UserId = Guid.Empty, RemoteEndPoint = "192.0.2.4" }
        };

        var run = await Run(_guest, "Session", "GetSessions", result: new OkObjectResult(sessions), deviceId: "sharelinks-me");

        var shown = Assert.IsAssignableFrom<IEnumerable<SessionInfoDto>>(((ObjectResult)run.Result!).Value);
        Assert.Equal("sharelinks-me", Assert.Single(shown).DeviceId);

        var steer = await Run(_guest, "Session", "SendPlaystateCommand", Args(("sessionId", "other")), deviceId: "sharelinks-me");
        Assert.Equal(403, Status(steer));
        var message = await Run(_guest, "Session", "SendMessageCommand", Args(("sessionId", "other")), deviceId: "sharelinks-me");
        Assert.Equal(403, Status(message));
    }

    [Fact]
    public async Task AGuestOnlySeesItsOwnAccount()
    {
        var users = new List<UserDto> { new() { Id = _guest.Id }, new() { Id = _regular.Id }, new() { Id = Guid.NewGuid() } };
        var list = await Run(_guest, "User", "GetUsers", result: new OkObjectResult(users));
        Assert.Equal(_guest.Id, Assert.Single(Assert.IsAssignableFrom<IEnumerable<UserDto>>(((ObjectResult)list.Result!).Value)).Id);

        var other = await Run(_guest, "User", "GetUserById", Args(("userId", _regular.Id)));
        Assert.False(other.NextCalled);
        Assert.IsType<NotFoundResult>(other.Result);

        var self = await Run(_guest, "User", "GetUserById", Args(("userId", _guest.Id)));
        Assert.True(self.NextCalled);
    }

    [Fact]
    public async Task LiveTvSchedulesStayPrivate()
    {
        var timers = await Run(_guest, "LiveTv", "GetTimers");
        Assert.Equal(403, Status(timers));

        var channels = await Run(_guest, "LiveTv", "GetLiveTvChannels");
        Assert.True(channels.NextCalled);
    }

    [Fact]
    public async Task ALiveStreamOpensOnlyForASharedChannel()
    {
        var shared = new LiveTvChannel { Id = Guid.NewGuid() };
        var other = new LiveTvChannel { Id = Guid.NewGuid() };
        Known(shared, guestCanOpen: true);
        Known(other, guestCanOpen: false);
        await SaveShare(party: false, hostOnly: false, r =>
        {
            r.Kind = ShareKind.LiveTv;
            r.ItemId = shared.Id.ToString("D");
        });
        var provider = Guid.NewGuid().ToString("N");

        var ok = await Run(_guest, "MediaInfo", "OpenLiveStream", Args(
            ("openToken", $"{provider}_LiveTvChannel_{shared.Id:N}_src"),
            ("itemId", shared.Id),
            ("userId", _guest.Id),
            ("playSessionId", Guid.NewGuid().ToString("N"))));
        Assert.True(ok.NextCalled);

        // The token is what Jellyfin opens; naming a shared item beside it changes nothing.
        var forged = await Run(_guest, "MediaInfo", "OpenLiveStream", Args(
            ("openToken", $"{provider}_LiveTvChannel_{other.Id:N}_src"),
            ("itemId", shared.Id)));
        Assert.Equal(403, Status(forged));

        var dashed = await Run(_guest, "MediaInfo", "OpenLiveStream", Args(("openToken", $"{provider}_LiveTvChannel_{other.Id:D}_src")));
        Assert.Equal(403, Status(dashed));

        var inBody = await Run(_guest, "MediaInfo", "OpenLiveStream", Args(("openLiveStreamDto", new LiveStreamBody { OpenToken = $"{provider}_LiveTvChannel_{other.Id:N}_src" })));
        Assert.Equal(403, Status(inBody));

        var nothing = await Run(_guest, "MediaInfo", "OpenLiveStream", Args(("openToken", "garbage")));
        Assert.Equal(403, Status(nothing));
    }

    [Fact]
    public async Task NoGuestReachesAChannelThroughItsId()
    {
        // Jellyfin's own tag check lets every channel through (a channel's parent is
        // a view), so the guest "can open" both here, as it can on a real server.
        var shared = new LiveTvChannel { Id = Guid.NewGuid() };
        var other = new LiveTvChannel { Id = Guid.NewGuid() };
        Known(shared, guestCanOpen: true);
        Known(other, guestCanOpen: true);

        // A movie guest.
        await SaveShare(party: false, hostOnly: false);
        Assert.Equal(403, Status(await Run(_guest, "UserLibrary", "GetItem", Args(("itemId", other.Id)))));
        Assert.Equal(403, Status(await Run(_guest, "MediaInfo", "GetPostedPlaybackInfo", Args(("itemId", other.Id)))));
        Assert.Equal(403, Status(await Run(_guest, "Items", "GetItems", Args(("ids", new[] { other.Id })))));
        Assert.Equal(403, Status(await Run(_guest, "Videos", "GetVideoStream", Args(("itemId", Guid.NewGuid()), ("mediaSourceId", other.Id.ToString("N"))))));
    }

    [Fact]
    public async Task AChannelGuestReachesOnlyItsChannel()
    {
        var shared = new LiveTvChannel { Id = Guid.NewGuid() };
        var other = new LiveTvChannel { Id = Guid.NewGuid() };
        Known(shared, guestCanOpen: true);
        Known(other, guestCanOpen: true);
        await SaveShare(party: false, hostOnly: false, r =>
        {
            r.Kind = ShareKind.LiveTv;
            r.ItemId = shared.Id.ToString("D");
        });

        Assert.True((await Run(_guest, "LiveTv", "GetChannel", Args(("channelId", shared.Id)))).NextCalled);
        Assert.Equal(403, Status(await Run(_guest, "LiveTv", "GetChannel", Args(("channelId", other.Id)))));
        Assert.Equal(403, Status(await Run(_guest, "UserLibrary", "GetItem", Args(("itemId", other.Id)))));

        var provider = Guid.NewGuid().ToString("N");
        var forged = await Run(_guest, "MediaInfo", "OpenLiveStream", Args(("openToken", $"{provider}_LiveTvChannel_{other.Id:N}_src"), ("itemId", shared.Id)));
        Assert.Equal(403, Status(forged));
        var own = await Run(_guest, "MediaInfo", "OpenLiveStream", Args(("openToken", $"{provider}_LiveTvChannel_{shared.Id:N}_src"), ("itemId", shared.Id)));
        Assert.True(own.NextCalled);

        var otherProgramme = new LiveTvProgram { Id = Guid.NewGuid(), ChannelId = other.Id };
        var ownProgramme = new LiveTvProgram { Id = Guid.NewGuid(), ChannelId = shared.Id };
        Known(otherProgramme, guestCanOpen: true);
        Known(ownProgramme, guestCanOpen: true);
        Assert.Equal(403, Status(await Run(_guest, "LiveTv", "GetProgram", Args(("programId", otherProgramme.Id.ToString("N"))))));
        Assert.Equal(403, Status(await Run(_guest, "UserLibrary", "GetItem", Args(("itemId", otherProgramme.Id)))));
        Assert.True((await Run(_guest, "UserLibrary", "GetItem", Args(("itemId", ownProgramme.Id)))).NextCalled);
    }

    [Fact]
    public async Task AnEmptyOrUnknownIdInATokenIsRefusedCleanly()
    {
        _library.Setup(l => l.GetItemById(Guid.Empty)).Throws<ArgumentException>();
        var run = await Run(_guest, "MediaInfo", "OpenLiveStream", Args(("openToken", $"{Guid.Empty:N}_LiveTvChannel_{Guid.NewGuid():N}_x")));
        Assert.Equal(403, Status(run));
    }

    [Fact]
    public async Task TheAlbumOfAPlayableTrackComesBackAsAStandIn()
    {
        var album = TestSupport.Item<MusicAlbum>("Shared Album");
        album.NormalizationGain = -6.5f;
        album.Overview = "should not leak";
        var otherAlbum = TestSupport.Item<MusicAlbum>("Other Album");
        _library.Setup(l => l.GetItemById(album.Id)).Returns(album);
        _library.Setup(l => l.GetItemById(otherAlbum.Id)).Returns(otherAlbum);
        _library.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ParentId == album.Id && q.User == _guest)))
            .Returns(new List<BaseItem> { TestSupport.Item<MediaBrowser.Controller.Entities.Audio.Audio>() });
        _library.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ParentId == otherAlbum.Id)))
            .Returns(new List<BaseItem>());

        var run = await Run(_guest, "UserLibrary", "GetItem", Args(("itemId", album.Id)), result: new NotFoundResult());
        var dto = Assert.IsType<BaseItemDto>(Assert.IsType<OkObjectResult>(run.Executed!.Result).Value);
        Assert.Equal(album.Id, dto.Id);
        Assert.Equal(-6.5f, dto.NormalizationGain);
        Assert.Null(dto.Overview);
        Assert.Null(dto.ChildCount);

        // An album with nothing the guest can play stays a 404.
        var other = await Run(_guest, "UserLibrary", "GetItemLegacy", Args(("itemId", otherAlbum.Id)), result: new NotFoundResult());
        Assert.IsType<NotFoundResult>(other.Executed!.Result);

        // Anything that is not an album stays a 404 too.
        var movie = TestSupport.Item<Movie>();
        _library.Setup(l => l.GetItemById(movie.Id)).Returns(movie);
        var notAlbum = await Run(_guest, "UserLibrary", "GetItem", Args(("itemId", movie.Id)), result: new NotFoundResult());
        Assert.IsType<NotFoundResult>(notAlbum.Executed!.Result);
    }

    [Fact]
    public async Task IntrosAreAlwaysEmptyForGuests()
    {
        var run = await Run(_guest, "UserLibrary", "GetIntros");
        Assert.False(run.NextCalled);
        var page = Assert.IsType<QueryResult<BaseItemDto>>(((ObjectResult)run.Result!).Value);
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task UnsharedPlayableItemsAreDroppedFromResponses()
    {
        var shared = TestSupport.Item<Movie>();
        var hidden = TestSupport.Item<Movie>();
        Known(shared, guestCanOpen: true);
        Known(hidden, guestCanOpen: false);
        var page = new QueryResult<BaseItemDto>(new[]
        {
            new BaseItemDto { Id = shared.Id, MediaType = Jellyfin.Data.Enums.MediaType.Video },
            new BaseItemDto { Id = hidden.Id, MediaType = Jellyfin.Data.Enums.MediaType.Video }
        });

        var run = await Run(_guest, "Items", "GetItems", result: new OkObjectResult(page));

        Assert.Equal(shared.Id, Assert.Single(page.Items).Id);
        Assert.True(run.NextCalled);
    }

    [Fact]
    public async Task OtherPluginsAreBlockedForGuests()
    {
        var run = await Run(_guest, "Thing", "Get", controller: PluginController);
        Assert.False(run.NextCalled);
        Assert.Equal(403, Status(run));

        var regular = await Run(_regular, "Thing", "Get", controller: PluginController);
        Assert.True(regular.NextCalled);
    }

    public void Dispose() => _temp.Dispose();

    private async Task<ShareLinkRecord> SaveShare(bool party, bool hostOnly, Action<ShareLinkRecord>? configure = null)
    {
        var record = TestSupport.ActiveRecord(_guest.Id, r =>
        {
            r.WatchParty = party;
            r.WatchPartyGroupId = party ? _partyGroup : null;
            r.WatchPartyHostOnly = party && hostOnly;
            configure?.Invoke(r);
        });
        await _temp.Store.UpsertAsync(record);
        return record;
    }

    private void Known(BaseItem item, bool guestCanOpen)
    {
        _library.Setup(l => l.GetItemById(item.Id)).Returns(item);
        _library.Setup(l => l.GetItemById<BaseItem>(item.Id, It.Is<User>(u => u.Id == _guest.Id))).Returns(guestCanOpen ? item : null);
    }

    private static GroupInfoDto Group(Guid id) => new(id, "group", GroupStateType.Idle, new[] { "someone" }, DateTime.UtcNow);

    private static Dictionary<string, object?> Args(params (string Name, object? Value)[] values)
    {
        return values.ToDictionary(v => v.Name, v => v.Value);
    }

    private static int? Status(RunResult run)
    {
        return (run.Result as StatusCodeResult)?.StatusCode ?? (run.Result as ObjectResult)?.StatusCode;
    }

    private async Task<RunResult> Run(
        User user,
        string controllerName,
        string action,
        Dictionary<string, object?>? arguments = null,
        IActionResult? result = null,
        string deviceId = "sharelinks-device",
        TypeInfo? controller = null)
    {
        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim("Jellyfin-UserId", user.Id.ToString()),
                new Claim("Jellyfin-Client", "Jellyfin Web"),
                new Claim("Jellyfin-Version", "10.11.0"),
                new Claim("Jellyfin-DeviceId", deviceId),
                new Claim("Jellyfin-Device", "Firefox")
            },
            "test");
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        http.Connection.RemoteIpAddress = IPAddress.Loopback;
        var descriptor = new ControllerActionDescriptor
        {
            ControllerName = controllerName,
            ActionName = action,
            ControllerTypeInfo = controller ?? CoreController,
            DisplayName = controllerName + "." + action
        };
        var actionContext = new ActionContext(http, new RouteData(), descriptor);
        var filters = new List<IFilterMetadata>();
        var executing = new ActionExecutingContext(actionContext, filters, arguments ?? new Dictionary<string, object?>(), new object());
        var run = new RunResult();

        await _guard.OnActionExecutionAsync(executing, () =>
        {
            run.NextCalled = true;
            run.Executed = new ActionExecutedContext(actionContext, filters, new object()) { Result = result ?? new OkResult() };
            return Task.FromResult(run.Executed);
        });

        run.Result = run.NextCalled ? result ?? new OkResult() : executing.Result;
        return run;
    }

    private static TypeInfo MakeController(string assemblyName)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(assemblyName), AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule(assemblyName);
        return module.DefineType("FakeController", TypeAttributes.Public).CreateTypeInfo()!;
    }

    private sealed class RunResult
    {
        public bool NextCalled { get; set; }

        public IActionResult? Result { get; set; }

        public ActionExecutedContext? Executed { get; set; }
    }

    private sealed record JoinBody(Guid GroupId);

    private sealed class LiveStreamBody
    {
        public string? OpenToken { get; set; }

        public Guid? ItemId { get; set; }
    }
}
