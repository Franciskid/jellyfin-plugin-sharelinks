using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ShareLinks.Configuration;
using Jellyfin.Plugin.ShareLinks.Models;
using Jellyfin.Plugin.ShareLinks.Services;
using Jellyfin.Plugin.ShareLinks.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
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
    private readonly ILogger<ShareLinksController> _logger;

    /// <summary>Initializes a new instance of the <see cref="ShareLinksController"/> class.</summary>
    public ShareLinksController(
        ILibraryManager libraryManager,
        ShareLinkCreationService creationService,
        ShareLinkCleanupService cleanupService,
        ShareLinkRedemptionService redemptionService,
        ShareLinkStore store,
        ILogger<ShareLinksController> logger)
    {
        _libraryManager = libraryManager;
        _creationService = creationService;
        _cleanupService = cleanupService;
        _redemptionService = redemptionService;
        _store = store;
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

        var effectiveMaxExpiryHours = Math.Max(config.MaxExpiryHours, 720);
        if (expiryHours > effectiveMaxExpiryHours)
        {
            return BadRequest(new { error = $"Expiry exceeds the configured maximum of {effectiveMaxExpiryHours} hours." });
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            _logger.LogWarning("ShareLinks: create rejected, item {ItemId} not found.", itemId);
            return NotFound(new { error = "Item not found." });
        }

        if (item.IsFolder)
        {
            _logger.LogWarning("ShareLinks: create rejected, item {ItemId} \"{ItemName}\" is a folder or library, not shareable media.", itemId, item.Name);
            return BadRequest(new { error = "Only a movie or episode can be shared, not a folder or library. Open the title's page and try again." });
        }

        try
        {
            var creatorUserId = GetCurrentUserId();
            var oneUse = request.OneUse ?? config.OneUseDefault;
            var creation = await _creationService.CreateAsync(item, creatorUserId, expiryHours, oneUse, cancellationToken).ConfigureAwait(false);
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
                return Ok(new ShareLinkGuestStateDto
                {
                    IsGuest = true,
                    AllowedItemId = match.ItemId,
                    ShareId = match.Id,
                    ExpiresAtUtc = match.ExpiresAtUtc,
                    LockdownEnabled = config.GuestModeLockdownEnabled,
                    HiddenSelectors = config.GuestHiddenSelectors
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
            return NotFound();
        }

        var html = await _redemptionService.RedeemAsync(token, Request, cancellationToken).ConfigureAwait(false);
        if (html is null)
        {
            return NotFound();
        }

        return Content(html, "text/html; charset=utf-8");
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
            ShareUrl = record.ShareUrl
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
