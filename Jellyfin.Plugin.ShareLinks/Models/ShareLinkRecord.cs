using System;

namespace Jellyfin.Plugin.ShareLinks.Models;

/// <summary>
/// Persistent share-link record. Only the token hash is stored; the raw token
/// never enters durable storage.
/// </summary>
public sealed class ShareLinkRecord
{
    /// <summary>Gets or sets the share-link id.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Gets or sets the HMAC hash of the token.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>Gets or sets the Jellyfin item id snapshot.</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets the Jellyfin item name snapshot.</summary>
    public string ItemNameSnapshot { get; set; } = string.Empty;

    /// <summary>Gets or sets the library id snapshot.</summary>
    public string? LibraryId { get; set; }

    /// <summary>Gets or sets the user id that created the link.</summary>
    public Guid? CreatedByUserId { get; set; }

    /// <summary>Gets or sets the UTC creation time.</summary>
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Gets or sets the UTC redemption time, if any.</summary>
    public DateTimeOffset? RedeemedAtUtc { get; set; }

    /// <summary>Gets or sets the UTC expiry time.</summary>
    public DateTimeOffset ExpiresAtUtc { get; set; }

    /// <summary>Gets or sets the current lifecycle status.</summary>
    public ShareLinkStatus Status { get; set; } = ShareLinkStatus.Pending;

    /// <summary>Gets or sets the guest user id associated with the link.</summary>
    public Guid? GuestUserId { get; set; }

    /// <summary>Gets or sets the guest user name associated with the link.</summary>
    public string? GuestUserName { get; set; }

    /// <summary>Gets or sets the access token id used by the guest session, if available.</summary>
    public string? AccessTokenId { get; set; }

    /// <summary>Gets or sets the device id used by the guest session, if available.</summary>
    public string? DeviceId { get; set; }

    /// <summary>Gets or sets the allowed tag snapshot, if any.</summary>
    public string? AllowedTag { get; set; }

    /// <summary>Gets or sets a value indicating whether the link may be used once only.</summary>
    public bool OneUse { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether metadata was touched during cleanup.</summary>
    public bool MetadataTouched { get; set; }

    /// <summary>Gets or sets the number of cleanup attempts performed on this record.</summary>
    public int CleanupAttempts { get; set; }

    /// <summary>Gets or sets the last cleanup error, if any.</summary>
    public string? CleanupError { get; set; }

    /// <summary>Gets or sets the share URL issued at creation, for admin display.</summary>
    public string? ShareUrl { get; set; }

    /// <summary>Gets or sets what the share hands out. Older records read back as Video.</summary>
    public ShareKind Kind { get; set; } = ShareKind.Video;

    /// <summary>Gets or sets a value indicating whether guests join the creator's SyncPlay group.</summary>
    public bool WatchParty { get; set; }

    /// <summary>Gets or sets the SyncPlay group a watch party is bound to.</summary>
    public Guid? WatchPartyGroupId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether only the host may pause, seek or change
    /// what is playing. Guests can still buffer, which makes the group wait for them.
    /// </summary>
    public bool WatchPartyHostOnly { get; set; }
}
