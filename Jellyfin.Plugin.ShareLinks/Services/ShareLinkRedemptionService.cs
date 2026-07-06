using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ShareLinks.Models;
using Jellyfin.Plugin.ShareLinks.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
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
    private readonly ILogger<ShareLinkRedemptionService> _logger;

    /// <summary>Initializes a new instance of the <see cref="ShareLinkRedemptionService"/> class.</summary>
    public ShareLinkRedemptionService(
        ILibraryManager libraryManager,
        ShareLinkStore store,
        ShareTokenService tokenService,
        ItemTagService itemTagService,
        JellyfinGuestUserService guestUserService,
        ShareLinkCleanupService cleanupService,
        ILogger<ShareLinkRedemptionService> logger)
    {
        _libraryManager = libraryManager;
        _store = store;
        _tokenService = tokenService;
        _itemTagService = itemTagService;
        _guestUserService = guestUserService;
        _cleanupService = cleanupService;
        _logger = logger;
    }

    /// <summary>Redeems a token and returns the bootstrap HTML, or null if the token is unusable.</summary>
    public async Task<string?> RedeemAsync(string rawToken, HttpRequest request, CancellationToken cancellationToken)
    {
        var tokenHash = await _tokenService.HashTokenAsync(rawToken, cancellationToken).ConfigureAwait(false);
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
            await _itemTagService.EnsureTagAsync(item, record.AllowedTag!, cancellationToken).ConfigureAwait(false);
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

        var password = await GetOrCreatePasswordAsync(record, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(record.GuestUserName))
        {
            record.GuestUserName = JellyfinGuestUserService.BuildGuestUsername(record);
        }

        try
        {
            var user = await _guestUserService.EnsureGuestUserAsync(record, password, cancellationToken).ConfigureAwait(false);
            record.GuestUserId = user.Id;
            record.GuestUserName = user.Username;
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

        return BuildBootstrapHtml(request, record, password, itemId);
    }

    private async Task<string> GetOrCreatePasswordAsync(ShareLinkRecord record, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(record.GuestPasswordEncrypted))
        {
            try
            {
                return await _tokenService.UnprotectStringAsync(record.GuestPasswordEncrypted, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ShareLinks: stored guest password could not be decrypted for record {RecordId}; generating a replacement.", record.Id);
            }
        }

        var password = JellyfinGuestUserService.GeneratePassword();
        record.GuestPasswordEncrypted = await _tokenService.ProtectStringAsync(password, cancellationToken).ConfigureAwait(false);
        record.Status = ShareLinkStatus.Redeeming;
        record.CleanupError = null;
        await _store.UpdateAsync(record, cancellationToken).ConfigureAwait(false);
        return password;
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

    private static string BuildBootstrapHtml(HttpRequest request, ShareLinkRecord record, string password, Guid itemId)
    {
        var pathBase = request.PathBase.Value ?? string.Empty;
        var authUrl = $"{pathBase}/Users/AuthenticateByName";
        var redirectUrl = $"{pathBase}/web/index.html#!/details?id={Uri.EscapeDataString(itemId.ToString("D"))}";
        var username = record.GuestUserName ?? JellyfinGuestUserService.BuildGuestUsername(record);
        var deviceId = record.DeviceId ?? string.Empty;

        var authJson = JsonSerializer.Serialize(new
        {
            Username = username,
            Pw = password
        });

        var authUrlJson = JsonSerializer.Serialize(authUrl);
        var redirectUrlJson = JsonSerializer.Serialize(redirectUrl);
        var usernameJson = JsonSerializer.Serialize(username);
        var deviceIdJson = JsonSerializer.Serialize(deviceId);

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
  const authUrl = {{authUrlJson}};
  const redirectUrl = {{redirectUrlJson}};
  const username = {{usernameJson}};
  const deviceId = {{deviceIdJson}} || crypto.randomUUID().replace(/-/g, "");

  document.getElementById("status").textContent = "Authenticating " + username + ".";

  const response = await fetch(authUrl, {
    method: "POST",
    credentials: "same-origin",
    headers: {
      "Content-Type": "application/json",
      "Accept": "application/json",
      "X-Emby-Authorization": `MediaBrowser Client="ShareLinks", Device="ShareLinks", DeviceId="${deviceId}", Version="1.0.0"`
    },
    body: {{authJson}}
  });

  if (!response.ok) {
    throw new Error(`Authentication failed (${response.status})`);
  }

  const auth = await response.json();
  const accessToken = auth.AccessToken ?? auth.accessToken ?? "";
  const userId = auth.User?.Id ?? auth.user?.Id ?? auth.UserId ?? auth.userId ?? "";
  const userName = auth.User?.Name ?? auth.user?.Name ?? auth.UserName ?? auth.userName ?? username;
  const snapshot = {
    AccessToken: accessToken,
    UserId: userId,
    UserName: userName,
    ServerUrl: window.location.origin
  };

  try {
    for (const key of ["jellyfinCredentials", "jellyfin_credentials", "jellyfin-credentials"]) {
      localStorage.setItem(key, JSON.stringify(snapshot));
    }
    localStorage.setItem("jellyfin.server", window.location.origin);
  } catch (_) {
    // Best effort only. Jellyfin Web storage format should be verified live.
  }

  window.location.replace(redirectUrl);
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
