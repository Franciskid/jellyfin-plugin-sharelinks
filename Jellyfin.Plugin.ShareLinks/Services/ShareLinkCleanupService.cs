using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ShareLinks.Models;
using Jellyfin.Plugin.ShareLinks.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShareLinks.Services;

/// <summary>Cleanly expires links and tears down temporary guest state.</summary>
public sealed class ShareLinkCleanupService : IShareLinkCleanupService
{
    private readonly ShareLinkStore _store;
    private readonly ILibraryManager _libraryManager;
    private readonly ItemTagService _itemTagService;
    private readonly JellyfinGuestUserService _guestUserService;
    private readonly WatchPartyService _watchPartyService;
    private readonly ILogger<ShareLinkCleanupService> _logger;

    /// <summary>Initializes a new instance of the <see cref="ShareLinkCleanupService"/> class.</summary>
    public ShareLinkCleanupService(
        ShareLinkStore store,
        ILibraryManager libraryManager,
        ItemTagService itemTagService,
        JellyfinGuestUserService guestUserService,
        WatchPartyService watchPartyService,
        ILogger<ShareLinkCleanupService> logger)
    {
        _store = store;
        _libraryManager = libraryManager;
        _itemTagService = itemTagService;
        _guestUserService = guestUserService;
        _watchPartyService = watchPartyService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task CleanupAsync(CancellationToken cancellationToken)
    {
        var records = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var record in records)
        {
            var cleaned = await CleanupRecordInternalAsync(record, records, false, cancellationToken).ConfigureAwait(false);
            await HealLiveTvTagAsync(cleaned, cancellationToken).ConfigureAwait(false);
        }

        await EndFinishedWatchPartiesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Tears down every watch party whose SyncPlay group has closed. A group closes
    /// when its last member leaves, and it cannot be reopened under the same id, so
    /// the link has nothing left to lead to.
    /// </summary>
    public async Task<int> EndFinishedWatchPartiesAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var records = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        var ended = 0;
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!record.WatchParty
                || !record.WatchPartyGroupId.HasValue
                || record.ExpiresAtUtc <= now
                || record.Status is not (ShareLinkStatus.Active or ShareLinkStatus.Redeemed))
            {
                continue;
            }

            if (_watchPartyService.GroupExists(record.WatchPartyGroupId.Value, record.CreatedByUserId))
            {
                continue;
            }

            _logger.LogInformation(
                "ShareLinks: SyncPlay group {GroupId} closed, ending watch party {RecordId}.",
                record.WatchPartyGroupId.Value,
                record.Id);
            await RevokeAsync(record.Id, cancellationToken).ConfigureAwait(false);
            ended++;
        }

        return ended;
    }

    /// <summary>
    /// Puts a Live TV share's tag back on its channel. A guide refresh replaces a
    /// channel's tags with the ones the tuner reports, which would quietly lock the
    /// guest out until they opened the link again.
    /// </summary>
    public async Task HealLiveTvTagAsync(ShareLinkRecord record, CancellationToken cancellationToken)
    {
        if (record.Kind != ShareKind.LiveTv
            || string.IsNullOrWhiteSpace(record.AllowedTag)
            || record.ExpiresAtUtc <= DateTimeOffset.UtcNow
            || record.Status is not (ShareLinkStatus.Active or ShareLinkStatus.Redeemed))
        {
            return;
        }

        var item = TryGetItem(record.ItemId);
        if (item is null)
        {
            return;
        }

        try
        {
            if (await _itemTagService.EnsureTagAsync(item, record.AllowedTag!, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogInformation("ShareLinks: put the share tag back on channel {ItemId} after a guide refresh.", item.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ShareLinks: could not restore the share tag on channel {ItemId}.", item.Id);
        }
    }

    /// <summary>Revokes a specific share link and immediately runs teardown.</summary>
    public async Task<ShareLinkRecord?> RevokeAsync(Guid id, CancellationToken cancellationToken)
    {
        var record = await _store.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return null;
        }

        record.Status = ShareLinkStatus.Revoked;
        record.CleanupError = null;
        await _store.UpdateAsync(record, cancellationToken).ConfigureAwait(false);

        var records = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        return await CleanupRecordInternalAsync(record, records, true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs cleanup for one record by id.</summary>
    public async Task CleanupRecordAsync(Guid id, bool force, CancellationToken cancellationToken)
    {
        var record = await _store.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return;
        }

        var records = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        await CleanupRecordInternalAsync(record, records, force, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a normal cleanup pass and then drops every record that is finished
    /// with, so the admin list does not grow forever. A link that can still be
    /// used is never removed, and neither is one whose guest is still watching.
    /// </summary>
    public async Task<int> PurgeFinishedAsync(CancellationToken cancellationToken)
    {
        // Teardown first: this expires anything past its date and removes the guest
        // account and tags, so nothing is dropped from the store while it still has
        // state hanging off it.
        await CleanupAsync(cancellationToken).ConfigureAwait(false);

        var records = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        var removed = 0;
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (record.Status is not (ShareLinkStatus.Expired or ShareLinkStatus.Revoked or ShareLinkStatus.Failed))
            {
                continue;
            }

            await _store.DeleteAsync(record.Id, cancellationToken).ConfigureAwait(false);
            removed++;
        }

        if (removed > 0)
        {
            _logger.LogInformation("ShareLinks: purged {Count} finished share-link record(s).", removed);
        }

        return removed;
    }

    private async Task<ShareLinkRecord> CleanupRecordInternalAsync(
        ShareLinkRecord record,
        IReadOnlyList<ShareLinkRecord> allRecords,
        bool force,
        CancellationToken cancellationToken)
    {
        record.CleanupAttempts += 1;
        var now = DateTimeOffset.UtcNow;
        var shouldExpire = record.ExpiresAtUtc <= now && record.Status is not ShareLinkStatus.Expired and not ShareLinkStatus.Revoked;
        if (shouldExpire)
        {
            record.Status = ShareLinkStatus.Expired;
        }

        var shouldTeardown = force
            || record.Status is ShareLinkStatus.Expired
            || record.Status is ShareLinkStatus.Revoked
            || record.Status is ShareLinkStatus.Failed;

        if (!shouldTeardown)
        {
            await _store.UpdateAsync(record, cancellationToken).ConfigureAwait(false);
            return record;
        }

        // The share URL carries the raw token, and it is kept on the record only so
        // the dashboard can offer "copy" while the link is still usable. Once the
        // link is torn down the token is dead weight, so drop it rather than leave
        // it sitting in the store for good.
        record.ShareUrl = null;

        var errors = new List<string>();
        _watchPartyService.ForgetShare(record.Id);

        try
        {
            await _guestUserService.DisableGuestUserAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            errors.Add($"disable:{ex.Message}");
            _logger.LogWarning(ex, "ShareLinks: failed to disable guest user for record {RecordId}.", record.Id);
        }

        // While the guest's id is still known: a playlist's share list holds it.
        if (record.Kind == ShareKind.Playlist && record.GuestUserId.HasValue && TryGetItem(record.ItemId) is Playlist playlist)
        {
            try
            {
                await _guestUserService.RevokePlaylistAsync(playlist, record.GuestUserId.Value, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                errors.Add($"playlist:{ex.Message}");
                _logger.LogWarning(ex, "ShareLinks: failed to take the guest off playlist {ItemId} for record {RecordId}.", record.ItemId, record.Id);
            }
        }

        try
        {
            await _guestUserService.DeleteGuestUserAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            errors.Add($"delete:{ex.Message}");
            _logger.LogWarning(ex, "ShareLinks: failed to delete guest user for record {RecordId}.", record.Id);
        }

        if (!string.IsNullOrWhiteSpace(record.AllowedTag) && !IsTagStillInUse(record, allRecords, now))
        {
            // Runs even when the shared item is gone: the members of a deleted
            // collection or playlist still carry the tag, and the library search
            // inside RemoveTagTreeAsync finds them without it.
            var item = TryGetItem(record.ItemId);
            try
            {
                var removed = await _itemTagService.RemoveTagTreeAsync(item, record.AllowedTag!, cancellationToken).ConfigureAwait(false);
                record.MetadataTouched |= removed;
            }
            catch (Exception ex)
            {
                errors.Add($"tag:{ex.Message}");
                _logger.LogWarning(ex, "ShareLinks: failed to remove tag {Tag} from record {RecordId}.", record.AllowedTag, record.Id);
            }
        }

        record.CleanupError = errors.Count == 0 ? null : string.Join(" | ", errors);
        await _store.UpdateAsync(record, cancellationToken).ConfigureAwait(false);
        return record;
    }

    private BaseItem? TryGetItem(string itemId)
    {
        if (!Guid.TryParse(itemId, out var id))
        {
            return null;
        }

        return _libraryManager.GetItemById(id);
    }

    private static bool IsTagStillInUse(ShareLinkRecord record, IReadOnlyList<ShareLinkRecord> allRecords, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(record.AllowedTag))
        {
            return false;
        }

        return allRecords.Any(other =>
            other.Id != record.Id
            && string.Equals(other.AllowedTag, record.AllowedTag, StringComparison.OrdinalIgnoreCase)
            && other.ExpiresAtUtc > now
            && other.Status is ShareLinkStatus.Pending
                or ShareLinkStatus.Active
                or ShareLinkStatus.Redeeming
                or ShareLinkStatus.Redeemed);
    }
}
