using Jellyfin.Plugin.ShareLinks.Models;
using Xunit;

namespace Jellyfin.Plugin.ShareLinks.Tests;

public sealed class ShareLinkStoreTests : IDisposable
{
    private readonly TempStore _temp = new();

    [Fact]
    public async Task ReadsAreIndependentCopies()
    {
        var record = TestSupport.ActiveRecord(Guid.NewGuid());
        await _temp.Store.UpsertAsync(record);

        var first = await _temp.Store.GetByIdAsync(record.Id);
        first!.Status = ShareLinkStatus.Revoked;
        first.WatchPartyGroupId = Guid.NewGuid();

        // Changing what a read returned must not change the store until it is saved.
        var second = await _temp.Store.GetByIdAsync(record.Id);
        Assert.Equal(ShareLinkStatus.Redeemed, second!.Status);
        Assert.Null(second.WatchPartyGroupId);
    }

    [Fact]
    public async Task WritesReachDiskAndTheCache()
    {
        var record = TestSupport.ActiveRecord(Guid.NewGuid(), r =>
        {
            r.Kind = ShareKind.Playlist;
            r.WatchParty = true;
            r.WatchPartyGroupId = Guid.NewGuid();
            r.WatchPartyHostOnly = true;
        });
        await _temp.Store.UpsertAsync(record);

        var reopened = await _temp.Reopen().GetByIdAsync(record.Id);
        Assert.NotNull(reopened);
        Assert.Equal(ShareKind.Playlist, reopened!.Kind);
        Assert.True(reopened.WatchParty);
        Assert.True(reopened.WatchPartyHostOnly);
        Assert.Equal(record.WatchPartyGroupId, reopened.WatchPartyGroupId);

        record.Status = ShareLinkStatus.Revoked;
        await _temp.Store.UpdateAsync(record);
        Assert.Equal(ShareLinkStatus.Revoked, (await _temp.Store.GetByIdAsync(record.Id))!.Status);
    }

    [Fact]
    public async Task RecordsFromBeforeThisReleaseReadAsOrdinaryVideoShares()
    {
        var id = Guid.NewGuid();
        var directory = Path.Combine(_temp.Directory, "sharelinks");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "sharelinks.json"),
            $$"""[{"Id":"{{id}}","TokenHash":"x","ItemId":"{{Guid.NewGuid()}}","Status":2,"ExpiresAtUtc":"2999-01-01T00:00:00+00:00","OneUse":true}]""");

        var record = await _temp.Reopen().GetByIdAsync(id);

        Assert.NotNull(record);
        Assert.Equal(ShareKind.Video, record!.Kind);
        Assert.False(record.WatchParty);
        Assert.Null(record.WatchPartyGroupId);
        Assert.False(record.WatchPartyHostOnly);
    }

    [Fact]
    public async Task OnlyALiveShareBelongsToAGuest()
    {
        var guest = Guid.NewGuid();
        await _temp.Store.UpsertAsync(TestSupport.ActiveRecord(guest, r => r.Status = ShareLinkStatus.Revoked));
        await _temp.Store.UpsertAsync(TestSupport.ActiveRecord(guest, r => r.ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1)));
        Assert.Null(await _temp.Store.GetActiveByGuestUserIdAsync(guest));

        var live = TestSupport.ActiveRecord(guest);
        await _temp.Store.UpsertAsync(live);
        Assert.Equal(live.Id, (await _temp.Store.GetActiveByGuestUserIdAsync(guest))!.Id);

        Assert.Null(await _temp.Store.GetActiveByGuestUserIdAsync(Guid.NewGuid()));
        Assert.Null(await _temp.Store.GetActiveByGuestUserIdAsync(Guid.Empty));
    }

    [Theory]
    [InlineData(ShareLinkStatus.Active, true)]
    [InlineData(ShareLinkStatus.Redeeming, true)]
    [InlineData(ShareLinkStatus.Redeemed, true)]
    [InlineData(ShareLinkStatus.Pending, false)]
    [InlineData(ShareLinkStatus.Expired, false)]
    [InlineData(ShareLinkStatus.Revoked, false)]
    [InlineData(ShareLinkStatus.Failed, false)]
    public async Task GuestLookupFollowsTheStatus(ShareLinkStatus status, bool found)
    {
        var guest = Guid.NewGuid();
        await _temp.Store.UpsertAsync(TestSupport.ActiveRecord(guest, r => r.Status = status));
        Assert.Equal(found, await _temp.Store.GetActiveByGuestUserIdAsync(guest) is not null);
    }

    public void Dispose() => _temp.Dispose();
}
