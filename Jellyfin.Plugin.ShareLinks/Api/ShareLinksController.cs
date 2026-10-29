using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ShareLinks.Configuration;
using Jellyfin.Plugin.ShareLinks.Models;
using Jellyfin.Plugin.ShareLinks.Services;
using Jellyfin.Plugin.ShareLinks.Storage;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShareLinks.Api;

/// <summary>Request body for ShareLinks admin creation.</summary>
public sealed class ShareLinkCreateRequest
{
    /// <summary>Gets or sets the Jellyfin item id.</summary>
    public string? ItemId { get; set; }

    /// <summary>Gets or sets an optional expiry in hours.</summary>
    public int? ExpiryHours { get; set; }

    /// <summary>Gets or sets whether the link may be redeemed once only.</summary>
    public bool? OneUse { get; set; }

    /// <summary>Gets or sets whether guests join a SyncPlay group opened in the creator's session.</summary>
    public bool? WatchParty { get; set; }

    /// <summary>Gets or sets whether only the host may pause, seek or change what plays in the party.</summary>
    public bool? HostOnlyControl { get; set; }
}

/// <summary>Admin response for a created ShareLinks record.</summary>
public sealed class ShareLinkCreateResponse
{
    /// <summary>Gets or sets the raw share URL.</summary>
    public string ShareUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the created record snapshot.</summary>
    public ShareLinkAdminRecordDto Record { get; set; } = new();
}

/// <summary>DTO returned by admin list and revoke endpoints.</summary>
public sealed class ShareLinkAdminRecordDto
{
    public Guid Id { get; set; }

    public string ItemId { get; set; } = string.Empty;

    public string ItemNameSnapshot { get; set; } = string.Empty;

    public string? LibraryId { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset? RedeemedAtUtc { get; set; }

    public DateTimeOffset ExpiresAtUtc { get; set; }

    public ShareLinkStatus Status { get; set; }

    public Guid? GuestUserId { get; set; }

    public string? GuestUserName { get; set; }

    public string? AllowedTag { get; set; }

    public bool OneUse { get; set; }

    public bool MetadataTouched { get; set; }

    public int CleanupAttempts { get; set; }

    public string? CleanupError { get; set; }

    public string? ShareUrl { get; set; }

    public ShareKind Kind { get; set; }

    public bool WatchParty { get; set; }

    public bool WatchPartyHostOnly { get; set; }
}

/// <summary>Guest session state returned to the web client.</summary>
public sealed class ShareLinkGuestStateDto
{
    public bool IsGuest { get; set; }

    public string? AllowedItemId { get; set; }

    public Guid? ShareId { get; set; }

    public DateTimeOffset? ExpiresAtUtc { get; set; }

    public bool LockdownEnabled { get; set; }

    public string? HiddenSelectors { get; set; }

    public ShareKind? Kind { get; set; }

    public bool WatchParty { get; set; }

    public bool WatchPartyHostOnly { get; set; }
}

/// <summary>An installed plugin, as offered in the guard's exception list.</summary>
public sealed class ShareLinkPluginDto
{
    /// <summary>Gets or sets the plugin id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the plugin's display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether guests may currently reach it.</summary>
    public bool AllowedForGuests { get; set; }
}

/// <summary>ShareLinks API surface.</summary>
[ApiController]
[Route("ShareLinks")]
public sealed class ShareLinksController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly ShareLinkCreationService _creationService;
    private readonly ShareLinkCleanupService _cleanupService;
    private readonly ShareLinkRedemptionService _redemptionService;
    private readonly ShareLinkStore _store;
    private readonly WatchPartyService _watchPartyService;
    private readonly IPluginManager _pluginManager;
    private readonly ILogger<ShareLinksController> _logger;

    /// <summary>Initializes a new instance of the <see cref="ShareLinksController"/> class.</summary>
    public ShareLinksController(
        ILibraryManager libraryManager,
        ShareLinkCreationService creationService,
        ShareLinkCleanupService cleanupService,
        ShareLinkRedemptionService redemptionService,
        ShareLinkStore store,
        WatchPartyService watchPartyService,
        IPluginManager pluginManager,
        ILogger<ShareLinksController> logger)
    {
        _libraryManager = libraryManager;
        _creationService = creationService;
        _cleanupService = cleanupService;
        _redemptionService = redemptionService;
        _store = store;
        _watchPartyService = watchPartyService;
        _pluginManager = pluginManager;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    /// <summary>Serves the client-side ShareLinks script.</summary>
    [HttpGet("ClientScript")]
    [AllowAnonymous]
    public ActionResult ClientScript()
    {
        SetNoStoreHeaders();

        var assembly = typeof(ShareLinksController).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(".Web.sharelinks.js", StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
        {
            return NotFound();
        }

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return NotFound();
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return Content(reader.ReadToEnd(), "application/javascript; charset=utf-8");
    }

    /// <summary>Creates a new share link for an item.</summary>
    [HttpPost("Admin/Create")]
    [Authorize(AuthenticationSchemes = "CustomAuthentication")]
    public async Task<ActionResult<ShareLinkCreateResponse>> Create([FromBody] ShareLinkCreateRequest request, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();
        if (!User.IsInRole("Administrator"))
        {
            return Forbid();
        }

        var config = Config;
        if (!config.Enabled)
        {
            return StatusCode(503, new { error = "ShareLinks is disabled." });
        }

        if (request is null || string.IsNullOrWhiteSpace(request.ItemId))
        {
            _logger.LogWarning("ShareLinks: create rejected, missing itemId.");
            return BadRequest(new { error = "Missing itemId." });
        }

        if (!Guid.TryParse(request.ItemId!.Trim(), out var itemId))
        {
            _logger.LogWarning("ShareLinks: create rejected, itemId {ItemId} is not a GUID.", request.ItemId);
            return BadRequest(new { error = "Invalid itemId." });
        }

        var expiryHours = request.ExpiryHours ?? config.DefaultExpiryHours;
        if (expiryHours <= 0)
        {
            return BadRequest(new { error = "Expiry must be positive." });
        }

        var maxExpiryHours = config.MaxExpiryHours > 0 ? config.MaxExpiryHours : 720;
        if (expiryHours > maxExpiryHours)
        {
            return BadRequest(new { error = $"Expiry exceeds the configured maximum of {maxExpiryHours} hours." });
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            _logger.LogWarning("ShareLinks: create rejected, item {ItemId} not found.", itemId);
            return NotFound(new { error = "Item not found." });
        }

        var kind = ShareableItemPolicy.Classify(item);
        if (!kind.HasValue)
        {
            _logger.LogWarning(
                "ShareLinks: create rejected, item {ItemId} \"{ItemName}\" is a {ItemType}, which cannot be shared.",
                itemId,
                item.Name,
                item.GetType().Name);
            return BadRequest(new { error = "This can't be shared. Share a title, a track, an album, a photo, a book, a collection, a playlist, a folder or a Live TV channel." });
        }

        var watchParty = request.WatchParty == true;
        SessionInfo? hostSession = null;
        var creatorUserId = GetCurrentUserId();
        if (watchParty)
        {
            if (!config.WatchPartyEnabled)
            {
                return BadRequest(new { error = "Watch parties are turned off in the ShareLinks settings." });
            }

            if (!ShareableItemPolicy.SupportsWatchParty(kind.Value))
            {
                return BadRequest(new { error = "Only something that plays can be watched together." });
            }

            hostSession = await _watchPartyService.GetRequestSessionAsync(HttpContext, creatorUserId).ConfigureAwait(false);
            if (hostSession is null)
            {
                return BadRequest(new { error = "Start the watch party from the web client, so it can open a SyncPlay group for you." });
            }
        }

        try
        {
            var oneUse = request.OneUse ?? config.OneUseDefault;
            var creation = await _creationService.CreateAsync(
                item,
                creatorUserId,
                expiryHours,
                oneUse,
                watchParty,
                request.HostOnlyControl ?? true,
                cancellationToken).ConfigureAwait(false);

            if (hostSession is not null)
            {
                try
                {
                    creation.Record.WatchPartyGroupId = _watchPartyService.StartGroup(hostSession, item.Name ?? string.Empty, cancellationToken);
                }
                catch (Exception ex)
                {
                    // A party without a group would be torn down on first use anyway;
                    // do it now and say so, rather than hand out a dead link.
                    _logger.LogWarning(ex, "ShareLinks: could not open a SyncPlay group for record {RecordId}.", creation.Record.Id);
                    await _cleanupService.RevokeAsync(creation.Record.Id, cancellationToken).ConfigureAwait(false);
                    return StatusCode(500, new { error = "Could not open a SyncPlay group for the watch party." });
                }
            }

            var shareUrl = BuildShareUrl(Request, creation.RawToken);
            creation.Record.ShareUrl = shareUrl;
            await _store.UpdateAsync(creation.Record, cancellationToken).ConfigureAwait(false);
            return Ok(new ShareLinkCreateResponse
            {
                ShareUrl = shareUrl,
                Record = ToDto(creation.Record)
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ShareLinks: create failed for item {ItemId}.", itemId);
            return StatusCode(500, new { error = "Failed to create share link." });
        }
    }

    /// <summary>
    /// Puts the calling guest's session into their watch party. The group comes from
    /// the share the guest account belongs to, never from the request.
    /// </summary>
    [HttpPost("WatchParty/Join")]
    [Authorize(AuthenticationSchemes = "CustomAuthentication")]
    public async Task<ActionResult> JoinWatchParty(CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();
        if (!Config.Enabled)
        {
            return StatusCode(503, new { error = "ShareLinks is disabled." });
        }

        var result = await _watchPartyService.JoinAsync(GetCurrentUserId(), HttpContext, cancellationToken).ConfigureAwait(false);
        return result switch
        {
            WatchPartyJoinResult.Joined => Ok(new { status = "joined" }),
            WatchPartyJoinResult.Ended => StatusCode(StatusCodes.Status410Gone, new { status = "ended" }),
            WatchPartyJoinResult.NotShared => Conflict(new { status = "notShared" }),
            WatchPartyJoinResult.NotConnected => StatusCode(425, new { status = "notConnected" }),
            _ => NotFound(new { status = "notAParty" })
        };
    }

    /// <summary>Lists all share links for administrators.</summary>
    [HttpGet("Admin/List")]
    [Authorize(AuthenticationSchemes = "CustomAuthentication")]
    public async Task<ActionResult<IEnumerable<ShareLinkAdminRecordDto>>> List(CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();
        if (!User.IsInRole("Administrator"))
        {
            return Forbid();
        }

        var records = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        return Ok(records.Select(ToDto).ToArray());
    }

    /// <summary>Revokes a share link and triggers cleanup.</summary>
    [HttpPost("Admin/Revoke/{id:guid}")]
    [Authorize(AuthenticationSchemes = "CustomAuthentication")]
    public async Task<ActionResult<ShareLinkAdminRecordDto>> Revoke(Guid id, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();
        if (!User.IsInRole("Administrator"))
        {
            return Forbid();
        }

        var record = await _cleanupService.RevokeAsync(id, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return NotFound(new { error = "Share link not found." });
        }

        return Ok(ToDto(record));
    }

    /// <summary>Removes revoked, expired and failed share links from the store.</summary>
    [HttpPost("Admin/Cleanup")]
    [Authorize(AuthenticationSchemes = "CustomAuthentication")]
    public async Task<ActionResult> Cleanup(CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();
        if (!User.IsInRole("Administrator"))
        {
            return Forbid();
        }

        var removed = await _cleanupService.PurgeFinishedAsync(cancellationToken).ConfigureAwait(false);
        return Ok(new { removed });
    }

    /// <summary>
    /// Lists the installed plugins so the config page can offer them as guard
    /// exceptions. ShareLinks itself is left out: it is always reachable, since the
    /// guest's browser fetches the lockdown script and guest state from it.
    /// </summary>
    [HttpGet("Admin/Plugins")]
    [Authorize(AuthenticationSchemes = "CustomAuthentication")]
    public ActionResult<IEnumerable<ShareLinkPluginDto>> Plugins()
    {
        SetNoStoreHeaders();
        if (!User.IsInRole("Administrator"))
        {
            return Forbid();
        }

        var allowed = Config.GuestAllowedPluginIds ?? Array.Empty<string>();
        var ownId = Plugin.Instance?.Id;

        var plugins = _pluginManager.Plugins
            .Where(plugin => !ownId.HasValue || plugin.Id != ownId.Value)
            .Select(plugin => new ShareLinkPluginDto
            {
                Id = plugin.Id,
                Name = plugin.Name,
                AllowedForGuests = allowed.Any(value => Guid.TryParse(value, out var parsed) && parsed == plugin.Id)
            })
            .OrderBy(plugin => plugin.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Ok(plugins);
    }

    /// <summary>Returns the guest session state for the current authenticated user.</summary>
    [HttpGet("GuestState")]
    [Authorize(AuthenticationSchemes = "CustomAuthentication")]
    public async Task<ActionResult<ShareLinkGuestStateDto>> GuestState(CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        var config = Config;
        var currentUserId = GetCurrentUserId();
        var currentUserName = GetCurrentUserName();

        if (currentUserId != Guid.Empty || !string.IsNullOrWhiteSpace(currentUserName))
        {
            var records = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
            var match = records.FirstOrDefault(record =>
                !IsExpired(record) &&
                IsGuestSessionStatus(record.Status) &&
                (
                    (currentUserId != Guid.Empty && record.GuestUserId.HasValue && record.GuestUserId.Value == currentUserId) ||
                    (!string.IsNullOrWhiteSpace(currentUserName) &&
                     !string.IsNullOrWhiteSpace(record.GuestUserName) &&
                     string.Equals(record.GuestUserName, currentUserName, StringComparison.OrdinalIgnoreCase))
                ));

            if (match is not null)
            {
                await _cleanupService.HealLiveTvTagAsync(match, cancellationToken).ConfigureAwait(false);
                return Ok(new ShareLinkGuestStateDto
                {
                    IsGuest = true,
                    AllowedItemId = match.ItemId,
                    ShareId = match.Id,
                    ExpiresAtUtc = match.ExpiresAtUtc,
                    LockdownEnabled = config.GuestModeLockdownEnabled,
                    HiddenSelectors = config.GuestHiddenSelectors,
                    Kind = match.Kind,
                    WatchParty = match.WatchParty,
                    WatchPartyHostOnly = match.WatchPartyHostOnly
                });
            }
        }

        return Ok(new ShareLinkGuestStateDto
        {
            IsGuest = false,
            LockdownEnabled = config.GuestModeLockdownEnabled,
            HiddenSelectors = config.GuestHiddenSelectors
        });
    }

    /// <summary>Redeems a share link token and returns the bootstrap login page.</summary>
    [HttpGet("Redeem")]
    [AllowAnonymous]
    public async Task<ActionResult> Redeem([FromQuery(Name = "t")] string? token, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();
        if (string.IsNullOrWhiteSpace(token))
        {
            return LinkUnavailablePage(Request);
        }

        var result = await _redemptionService.RedeemAsync(token, Request, cancellationToken).ConfigureAwait(false);
        if (result.AtCapacity)
        {
            return LinkBusyPage();
        }

        if (result.Html is null)
        {
            return LinkUnavailablePage(Request);
        }

        return Content(result.Html, "text/html; charset=utf-8");
    }

    private static ContentResult LinkUnavailablePage(HttpRequest request)
    {
        var pathBase = request.PathBase.Value ?? string.Empty;
        var redirectUrl = $"{pathBase}/web/";

        var redirectUrlJson = JsonSerializer.Serialize(redirectUrl);
        var redirectUrlHtml = System.Net.WebUtility.HtmlEncode(redirectUrl);

        var html = $$"""
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <meta http-equiv="refresh" content="6;url={{redirectUrlHtml}}">
  <title>Link unavailable</title>
  <style>
    body { font-family: system-ui, sans-serif; margin: 0; min-height: 100vh; display: grid; place-items: center; background: #111827; color: #e5e7eb; }
    main { max-width: 36rem; padding: 2rem; }
    .muted { color: #9ca3af; }
    a { color: #60a5fa; }
  </style>
</head>
<body>
<main>
  <div>This share link is no longer valid.</div>
  <div class="muted">Ce lien de partage n'est plus valide.</div>
  <div class="muted">Taking you to the home page...</div>
  <p><a href="{{redirectUrlHtml}}">Open Jellyfin</a></p>
</main>
<script>
setTimeout(function () { window.location.replace({{redirectUrlJson}}); }, 4000);
</script>
</body>
</html>
""";

        return new ContentResult
        {
            StatusCode = StatusCodes.Status404NotFound,
            ContentType = "text/html; charset=utf-8",
            Content = html
        };
    }

    /// <summary>
    /// Served when a multi-use link has as many viewers as it is allowed. The link
    /// itself is still good, so this deliberately invites a retry instead of
    /// looking like a dead link.
    /// </summary>
    private static ContentResult LinkBusyPage()
    {
        var html = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Too many viewers</title>
  <style>
    body { font-family: system-ui, sans-serif; margin: 0; min-height: 100vh; display: grid; place-items: center; background: #111827; color: #e5e7eb; }
    main { max-width: 36rem; padding: 2rem; }
    .muted { color: #9ca3af; }
    a { color: #60a5fa; }
  </style>
</head>
<body>
<main>
  <div>This link is being watched by as many people as it allows right now.</div>
  <div class="muted">Ce lien est deja utilise par autant de personnes qu'il l'autorise.</div>
  <p><a href="">Try again</a></p>
</main>
</body>
</html>
""";

        return new ContentResult
        {
            StatusCode = StatusCodes.Status503ServiceUnavailable,
            ContentType = "text/html; charset=utf-8",
            Content = html
        };
    }

    private static ShareLinkAdminRecordDto ToDto(ShareLinkRecord record)
    {
        return new ShareLinkAdminRecordDto
        {
            Id = record.Id,
            ItemId = record.ItemId,
            ItemNameSnapshot = record.ItemNameSnapshot,
            LibraryId = record.LibraryId,
            CreatedByUserId = record.CreatedByUserId,
            CreatedAtUtc = record.CreatedAtUtc,
            RedeemedAtUtc = record.RedeemedAtUtc,
            ExpiresAtUtc = record.ExpiresAtUtc,
            Status = record.Status,
            GuestUserId = record.GuestUserId,
            GuestUserName = record.GuestUserName,
            AllowedTag = record.AllowedTag,
            OneUse = record.OneUse,
            MetadataTouched = record.MetadataTouched,
            CleanupAttempts = record.CleanupAttempts,
            CleanupError = record.CleanupError,
            ShareUrl = record.ShareUrl,
            Kind = record.Kind,
            WatchParty = record.WatchParty,
            WatchPartyHostOnly = record.WatchPartyHostOnly
        };
    }

    private Guid GetCurrentUserId()
    {
        var claim = User.FindFirst("Jellyfin-UserId")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(claim, out var id) ? id : Guid.Empty;
    }

    private string? GetCurrentUserName()
    {
        return User.FindFirst("Jellyfin-UserName")?.Value
            ?? User.FindFirst(ClaimTypes.Name)?.Value
            ?? User.Identity?.Name;
    }

    private static bool IsExpired(ShareLinkRecord record)
    {
        return record.ExpiresAtUtc <= DateTimeOffset.UtcNow;
    }

    private static bool IsGuestSessionStatus(ShareLinkStatus status)
    {
        return status is ShareLinkStatus.Active or ShareLinkStatus.Redeeming or ShareLinkStatus.Redeemed;
    }

    private void SetNoStoreHeaders()
    {
        Response.Headers["Cache-Control"] = "no-store, no-cache, max-age=0, must-revalidate";
        Response.Headers["Pragma"] = "no-cache";
    }

    private static string BuildShareUrl(Microsoft.AspNetCore.Http.HttpRequest request, string rawToken)
    {
        var config = Config;
        var baseUrl = string.IsNullOrWhiteSpace(config.PublicBaseUrlOverride)
            ? $"{request.Scheme}://{request.Host}{request.PathBase}"
            : config.PublicBaseUrlOverride.TrimEnd('/');

        return $"{baseUrl.TrimEnd('/')}/ShareLinks/Redeem?t={Uri.EscapeDataString(rawToken)}";
    }
}
