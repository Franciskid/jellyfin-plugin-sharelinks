namespace Jellyfin.Plugin.ShareLinks.Models;

/// <summary>
/// A freshly generated share token and its persisted hash.
/// </summary>
public sealed class ShareTokenMaterial
{
    /// <summary>Gets or sets the raw token returned once to the caller.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Gets or sets the HMAC hash stored durably.</summary>
    public string TokenHash { get; set; } = string.Empty;
}
