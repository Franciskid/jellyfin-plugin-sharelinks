using Jellyfin.Plugin.ShareLinks.Models;
using Jellyfin.Plugin.ShareLinks.Security;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.LiveTv;
using Xunit;

namespace Jellyfin.Plugin.ShareLinks.Tests;

public sealed class GuestCoreRulesTests
{
    private static readonly Guid OwnGroup = Guid.NewGuid();
    private static readonly Guid OtherGroup = Guid.NewGuid();

    private static readonly string[] PlaybackControl =
    {
        "SyncPlaySetNewQueue", "SyncPlaySetPlaylistItem", "SyncPlayRemoveFromPlaylist",
        "SyncPlayMovePlaylistItem", "SyncPlayQueue", "SyncPlayUnpause", "SyncPlayPause",
        "SyncPlaySeek", "SyncPlayNextItem", "SyncPlayPreviousItem", "SyncPlaySetRepeatMode",
        "SyncPlaySetShuffleMode"
    };

    private static readonly string[] Reporting =
    {
        "SyncPlayLeaveGroup", "SyncPlayPing", "SyncPlayBuffering", "SyncPlayReady", "SyncPlaySetIgnoreWait"
    };

    private static ShareLinkRecord Party(bool hostOnly) => TestSupport.ActiveRecord(Guid.NewGuid(), r =>
    {
        r.WatchParty = true;
        r.WatchPartyGroupId = OwnGroup;
        r.WatchPartyHostOnly = hostOnly;
    });

    private static ShareLinkRecord Ordinary() => TestSupport.ActiveRecord(Guid.NewGuid());

    private static bool OnlyOwnGroup(ShareLinkRecord record, Guid groupId) => groupId == OwnGroup;

    private static GuestCoreDecision SyncPlay(string action, ShareLinkRecord? share, Guid? group = null)
    {
        return GuestCoreRules.Evaluate(new GuestCall("SyncPlay", action, share, group), OnlyOwnGroup);
    }

    [Fact]
    public void AGuestOnlyJoinsItsOwnGroup()
    {
        foreach (var share in new[] { Party(true), Party(false), Ordinary() })
        {
            Assert.Equal(GuestCoreDecision.Allow, SyncPlay("SyncPlayJoinGroup", share, OwnGroup));
            Assert.Equal(GuestCoreDecision.Deny, SyncPlay("SyncPlayJoinGroup", share, OtherGroup));
            Assert.Equal(GuestCoreDecision.Deny, SyncPlay("SyncPlayJoinGroup", share, null));
        }
    }

    [Fact]
    public void AnotherGroupLooksLikeItDoesNotExist()
    {
        Assert.Equal(GuestCoreDecision.Allow, SyncPlay("SyncPlayGetGroup", Party(false), OwnGroup));
        Assert.Equal(GuestCoreDecision.NotFound, SyncPlay("SyncPlayGetGroup", Party(false), OtherGroup));
        Assert.Equal(GuestCoreDecision.NotFound, SyncPlay("SyncPlayGetGroup", Ordinary(), null));
    }

    [Fact]
    public void APartyGuestCannotOpenAGroupButOtherGuestsCan()
    {
        Assert.Equal(GuestCoreDecision.Deny, SyncPlay("SyncPlayCreateGroup", Party(false)));
        Assert.Equal(GuestCoreDecision.Deny, SyncPlay("SyncPlayCreateGroup", Party(true)));
        Assert.Equal(GuestCoreDecision.Allow, SyncPlay("SyncPlayCreateGroup", Ordinary()));
    }

    [Fact]
    public void HostOnlyKeepsPlaybackControlWithTheHost()
    {
        var share = Party(hostOnly: true);
        foreach (var action in PlaybackControl)
        {
            Assert.Equal(GuestCoreDecision.Deny, SyncPlay(action, share));
        }

        // Stop is turned into leaving, so the guest is not stuck in the player.
        Assert.Equal(GuestCoreDecision.LeaveGroup, SyncPlay("SyncPlayStop", share));

        // A guest's own player state still has to reach the group.
        foreach (var action in Reporting)
        {
            Assert.Equal(GuestCoreDecision.Allow, SyncPlay(action, share));
        }
    }

    [Fact]
    public void WithoutHostOnlyEveryoneControls()
    {
        foreach (var share in new[] { Party(hostOnly: false), Ordinary() })
        {
            foreach (var action in PlaybackControl.Append("SyncPlayStop"))
            {
                Assert.Equal(GuestCoreDecision.Allow, SyncPlay(action, share));
            }
        }
    }

    [Fact]
    public void AnOrdinaryShareIsNeverHostOnly()
    {
        // The flag means nothing outside a party; creation never sets it there.
        var share = TestSupport.ActiveRecord(Guid.NewGuid(), r => r.WatchPartyHostOnly = false);
        Assert.Equal(GuestCoreDecision.Allow, SyncPlay("SyncPlayPause", share));
    }

    [Fact]
    public void AGuestWithoutALiveShareCanOnlyLeave()
    {
        Assert.Equal(GuestCoreDecision.Allow, SyncPlay("SyncPlayLeaveGroup", null));
        foreach (var action in PlaybackControl.Concat(new[] { "SyncPlayJoinGroup", "SyncPlayCreateGroup", "SyncPlayGetGroups", "SyncPlayPing" }))
        {
            Assert.Equal(GuestCoreDecision.Deny, SyncPlay(action, null, OwnGroup));
        }
    }

    [Fact]
    public void UnknownSyncPlayActionsAreRefused()
    {
        Assert.Equal(GuestCoreDecision.Deny, SyncPlay("SyncPlayKickParticipant", Ordinary()));
        Assert.Equal(GuestCoreDecision.Deny, SyncPlay("SyncPlayKickParticipant", Party(false)));
    }

    [Fact]
    public void TheGroupListIsLetThroughForFiltering()
    {
        Assert.Equal(GuestCoreDecision.Allow, SyncPlay("SyncPlayGetGroups", Party(true)));
    }

    [Theory]
    [InlineData("GetLiveTvChannels", GuestCoreDecision.Allow)]
    [InlineData("GetChannel", GuestCoreDecision.Allow)]
    [InlineData("GetLiveTvPrograms", GuestCoreDecision.Allow)]
    [InlineData("GetPrograms", GuestCoreDecision.Allow)]
    [InlineData("GetProgram", GuestCoreDecision.Allow)]
    [InlineData("GetRecommendedPrograms", GuestCoreDecision.Allow)]
    [InlineData("GetLiveStreamFile", GuestCoreDecision.Allow)]
    [InlineData("GetTimers", GuestCoreDecision.Deny)]
    [InlineData("GetTimer", GuestCoreDecision.Deny)]
    [InlineData("GetSeriesTimers", GuestCoreDecision.Deny)]
    [InlineData("GetDefaultTimer", GuestCoreDecision.Deny)]
    [InlineData("GetRecordings", GuestCoreDecision.Deny)]
    [InlineData("GetRecordingFolders", GuestCoreDecision.Deny)]
    [InlineData("GetLiveRecordingFile", GuestCoreDecision.Deny)]
    [InlineData("GetLiveTvInfo", GuestCoreDecision.Deny)]
    [InlineData("GetGuideInfo", GuestCoreDecision.Deny)]
    [InlineData("GetDefaultListingProvider", GuestCoreDecision.Deny)]
    [InlineData("GetLineups", GuestCoreDecision.Deny)]
    [InlineData("GetChannelMappingOptions", GuestCoreDecision.Deny)]
    [InlineData("GetTunerHostTypes", GuestCoreDecision.Deny)]
    [InlineData("CreateTimer", GuestCoreDecision.Deny)]
    public void LiveTvIsLimitedToWatchingAChannel(string action, GuestCoreDecision expected)
    {
        Assert.Equal(expected, GuestCoreRules.Evaluate(new GuestCall("LiveTv", action), OnlyOwnGroup));
    }

    [Fact]
    public void OpeningALiveStreamIsChecked()
    {
        Assert.Equal(GuestCoreDecision.CheckLiveStream, GuestCoreRules.Evaluate(new GuestCall("MediaInfo", "OpenLiveStream"), OnlyOwnGroup));
        Assert.Equal(GuestCoreDecision.Allow, GuestCoreRules.Evaluate(new GuestCall("MediaInfo", "GetPostedPlaybackInfo"), OnlyOwnGroup));
    }

    [Theory]
    [InlineData("DisplayContent")]
    [InlineData("Play")]
    [InlineData("SendPlaystateCommand")]
    [InlineData("SendSystemCommand")]
    [InlineData("SendGeneralCommand")]
    [InlineData("SendFullGeneralCommand")]
    [InlineData("SendMessageCommand")]
    [InlineData("AddUserToSession")]
    [InlineData("RemoveUserFromSession")]
    public void AGuestCannotSteerOtherSessions(string action)
    {
        Assert.Equal(GuestCoreDecision.Deny, GuestCoreRules.Evaluate(new GuestCall("Session", action), OnlyOwnGroup));
    }

    [Theory]
    [InlineData("GetSessions")]
    [InlineData("PostCapabilities")]
    [InlineData("PostFullCapabilities")]
    [InlineData("ReportViewing")]
    [InlineData("ReportSessionEnded")]
    public void AGuestStillReportsItsOwnSession(string action)
    {
        Assert.Equal(GuestCoreDecision.Allow, GuestCoreRules.Evaluate(new GuestCall("Session", action), OnlyOwnGroup));
    }

    [Fact]
    public void AGuestCanOnlyReadItsOwnAccount()
    {
        var self = Guid.NewGuid();
        Assert.Equal(GuestCoreDecision.Allow, GuestCoreRules.Evaluate(new GuestCall("User", "GetUserById", UserId: self, CallerId: self), OnlyOwnGroup));
        Assert.Equal(GuestCoreDecision.NotFound, GuestCoreRules.Evaluate(new GuestCall("User", "GetUserById", UserId: Guid.NewGuid(), CallerId: self), OnlyOwnGroup));
        Assert.Equal(GuestCoreDecision.NotFound, GuestCoreRules.Evaluate(new GuestCall("User", "GetUserById", UserId: null, CallerId: self), OnlyOwnGroup));
        Assert.Equal(GuestCoreDecision.NotFound, GuestCoreRules.Evaluate(new GuestCall("User", "GetUserById", UserId: Guid.Empty, CallerId: Guid.Empty), OnlyOwnGroup));
        Assert.Equal(GuestCoreDecision.Allow, GuestCoreRules.Evaluate(new GuestCall("User", "GetCurrentUser", CallerId: self), OnlyOwnGroup));
    }

    [Fact]
    public void EverythingElseIsLeftToTheTagPolicy()
    {
        Assert.Equal(GuestCoreDecision.Allow, GuestCoreRules.Evaluate(new GuestCall("Items", "GetItems"), OnlyOwnGroup));
        Assert.Equal(GuestCoreDecision.Allow, GuestCoreRules.Evaluate(new GuestCall("UserLibrary", "GetItem"), OnlyOwnGroup));
    }

    [Fact]
    public void OnlyTheSharedChannelAndItsProgrammesAreInsideALiveTvShare()
    {
        var shared = new LiveTvChannel { Id = Guid.NewGuid() };
        var other = new LiveTvChannel { Id = Guid.NewGuid() };
        var share = TestSupport.ActiveRecord(Guid.NewGuid(), r =>
        {
            r.Kind = ShareKind.LiveTv;
            r.ItemId = shared.Id.ToString("D");
        });

        Assert.False(GuestCoreRules.IsOutsideLiveTvShare(shared, share));
        Assert.True(GuestCoreRules.IsOutsideLiveTvShare(other, share));
        Assert.False(GuestCoreRules.IsOutsideLiveTvShare(new LiveTvProgram { Id = Guid.NewGuid(), ChannelId = shared.Id }, share));
        Assert.True(GuestCoreRules.IsOutsideLiveTvShare(new LiveTvProgram { Id = Guid.NewGuid(), ChannelId = other.Id }, share));
    }

    [Fact]
    public void NoOtherShareReachesLiveTv()
    {
        var channel = new LiveTvChannel { Id = Guid.NewGuid() };

        // A movie share whose item id happens to be written as the channel's.
        var movieShare = TestSupport.ActiveRecord(Guid.NewGuid(), r => r.ItemId = channel.Id.ToString("D"));
        Assert.True(GuestCoreRules.IsOutsideLiveTvShare(channel, movieShare));
        Assert.True(GuestCoreRules.IsOutsideLiveTvShare(channel, null));
        Assert.True(GuestCoreRules.IsOutsideLiveTvShare(channel, TestSupport.ActiveRecord(Guid.NewGuid(), r =>
        {
            r.Kind = ShareKind.LiveTv;
            r.ItemId = "not-a-guid";
        })));
    }

    [Fact]
    public void OrdinaryItemsAreNotLiveTv()
    {
        Assert.False(GuestCoreRules.IsOutsideLiveTvShare(new Movie { Id = Guid.NewGuid() }, null));
    }

    [Fact]
    public void TokenIdsAreFoundInEveryForm()
    {
        var provider = Guid.NewGuid();
        var channel = Guid.NewGuid();
        var source = Guid.NewGuid();

        var jellyfinForm = $"{provider:N}_LiveTvChannel_{channel:N}_{source:N}";
        Assert.Equal(new[] { provider, channel, source }, GuestCoreRules.ExtractTokenGuids(jellyfinForm));

        // Jellyfin's own parser accepts these too, so the check has to as well.
        Assert.Contains(channel, GuestCoreRules.ExtractTokenGuids($"{provider:N}_LiveTvChannel_{channel:D}_x"));
        Assert.Contains(channel, GuestCoreRules.ExtractTokenGuids($"{provider:N}_LiveTvChannel_{channel:B}_x"));
        Assert.Contains(channel, GuestCoreRules.ExtractTokenGuids($"{provider:N}_LiveTvChannel_ {channel:N} _x"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not_a_token")]
    [InlineData("____")]
    public void JunkTokensNameNothing(string? token)
    {
        Assert.Empty(GuestCoreRules.ExtractTokenGuids(token));
    }
}
