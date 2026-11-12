using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.ShareLinks.Configuration;
using Jellyfin.Plugin.ShareLinks.Models;
using Jellyfin.Plugin.ShareLinks.Security;
using Jellyfin.Plugin.ShareLinks.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.ShareLinks.Tests;

/// <summary>Who may share, and how far a regular user's share reaches.</summary>
public sealed class ShareCreatorPolicyTests
{
    private readonly FakeTree _tree = new();
    private readonly ItemTagService _tags;

    public ShareCreatorPolicyTests()
    {
        TestSupport.EnsurePlugin();
        _tags = new ItemTagService(new Mock<ILibraryManager>().Object, _tree, NullLogger<ItemTagService>.Instance);
    }

    [Fact]
    public void OnlyAdministratorsShareByDefault()
    {
        var config = new PluginConfiguration();

        Assert.True(ShareCreatorPolicy.CanCreate(config, TestSupport.Admin()));
        Assert.False(ShareCreatorPolicy.CanCreate(config, TestSupport.Regular()));
        Assert.False(ShareCreatorPolicy.CanCreate(config, null));
    }

    [Fact]
    public void ARegularUserSharesWhenEveryoneMayOrWhenListed()
    {
        var listed = TestSupport.Regular();
        var other = TestSupport.Regular();

        var byList = new PluginConfiguration { SharingUserIds = new[] { listed.Id.ToString("N"), "not-a-guid" } };
        Assert.True(ShareCreatorPolicy.CanCreate(byList, listed));
        Assert.False(ShareCreatorPolicy.CanCreate(byList, other));

        var everyone = new PluginConfiguration { AllowAllUsersToShare = true };
        Assert.True(ShareCreatorPolicy.CanCreate(everyone, other));
    }

    [Fact]
    public void AGuestOrADisabledUserNeverShares()
    {
        var guest = TestSupport.Guest();
        var disabled = TestSupport.Regular();
        disabled.SetPermission(PermissionKind.IsDisabled, true);
        var config = new PluginConfiguration
        {
            AllowAllUsersToShare = true,
            SharingUserIds = new[] { guest.Id.ToString("D"), disabled.Id.ToString("D") }
        };

        Assert.False(ShareCreatorPolicy.CanCreate(config, guest));
        Assert.False(ShareCreatorPolicy.CanCreate(config, disabled));
    }

    [Fact]
    public void AShareEndsWhenItsRegularCreatorIsGoneOrLosesTheRight()
    {
        var users = new Mock<IUserManager>();
        var creator = TestSupport.Regular();
        users.Setup(u => u.GetUserById(creator.Id)).Returns(creator);
        var allowed = new PluginConfiguration { SharingUserIds = new[] { creator.Id.ToString("N") } };
        var record = TestSupport.ActiveRecord(Guid.NewGuid(), r =>
        {
            r.CreatedByUserId = creator.Id;
            r.RestrictedCreator = true;
        });

        Assert.Equal(CreatorStanding.Restricted, ShareCreatorPolicy.Evaluate(allowed, record, users.Object, out var found));
        Assert.Same(creator, found);

        Assert.Equal(CreatorStanding.Revoked, ShareCreatorPolicy.Evaluate(new PluginConfiguration(), record, users.Object, out _));

        record.CreatedByUserId = Guid.NewGuid();
        Assert.Equal(CreatorStanding.Revoked, ShareCreatorPolicy.Evaluate(allowed, record, users.Object, out _));
    }

    [Fact]
    public void AnAdministratorsShareIsLeftAlone()
    {
        var users = new Mock<IUserManager>();
        var admin = TestSupport.Admin();
        users.Setup(u => u.GetUserById(admin.Id)).Returns(admin);
        var config = new PluginConfiguration();

        var live = TestSupport.ActiveRecord(Guid.NewGuid(), r => r.CreatedByUserId = admin.Id);
        Assert.Equal(CreatorStanding.Unrestricted, ShareCreatorPolicy.Evaluate(config, live, users.Object, out var creator));
        Assert.Null(creator);

        // Links made before creators were tracked, or by an admin since deleted.
        var orphan = TestSupport.ActiveRecord(Guid.NewGuid());
        Assert.Equal(CreatorStanding.Unrestricted, ShareCreatorPolicy.Evaluate(config, orphan, users.Object, out _));
    }

    [Fact]
    public void ACollectionDropsTheMembersItsCreatorCannotOpen()
    {
        var collection = TestSupport.Item<BoxSet>();
        var seen = TestSupport.Item<Movie>();
        var hidden = TestSupport.Item<Movie>();
        _tree.Linked(collection, seen, hidden);

        var targets = _tags.BuildTagTreeTargets(collection, item => item.Id != hidden.Id).Select(item => item.Id).ToHashSet();

        Assert.Contains(collection.Id, targets);
        Assert.Contains(seen.Id, targets);
        Assert.DoesNotContain(hidden.Id, targets);
    }

    [Fact]
    public void AnythingWithAHiddenItemInsideIsLeftOutWhole()
    {
        // The episode would inherit the series' tag, so the series cannot be tagged at all.
        var collection = TestSupport.Item<BoxSet>();
        var series = TestSupport.Item<Series>();
        var season = TestSupport.Item<Season>();
        var episode = TestSupport.Item<Episode>();
        var hiddenEpisode = TestSupport.Item<Episode>();
        _tree.Linked(collection, series).Physical(series, season, episode, hiddenEpisode);
        Func<BaseItem, bool> canOpen = item => item.Id != hiddenEpisode.Id;

        var targets = _tags.BuildTagTreeTargets(collection, canOpen);
        Assert.Equal(new[] { collection.Id }, targets.Select(item => item.Id));

        Assert.False(_tags.CanCover(series, canOpen));
        Assert.True(_tags.CanCover(collection, canOpen));
        Assert.False(_tags.CanCover(collection, item => item.Id != collection.Id));
    }

    [Fact]
    public async Task ARestrictedShareTakesItsTagBackOffWhatItNoLongerCovers()
    {
        const string Tag = "sharelinks-test";
        var collection = TestSupport.Item<BoxSet>();
        var kept = TestSupport.Item<Movie>();
        var lost = TestSupport.Item<Movie>();
        _tree.Linked(collection, kept, lost);
        _tree.Tagged.AddRange(new BaseItem[] { collection, kept, lost });

        await _tags.EnsureTagTreeAsync(collection, Tag, _ => true, CancellationToken.None);
        Assert.Contains(Tag, lost.Tags);

        await _tags.EnsureTagTreeAsync(collection, Tag, item => item.Id != lost.Id, CancellationToken.None);
        Assert.Contains(Tag, kept.Tags);
        Assert.DoesNotContain(Tag, lost.Tags);
    }

    [Fact]
    public void AGuestGetsNothingItsRegularCreatorLacks()
    {
        var creator = TestSupport.Regular();
        creator.SetPermission(PermissionKind.EnableVideoPlaybackTranscoding, false);
        creator.SetPermission(PermissionKind.EnableRemoteAccess, false);
        creator.RemoteClientBitrateLimit = 3_000_000;
        creator.SyncPlayAccess = SyncPlayUserAccessType.None;
        var record = TestSupport.ActiveRecord(Guid.NewGuid());

        var open = JellyfinGuestUserService.BuildPolicy(record, false, null);
        var capped = JellyfinGuestUserService.BuildPolicy(record, false, null, creator);

        Assert.True(open.EnableVideoPlaybackTranscoding);
        Assert.True(open.EnableRemoteAccess);
        Assert.False(capped.EnableVideoPlaybackTranscoding);
        Assert.False(capped.EnableRemoteAccess);
        Assert.Equal(3_000_000, capped.RemoteClientBitrateLimit);
        Assert.Equal(SyncPlayUserAccessType.None, capped.SyncPlayAccess);
        Assert.False(capped.IsAdministrator);
    }
}
