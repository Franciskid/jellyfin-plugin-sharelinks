using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ShareLinks.Models;
using Jellyfin.Plugin.ShareLinks.Storage;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShareLinks.Services;

/// <summary>Creates durable ShareLinks records and applies the temporary tag.</summary>
public sealed class ShareLinkCreationService
{
    private readonly ShareLinkStore _store;
    private readonly ShareTokenService _tokenService;
    private readonly ItemTagService _itemTagService;
    private readonly ILogger<ShareLinkCreationService> _logger;

    /// <summary>Initializes a new instance of the <see cref="ShareLinkCreationService"/> class.</summary>
    public ShareLinkCreationService(
        ShareLinkStore store,
        ShareTokenService tokenService,
        ItemTagService itemTagService,
        ILogger<ShareLinkCreationService> logger)
    {
        _store = store;
        _tokenService = tokenService;
        _itemTagService = itemTagService;
        _logger = logger;
    }

    /// <summary>Creates a new share-link record and returns the raw token once.</summary>
    public async Task<(ShareLinkRecord Record, string RawToken)> CreateAsync(
        BaseItem item,
        Guid createdByUserId,
        int expiryHours,
        bool oneUse,
        CancellationToken cancellationToken)
    {
        if (item is null)
        {
            throw new ArgumentNullException(nameof(item));
        }

        var token = await _tokenService.GenerateAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var record = new ShareLinkRecord
        {
            Id = Guid.NewGuid(),
            TokenHash = token.TokenHash,
            ItemId = item.Id.ToString("D"),
            ItemNameSnapshot = item.Name ?? string.Empty,
            CreatedByUserId = createdByUserId == Guid.Empty ? null : createdByUserId,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddHours(expiryHours),
            Status = ShareLinkStatus.Pending,
            OneUse = oneUse,
            AllowedTag = $"sharelinks-{Guid.NewGuid():N}",
            CleanupAttempts = 0
        };

        await _store.UpsertAsync(record, cancellationToken).ConfigureAwait(false);

        try
        {
            if (!string.IsNullOrWhiteSpace(record.AllowedTag))
            {
                record.MetadataTouched = await _itemTagService.EnsureTagAsync(item, record.AllowedTag!, cancellationToken).ConfigureAwait(false);
            }

            record.Status = ShareLinkStatus.Active;
            record.CleanupError = null;
            await _store.UpdateAsync(record, cancellationToken).ConfigureAwait(false);
            return (record, token.Token);
        }
        catch (Exception ex)
        {
            record.Status = ShareLinkStatus.Failed;
            record.CleanupError = ex.Message;
            record.MetadataTouched = true;
            await _store.UpdateAsync(record, cancellationToken).ConfigureAwait(false);
            _logger.LogWarning(ex, "ShareLinks: failed to finish creation for record {RecordId}.", record.Id);
            throw;
        }
    }
}
