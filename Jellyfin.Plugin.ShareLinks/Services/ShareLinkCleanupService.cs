using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ShareLinks.Models;
using Jellyfin.Plugin.ShareLinks.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShareLinks.Services;

/// <summary>Cleanly expires links and tears down temporary guest state.</summary>
public sealed class ShareLinkCleanupService : IShareLinkCleanupService
{
    private readonly ShareLinkStore _store;
    private readonly ILibraryManager _libraryManager;
    private readonly ItemTagService _itemTagService;
    private readonly JellyfinGuestUserService _guestUserService;
    private readonly ILogger<ShareLinkCleanupService> _logger;

    /// <summary>Initializes a new instance of the <see cref="ShareLinkCleanupService"/> class.</summary>
    public ShareLinkCleanupService(
        ShareLinkStore store,
        ILibraryManager libraryManager,
        ItemTagService itemTagService,
        JellyfinGuestUserService guestUserService,
        ILogger<ShareLinkCleanupService> logger)
    {
        _store = store;
        _libraryManager = libraryManager;
        _itemTagService = itemTagService;
        _guestUserService = guestUserService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task CleanupAsync(CancellationToken cancellationToken)
    {
        var records = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var record in records)
        {
            await CleanupRecordInternalAsync(record, records, false, cancellationToken).ConfigureAwait(false);
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

        var errors = new List<string>();
        try
        {
            await _guestUserService.DisableGuestUserAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            errors.Add($"disable:{ex.Message}");
            _logger.LogWarning(ex, "ShareLinks: failed to disable guest user for record {RecordId}.", record.Id);
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
            var item = TryGetItem(record.ItemId);
            if (item is not null)
            {
                try
                {
                    var removed = await _itemTagService.RemoveTagAsync(item, record.AllowedTag!, cancellationToken).ConfigureAwait(false);
                    record.MetadataTouched |= removed;
                }
                catch (Exception ex)
                {
                    errors.Add($"tag:{ex.Message}");
                    _logger.LogWarning(ex, "ShareLinks: failed to remove tag {Tag} from record {RecordId}.", record.AllowedTag, record.Id);
                }
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
