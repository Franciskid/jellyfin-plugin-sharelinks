using System;
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

    /// <summary>
    /// Gets or sets how many people may watch a multi-use link at the same time.
    /// 0 means no limit. One-use links are always a single viewer regardless.
    /// </summary>
    public int MaxConcurrentViewers { get; set; } = 10;

    /// <summary>Gets or sets a value indicating whether guest-mode lockdown is enabled.</summary>
    public bool GuestModeLockdownEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a comma-separated list of CSS selectors that are hidden from guest
    /// sessions in the web client. Used to suppress other plugins' injected UI (search
    /// bars, floating buttons) so a guest only sees the shared title. Empty by default.
    ///
    /// This is cosmetic only. It runs in the browser, so it tidies the guest's view but
    /// enforces nothing: the access boundary is the share tag policy plus
    /// <see cref="GuestPluginApiGuardEnabled"/>.
    /// </summary>
    public string GuestHiddenSelectors { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether share guests are refused access to other
    /// plugins' API endpoints. Jellyfin's own API stays reachable, since the share tag
    /// policy already bounds it and playback depends on it. On by default: a guest holds
    /// a real access token, so without this any installed plugin answers them directly.
    /// </summary>
    public bool GuestPluginApiGuardEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the plugin ids that guests may reach despite the guard. Empty by
    /// default, so a newly installed plugin is refused without anyone having to
    /// remember it. Opt a plugin in when it genuinely needs to serve guests: an
    /// intro-skip plugin, for instance, is called by the client mid-playback.
    /// </summary>
    public string[] GuestAllowedPluginIds { get; set; } = Array.Empty<string>();
}
