using System.Net;
using System.Security.Claims;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ShareLinks.Models;
using Jellyfin.Plugin.ShareLinks.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay;
using MediaBrowser.Controller.SyncPlay.Requests;
using MediaBrowser.Model.SyncPlay;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.ShareLinks.Tests;

public sealed class WatchPartyServiceTests : IDisposable
{
    private readonly TempStore _temp = new();
    private readonly Mock<ISyncPlayManager> _syncPlay = new();
    private readonly Mock<ISessionManager> _sessions = new();
    private readonly Mock<IUserManager> _users = new();
    private readonly User _host = TestSupport.Regular();
    private readonly User _guest = TestSupport.Guest();
    private readonly Guid _group = Guid.NewGuid();
    private readonly WatchPartyService _service;
    private SessionInfo? _guestSession;

    public WatchPartyServiceTests()
    {
        TestSupport.EnsurePlugin();
        _users.Setup(u => u.GetUserById(_host.Id)).Returns(_host);
        _users.Setup(u => u.GetUserById(_guest.Id)).Returns(_guest);
        _sessions
            .Setup(s => s.LogSessionActivity(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<User>()))
            .ReturnsAsync(() => _guestSession!);
        _service = new WatchPartyService(_syncPlay.Object, _sessions.Object, _users.Object, _temp.Store, NullLogger<WatchPartyService>.Instance);
    }

    [Fact]
    public async Task AGuestJoinsTheGroupOnTheirRecordAndNoOther()
    {
        await SaveParty();
        GroupExistsForHost(true);
        _guestSession = ConnectedSession(_guest.Id);
        _syncPlay.Setup(s => s.GetGroup(_guestSession, _group)).Returns(Info(_group));

        var result = await _service.JoinAsync(_guest.Id, GuestRequest(), CancellationToken.None);

        Assert.Equal(WatchPartyJoinResult.Joined, result);
        _syncPlay.Verify(s => s.JoinGroup(_guestSession, It.Is<JoinGroupRequest>(r => r.GroupId == _group), It.IsAny<CancellationToken>()), Times.Once);
        _syncPlay.Verify(s => s.JoinGroup(It.IsAny<SessionInfo>(), It.Is<JoinGroupRequest>(r => r.GroupId != _group), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OnlyAPartyGuestCanJoin()
    {
        await _temp.Store.UpsertAsync(TestSupport.ActiveRecord(_guest.Id, r => r.CreatedByUserId = _host.Id));
        Assert.Equal(WatchPartyJoinResult.NotAParty, await _service.JoinAsync(_guest.Id, GuestRequest(), CancellationToken.None));

        // Someone who is not a guest at all.
        Assert.Equal(WatchPartyJoinResult.NotAParty, await _service.JoinAsync(_host.Id, GuestRequest(), CancellationToken.None));
        _syncPlay.Verify(s => s.JoinGroup(It.IsAny<SessionInfo>(), It.IsAny<JoinGroupRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AnExpiredOrRevokedPartyCannotBeJoined()
    {
        await SaveParty(r => r.Status = ShareLinkStatus.Revoked);
        GroupExistsForHost(true);
        _guestSession = ConnectedSession(_guest.Id);
        Assert.Equal(WatchPartyJoinResult.NotAParty, await _service.JoinAsync(_guest.Id, GuestRequest(), CancellationToken.None));
    }

    [Fact]
    public async Task AClosedGroupMeansThePartyIsOver()
    {
        await SaveParty();
        GroupExistsForHost(false);
        _guestSession = ConnectedSession(_guest.Id);

        Assert.Equal(WatchPartyJoinResult.Ended, await _service.JoinAsync(_guest.Id, GuestRequest(), CancellationToken.None));
        _syncPlay.Verify(s => s.JoinGroup(It.IsAny<SessionInfo>(), It.IsAny<JoinGroupRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ABrowserWithoutItsSocketIsAskedToWait()
    {
        await SaveParty();
        GroupExistsForHost(true);
        _guestSession = new SessionInfo(_sessions.Object, NullLogger.Instance) { Id = "no-socket", UserId = _guest.Id };

        Assert.Equal(WatchPartyJoinResult.NotConnected, await _service.JoinAsync(_guest.Id, GuestRequest(), CancellationToken.None));
        _syncPlay.Verify(s => s.JoinGroup(It.IsAny<SessionInfo>(), It.IsAny<JoinGroupRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AGroupPlayingSomethingUnsharedIsNotJoined()
    {
        await SaveParty();
        GroupExistsForHost(true);
        _guestSession = ConnectedSession(_guest.Id);
        _syncPlay.Setup(s => s.GetGroup(_guestSession, _group)).Returns((GroupInfoDto)null!);

        Assert.Equal(WatchPartyJoinResult.NotShared, await _service.JoinAsync(_guest.Id, GuestRequest(), CancellationToken.None));
        _syncPlay.Verify(s => s.JoinGroup(It.IsAny<SessionInfo>(), It.IsAny<JoinGroupRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void TheGroupIsLookedUpAsTheHost()
    {
        _syncPlay.Setup(s => s.GetGroup(It.Is<SessionInfo>(p => p.UserId == _host.Id), _group)).Returns(Info(_group));

        Assert.True(_service.GroupExists(_group, _host.Id));
        Assert.False(_service.GroupExists(Guid.NewGuid(), _host.Id));
        Assert.False(_service.GroupExists(_group, null));
        Assert.False(_service.GroupExists(_group, Guid.NewGuid()));
    }

    [Fact]
    public void ALookupThatThrowsCountsAsGone()
    {
        _syncPlay.Setup(s => s.GetGroup(It.IsAny<SessionInfo>(), _group)).Throws<NullReferenceException>();
        Assert.False(_service.GroupExists(_group, _host.Id));
    }

    [Fact]
    public void GroupsAreConfinedToTheirShare()
    {
        var party = TestSupport.ActiveRecord(_guest.Id, r =>
        {
            r.WatchParty = true;
            r.WatchPartyGroupId = _group;
        });
        var ordinary = TestSupport.ActiveRecord(Guid.NewGuid());
        var otherOrdinary = TestSupport.ActiveRecord(Guid.NewGuid());
        var guestMade = Guid.NewGuid();

        Assert.True(_service.IsGroupAllowed(party, _group));
        Assert.False(_service.IsGroupAllowed(party, guestMade));
        Assert.False(_service.IsGroupAllowed(ordinary, guestMade));

        _service.RegisterGuestGroup(ordinary, guestMade);
        Assert.True(_service.IsGroupAllowed(ordinary, guestMade));
        Assert.False(_service.IsGroupAllowed(otherOrdinary, guestMade));
        Assert.False(_service.IsGroupAllowed(party, guestMade));
        Assert.False(_service.IsGroupAllowed(ordinary, _group));

        // A party guest's group is the party's; it cannot register a second one.
        _service.RegisterGuestGroup(party, Guid.NewGuid());
        Assert.False(_service.IsGroupAllowed(party, Guid.NewGuid()));

        _service.ForgetShare(ordinary.Id);
        Assert.False(_service.IsGroupAllowed(ordinary, guestMade));
    }

    [Fact]
    public void APartyWithoutAGroupAllowsNone()
    {
        var party = TestSupport.ActiveRecord(_guest.Id, r => r.WatchParty = true);
        Assert.False(_service.IsGroupAllowed(party, Guid.Empty));
        Assert.False(_service.IsGroupAllowed(party, Guid.NewGuid()));
    }

    [Fact]
    public async Task TheSessionIsFoundFromTheTokensClaims()
    {
        _guestSession = ConnectedSession(_guest.Id);
        var context = GuestRequest();

        var session = await _service.GetRequestSessionAsync(context, _guest.Id);

        Assert.Same(_guestSession, session);
        _sessions.Verify(s => s.LogSessionActivity("Jellyfin Web", "10.11.0", "sharelinks-device", "Firefox", "203.0.113.9", _guest), Times.Once);
    }

    [Fact]
    public async Task NoDeviceClaimMeansNoSession()
    {
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", _guest.Id.ToString()) }, "test")) };
        Assert.Null(await _service.GetRequestSessionAsync(context, _guest.Id));
        Assert.Null(await _service.GetRequestSessionAsync(GuestRequest(), Guid.NewGuid()));
    }

    [Fact]
    public void TheGroupIsNamedAfterTheTitle()
    {
        var host = ConnectedSession(_host.Id);
        _syncPlay.Setup(s => s.NewGroup(host, It.IsAny<NewGroupRequest>(), It.IsAny<CancellationToken>())).Returns(Info(_group));

        Assert.Equal(_group, _service.StartGroup(host, "  Heat  ", CancellationToken.None));
        _syncPlay.Verify(s => s.NewGroup(host, It.Is<NewGroupRequest>(r => r.GroupName == "Watch party: Heat"), It.IsAny<CancellationToken>()));

        _service.StartGroup(host, new string('x', 500), CancellationToken.None);
        _syncPlay.Verify(s => s.NewGroup(host, It.Is<NewGroupRequest>(r => r.GroupName.Length == 120), It.IsAny<CancellationToken>()));
    }

    public void Dispose() => _temp.Dispose();

    private async Task SaveParty(Action<ShareLinkRecord>? configure = null)
    {
        await _temp.Store.UpsertAsync(TestSupport.ActiveRecord(_guest.Id, r =>
        {
            r.CreatedByUserId = _host.Id;
            r.WatchParty = true;
            r.WatchPartyGroupId = _group;
            configure?.Invoke(r);
        }));
    }

    private void GroupExistsForHost(bool exists)
    {
        _syncPlay.Setup(s => s.GetGroup(It.Is<SessionInfo>(p => p.UserId == _host.Id), _group)).Returns(exists ? Info(_group) : null!);
    }

    private SessionInfo ConnectedSession(Guid userId)
    {
        var controller = new Mock<ISessionController>();
        controller.SetupGet(c => c.IsSessionActive).Returns(true);
        return new SessionInfo(_sessions.Object, NullLogger.Instance)
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = userId,
            SessionControllers = new[] { controller.Object }
        };
    }

    private static GroupInfoDto Info(Guid id) => new(id, "party", GroupStateType.Idle, new[] { "host" }, DateTime.UtcNow);

    private DefaultHttpContext GuestRequest()
    {
        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim("Jellyfin-UserId", _guest.Id.ToString()),
                new Claim("Jellyfin-Client", "Jellyfin Web"),
                new Claim("Jellyfin-Version", "10.11.0"),
                new Claim("Jellyfin-DeviceId", "sharelinks-device"),
                new Claim("Jellyfin-Device", "Firefox")
            },
            "test");
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
        return context;
    }
}
