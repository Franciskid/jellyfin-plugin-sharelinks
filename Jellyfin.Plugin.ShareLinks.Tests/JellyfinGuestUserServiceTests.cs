using Jellyfin.Data.Queries;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Entities.Security;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.ShareLinks.Models;
using Jellyfin.Plugin.ShareLinks.Services;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.ShareLinks.Tests;

public sealed class JellyfinGuestUserServiceTests
{
    private readonly Mock<IUserManager> _users = new();
    private readonly Mock<IDeviceManager> _devices = new();
    private readonly Mock<ISessionManager> _sessions = new();
    private readonly Mock<ILibraryManager> _library = new();
    private readonly JellyfinGuestUserService _service;

    public JellyfinGuestUserServiceTests()
    {
        TestSupport.EnsurePlugin();
        _devices.Setup(d => d.GetDevices(It.IsAny<DeviceQuery>())).Returns(new QueryResult<Device>(Array.Empty<Device>()));
        _service = new JellyfinGuestUserService(_users.Object, _devices.Object, _sessions.Object, _library.Object, NullLogger<JellyfinGuestUserService>.Instance);
    }

    [Theory]
    [InlineData(ShareKind.Video)]
    [InlineData(ShareKind.Music)]
    [InlineData(ShareKind.Photo)]
    [InlineData(ShareKind.Book)]
    [InlineData(ShareKind.Collection)]
    [InlineData(ShareKind.Playlist)]
    [InlineData(ShareKind.Folder)]
    [InlineData(ShareKind.LiveTv)]
    public void EveryGuestIsLockedDownTheSameWay(ShareKind kind)
    {
        var record = TestSupport.ActiveRecord(Guid.NewGuid(), r => r.Kind = kind);
        var policy = JellyfinGuestUserService.BuildPolicy(record, false, "reset");

        Assert.Equal(GuestAuthenticationProvider.ProviderId, policy.AuthenticationProviderId);
        Assert.Equal(new[] { record.AllowedTag }, policy.AllowedTags);
        Assert.Empty(policy.BlockedTags);
        Assert.False(policy.IsAdministrator);
        Assert.True(policy.IsHidden);
        Assert.False(policy.IsDisabled);
        Assert.False(policy.EnableRemoteControlOfOtherUsers);
        Assert.False(policy.EnableSharedDeviceControl);
        Assert.False(policy.EnableContentDeletion);
        Assert.Empty(policy.EnableContentDeletionFromFolders);
        Assert.False(policy.EnableCollectionManagement);
        Assert.False(policy.EnableSubtitleManagement);
        Assert.False(policy.EnableLyricManagement);
        Assert.False(policy.EnableUserPreferenceAccess);
        Assert.False(policy.EnableLiveTvManagement);
        Assert.False(policy.EnablePublicSharing);
        Assert.False(policy.EnableAllChannels);
        Assert.False(policy.EnableMediaConversion);
        Assert.True(policy.EnableMediaPlayback);
        Assert.Equal(-1, policy.LoginAttemptsBeforeLockout);
    }

    [Theory]
    [InlineData(ShareKind.Video, false, false)]
    [InlineData(ShareKind.Music, false, false)]
    [InlineData(ShareKind.Photo, false, false)]
    [InlineData(ShareKind.Book, false, true)]
    [InlineData(ShareKind.Collection, false, false)]
    [InlineData(ShareKind.Playlist, false, false)]
    [InlineData(ShareKind.Folder, false, false)]
    [InlineData(ShareKind.LiveTv, true, false)]
    public void LiveTvAndDownloadsOnlyWhereTheShareNeedsThem(ShareKind kind, bool liveTv, bool download)
    {
        var policy = JellyfinGuestUserService.BuildPolicy(TestSupport.ActiveRecord(Guid.NewGuid(), r => r.Kind = kind), false, null);
        Assert.Equal(liveTv, policy.EnableLiveTvAccess);
        Assert.Equal(download, policy.EnableContentDownloading);
    }

    [Theory]
    [InlineData(ShareKind.Video, false, SyncPlayUserAccessType.CreateAndJoinGroups)]
    [InlineData(ShareKind.Music, false, SyncPlayUserAccessType.CreateAndJoinGroups)]
    [InlineData(ShareKind.Playlist, false, SyncPlayUserAccessType.CreateAndJoinGroups)]
    [InlineData(ShareKind.Photo, false, SyncPlayUserAccessType.None)]
    [InlineData(ShareKind.Book, false, SyncPlayUserAccessType.None)]
    [InlineData(ShareKind.LiveTv, false, SyncPlayUserAccessType.None)]
    [InlineData(ShareKind.Video, true, SyncPlayUserAccessType.JoinGroups)]
    [InlineData(ShareKind.Collection, true, SyncPlayUserAccessType.JoinGroups)]
    public void SyncPlayFollowsTheShare(ShareKind kind, bool party, SyncPlayUserAccessType expected)
    {
        var record = TestSupport.ActiveRecord(Guid.NewGuid(), r =>
        {
            r.Kind = kind;
            r.WatchParty = party;
        });
        Assert.Equal(expected, JellyfinGuestUserService.BuildPolicy(record, false, null).SyncPlayAccess);
    }

    [Fact]
    public void ViewerCeilingFollowsTheLinkType()
    {
        Assert.Equal(1, JellyfinGuestUserService.BuildPolicy(TestSupport.ActiveRecord(Guid.NewGuid(), r => r.OneUse = true), false, null).MaxActiveSessions);
        Assert.Equal(10, JellyfinGuestUserService.BuildPolicy(TestSupport.ActiveRecord(Guid.NewGuid(), r => r.OneUse = false), false, null).MaxActiveSessions);
        Assert.True(JellyfinGuestUserService.BuildPolicy(TestSupport.ActiveRecord(Guid.NewGuid()), true, null).IsDisabled);
    }

    [Fact]
    public async Task SessionsEndBeforeTheGuestIsDeletedAndNobodyElseIsTouched()
    {
        var guest = TestSupport.Guest();
        var owner = TestSupport.Regular();
        _users.Setup(u => u.GetUserById(guest.Id)).Returns(guest);
        var live = new List<SessionInfo>
        {
            Session("viewer-1", guest.Id),
            Session("viewer-2", guest.Id),
            Session("owner", owner.Id)
        };
        _sessions.SetupGet(s => s.Sessions).Returns(() => live.ToList());
        var deleted = false;
        _sessions.Setup(s => s.ReportSessionEnded(It.IsAny<string>())).Returns((string id) =>
        {
            Assert.False(deleted);
            live.RemoveAll(session => session.Id == id);
            return ValueTask.CompletedTask;
        });
        _users.Setup(u => u.DeleteUserAsync(guest.Id)).Returns(() =>
        {
            Assert.DoesNotContain(live, session => session.UserId == guest.Id);
            deleted = true;
            return Task.CompletedTask;
        });

        await _service.DeleteGuestUserAsync(TestSupport.ActiveRecord(guest.Id), CancellationToken.None);

        Assert.True(deleted);
        Assert.Equal("owner", Assert.Single(live).Id);
        _sessions.Verify(s => s.ReportSessionEnded("owner"), Times.Never);
    }

    [Fact]
    public async Task APlaylistIsSharedReadOnlyAndTakenBack()
    {
        var guestId = Guid.NewGuid();
        var friend = new PlaylistUserPermissions(Guid.NewGuid(), true);
        var playlist = TestSupport.Item<Playlist>();
        playlist.Shares = new[] { friend };

        await _service.GrantPlaylistAsync(playlist, guestId, CancellationToken.None);
        await _service.GrantPlaylistAsync(playlist, guestId, CancellationToken.None);

        var share = Assert.Single(playlist.Shares, s => s.UserId == guestId);
        Assert.False(share.CanEdit);
        Assert.Contains(friend, playlist.Shares);
        _library.Verify(l => l.UpdateItemAsync(playlist, It.IsAny<BaseItem>(), ItemUpdateType.MetadataEdit, It.IsAny<CancellationToken>()), Times.Once);

        await _service.RevokePlaylistAsync(playlist, guestId, CancellationToken.None);
        await _service.RevokePlaylistAsync(playlist, guestId, CancellationToken.None);

        Assert.Equal(new[] { friend }, playlist.Shares);
        _library.Verify(l => l.UpdateItemAsync(playlist, It.IsAny<BaseItem>(), ItemUpdateType.MetadataEdit, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task AnEditableShareForTheGuestIsDowngraded()
    {
        var guestId = Guid.NewGuid();
        var playlist = TestSupport.Item<Playlist>();
        playlist.Shares = new[] { new PlaylistUserPermissions(guestId, true) };

        await _service.GrantPlaylistAsync(playlist, guestId, CancellationToken.None);

        Assert.False(Assert.Single(playlist.Shares).CanEdit);
    }

    private SessionInfo Session(string id, Guid userId)
    {
        return new SessionInfo(_sessions.Object, NullLogger.Instance) { Id = id, UserId = userId };
    }
}
