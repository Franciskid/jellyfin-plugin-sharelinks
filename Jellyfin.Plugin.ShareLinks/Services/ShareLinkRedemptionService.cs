using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ShareLinks.Models;
using Jellyfin.Plugin.ShareLinks.Storage;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShareLinks.Services;

/// <summary>Handles public share-link redemption and the bootstrap HTML response.</summary>
public sealed class ShareLinkRedemptionService
{
    private readonly ILibraryManager _libraryManager;
    private readonly ShareLinkStore _store;
    private readonly ShareTokenService _tokenService;
    private readonly ItemTagService _itemTagService;
    private readonly JellyfinGuestUserService _guestUserService;
    private readonly ShareLinkCleanupService _cleanupService;
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<ShareLinkRedemptionService> _logger;

    /// <summary>Initializes a new instance of the <see cref="ShareLinkRedemptionService"/> class.</summary>
    public ShareLinkRedemptionService(
        ILibraryManager libraryManager,
        ShareLinkStore store,
        ShareTokenService tokenService,
        ItemTagService itemTagService,
        JellyfinGuestUserService guestUserService,
        ShareLinkCleanupService cleanupService,
        ISessionManager sessionManager,
        ILogger<ShareLinkRedemptionService> logger)
    {
        _libraryManager = libraryManager;
        _store = store;
        _tokenService = tokenService;
        _itemTagService = itemTagService;
        _guestUserService = guestUserService;
        _cleanupService = cleanupService;
        _sessionManager = sessionManager;
        _logger = logger;
    }

    /// <summary>Redeems a token and returns the bootstrap HTML, or null if the token is unusable.</summary>
    public async Task<string?> RedeemAsync(string rawToken, HttpRequest request, CancellationToken cancellationToken)
    {
        var tokenHash = await _tokenService.HashTokenAsync(rawToken, cancellationToken).ConfigureAwait(false);
        if (tokenHash is null)
        {
            return null;
        }

        var record = await _store.GetByTokenHashAsync(tokenHash, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (record.ExpiresAtUtc <= now)
        {
            await HandleTerminalRecordAsync(record, ShareLinkStatus.Expired, "Share link has expired.", cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (record.Status == ShareLinkStatus.Revoked || record.Status == ShareLinkStatus.Failed)
        {
            return null;
        }

        if (!Guid.TryParse(record.ItemId, out var itemId))
        {
            await HandleFailureAsync(record, "Shared item snapshot is invalid.", cancellationToken).ConfigureAwait(false);
            return null;
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            await HandleFailureAsync(record, "Shared item no longer exists.", cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (!string.IsNullOrWhiteSpace(record.AllowedTag))
        {
            await _itemTagService.EnsureTagTreeAsync(item, record.AllowedTag!, cancellationToken).ConfigureAwait(false);
            record.MetadataTouched = true;
        }

        if (record.OneUse && record.Status == ShareLinkStatus.Redeemed)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(record.DeviceId))
        {
            record.DeviceId = Guid.NewGuid().ToString("N");
        }

        record.Status = ShareLinkStatus.Redeeming;
        record.CleanupError = null;
        await _store.UpdateAsync(record, cancellationToken).ConfigureAwait(false);

        // The account still needs a password so it can never be authenticated with a blank
        // login; it is generated fresh on every redemption and is never stored or sent
        // anywhere. The browser only ever receives a server-minted session token.
        var password = JellyfinGuestUserService.GeneratePassword();
        if (string.IsNullOrWhiteSpace(record.GuestUserName))
        {
            record.GuestUserName = JellyfinGuestUserService.BuildGuestUsername(record);
        }

        AuthenticationResult authResult;
        try
        {
            var user = await _guestUserService.EnsureGuestUserAsync(record, password, cancellationToken).ConfigureAwait(false);
            record.GuestUserId = user.Id;
            record.GuestUserName = user.Username;

            authResult = await _sessionManager.AuthenticateDirect(new AuthenticationRequest
            {
                Username = record.GuestUserName,
                UserId = record.GuestUserId.Value,
                App = "ShareLinks",
                AppVersion = "1.0.0",
                DeviceId = record.DeviceId,
                DeviceName = "ShareLinks",
                RemoteEndPoint = request.HttpContext.Connection.RemoteIpAddress?.ToString()
            }).ConfigureAwait(false);

            record.RedeemedAtUtc ??= now;
            record.Status = ShareLinkStatus.Redeemed;
            record.CleanupError = null;
            await _store.UpdateAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            record.Status = ShareLinkStatus.Failed;
            record.CleanupError = ex.Message;
            await _store.UpdateAsync(record, cancellationToken).ConfigureAwait(false);
            _logger.LogWarning(ex, "ShareLinks: failed to prepare guest session for record {RecordId}.", record.Id);
            await TryCleanupAsync(record, cancellationToken).ConfigureAwait(false);
            return null;
        }

        return BuildBootstrapHtml(request, authResult, itemId);
    }

    private async Task HandleTerminalRecordAsync(ShareLinkRecord record, ShareLinkStatus terminalStatus, string reason, CancellationToken cancellationToken)
    {
        record.Status = terminalStatus;
        record.CleanupError = reason;
        await _store.UpdateAsync(record, cancellationToken).ConfigureAwait(false);
        await TryCleanupAsync(record, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleFailureAsync(ShareLinkRecord record, string reason, CancellationToken cancellationToken)
    {
        record.Status = ShareLinkStatus.Failed;
        record.CleanupError = reason;
        await _store.UpdateAsync(record, cancellationToken).ConfigureAwait(false);
        await TryCleanupAsync(record, cancellationToken).ConfigureAwait(false);
    }

    private async Task TryCleanupAsync(ShareLinkRecord record, CancellationToken cancellationToken)
    {
        try
        {
            await _cleanupService.CleanupRecordAsync(record.Id, true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ShareLinks: cleanup after failed redemption did not complete for record {RecordId}.", record.Id);
        }
    }

    private static string BuildBootstrapHtml(HttpRequest request, AuthenticationResult authResult, Guid itemId)
    {
        var pathBase = request.PathBase.Value ?? string.Empty;
        var redirectUrl = $"{pathBase}/web/index.html#/details?id={Uri.EscapeDataString(itemId.ToString("D"))}";

        var accessTokenJson = JsonSerializer.Serialize(authResult.AccessToken);
        var userIdJson = JsonSerializer.Serialize(authResult.User.Id.ToString("N"));
        var redirectUrlJson = JsonSerializer.Serialize(redirectUrl);
        var infoUrlJson = JsonSerializer.Serialize($"{pathBase}/System/Info/Public");
        var pathBaseJson = JsonSerializer.Serialize(pathBase);

        return $$"""
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Signing in...</title>
  <style>
    body { font-family: system-ui, sans-serif; margin: 0; min-height: 100vh; display: grid; place-items: center; background: #111827; color: #e5e7eb; }
    main { max-width: 36rem; padding: 2rem; }
    .muted { color: #9ca3af; }
  </style>
</head>
<body>
<main>
  <div>Signing you in...</div>
  <div class="muted" id="status">Preparing temporary access.</div>
</main>
<script>
(async () => {
  const redirectUrl = {{redirectUrlJson}};
  const accessToken = {{accessTokenJson}};
  const userId = {{userIdJson}};

  document.getElementById("status").textContent = "Opening your title.";

  const info = await fetch({{infoUrlJson}}, {
    credentials: "same-origin",
    headers: { "Accept": "application/json" }
  }).then((r) => r.json());

  const serverAddress = window.location.origin + {{pathBaseJson}};
  const credentials = {
    Servers: [
      {
        ManualAddress: serverAddress,
        manualAddressOnly: true,
        Name: info.ServerName || "Jellyfin",
        Id: info.Id,
        LastConnectionMode: 1,
        AccessToken: accessToken,
        UserId: userId,
        DateLastAccessed: Date.now()
      }
    ]
  };

  try {
    localStorage.setItem("jellyfin_credentials", JSON.stringify(credentials));
  } catch (_) {
    // If storage is blocked the redirect lands on the login screen.
  }

  window.location.replace(redirectUrl + "&serverId=" + encodeURIComponent(info.Id));
})().catch((error) => {
  console.error(error);
  document.getElementById("status").textContent = "Sign-in failed.";
});
</script>
</body>
</html>
""";
    }
}
