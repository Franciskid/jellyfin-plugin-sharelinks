using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ShareLinks.Configuration;

/// <summary>
/// Plugin configuration persisted by Jellyfin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets a value indicating whether the plugin is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the default share expiry in hours.</summary>
    public int DefaultExpiryHours { get; set; } = 24;

    /// <summary>Gets or sets the maximum allowed share expiry in hours.</summary>
    public int MaxExpiryHours { get; set; } = 720;

    /// <summary>
    /// Gets or sets an override for the public base URL used when building
    /// absolute share links. Empty means "derive from the incoming request".
    /// </summary>
    public string PublicBaseUrlOverride { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the prefix used when creating guest user names.
    /// </summary>
    public string GuestUsernamePrefix { get; set; } = "share-";

    /// <summary>Gets or sets a value indicating whether shares may transcode.</summary>
    public bool AllowTranscoding { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether shares may remux.</summary>
    public bool AllowRemuxing { get; set; } = true;

    /// <summary>Gets or sets the cleanup interval, in minutes.</summary>
    public int CleanupIntervalMinutes { get; set; } = 60;

    /// <summary>Gets or sets a value indicating whether links default to one use.</summary>
    public bool OneUseDefault { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether guest-mode lockdown is enabled.</summary>
    public bool GuestModeLockdownEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a comma-separated list of CSS selectors that are hidden from guest
    /// sessions in the web client. Used to suppress other plugins' injected UI (search
    /// bars, floating buttons) so a guest only sees the shared title. The default value
    /// hides the elements of a plugin that injects its own floating button and panel
    /// into the web client; edit or clear it to match whatever else you run.
    /// </summary>
    public string GuestHiddenSelectors { get; set; } = ".ais-fab,.ais-overlay,#ais-styles";
}
