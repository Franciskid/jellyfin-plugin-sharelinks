namespace Jellyfin.Plugin.ShareLinks.Models;

/// <summary>Lifecycle state of a share link.</summary>
public enum ShareLinkStatus
{
    Pending = 0,
    Active = 1,
    Redeemed = 2,
    Expired = 3,
    Revoked = 4,
    Failed = 5,
    Redeeming = 6
}
