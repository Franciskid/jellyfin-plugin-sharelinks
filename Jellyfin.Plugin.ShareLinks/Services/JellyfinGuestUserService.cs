using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ShareLinks.Models;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShareLinks.Services;

/// <summary>Creates and tears down temporary Jellyfin guest users.</summary>
public sealed class JellyfinGuestUserService
{
    private readonly IUserManager _userManager;
    private readonly ILogger<JellyfinGuestUserService> _logger;

    /// <summary>Initializes a new instance of the <see cref="JellyfinGuestUserService"/> class.</summary>
    public JellyfinGuestUserService(IUserManager userManager, ILogger<JellyfinGuestUserService> logger)
    {
        _userManager = userManager;
        _logger = logger;
    }

    /// <summary>Builds the temporary guest username for a share record.</summary>
    public static string BuildGuestUsername(ShareLinkRecord record)
    {
        var prefix = Plugin.Instance?.Configuration.GuestUsernamePrefix ?? "share-";
        return $"{prefix}{record.Id:N}";
    }

    /// <summary>Generates a strong random password suitable for a temporary guest user.</summary>
    public static string GeneratePassword()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    /// <summary>Ensures the temporary guest user exists and has the correct policy and password.</summary>
    public async Task<User> EnsureGuestUserAsync(ShareLinkRecord record, string password, CancellationToken cancellationToken)
    {
        if (record is null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new ArgumentException("Password cannot be empty.", nameof(password));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var username = record.GuestUserName;
        if (string.IsNullOrWhiteSpace(username))
        {
            username = BuildGuestUsername(record);
            record.GuestUserName = username;
        }

        var user = _userManager.GetUserByName(username);
        if (user is null)
        {
            user = await _userManager.CreateUserAsync(username).ConfigureAwait(false);
            if (user is null)
            {
                throw new InvalidOperationException($"Unable to create temporary guest user '{username}'.");
            }
        }

        // The password must be set before the policy update: UpdatePolicyAsync bumps the
        // user's EF concurrency token server side, and ChangePassword with a stale instance
        // then throws DbUpdateConcurrencyException. The password is only a fallback - the
        // policy hands the account to GuestAuthenticationProvider, which refuses every
        // interactive sign-in - but it means the account is never reachable with a blank
        // password either.
        await _userManager.ChangePassword(user, password).ConfigureAwait(false);
        await ApplyPolicyAsync(user, record, disabled: false).ConfigureAwait(false);

        user = _userManager.GetUserById(user.Id) ?? user;
        _logger.LogInformation("ShareLinks: ensured guest user {UserName} for record {RecordId}.", user.Username, record.Id);
        return user;
    }

    /// <summary>Disables a temporary guest user before deletion.</summary>
    public async Task DisableGuestUserAsync(ShareLinkRecord record, CancellationToken cancellationToken)
    {
        var user = FindRecordUser(record);
        if (user is null)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await ApplyPolicyAsync(user, record, disabled: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ShareLinks: failed to disable guest user {UserName} for record {RecordId}.", user.Username, record.Id);
        }
    }

    /// <summary>Deletes a temporary guest user if it exists.</summary>
    public async Task DeleteGuestUserAsync(ShareLinkRecord record, CancellationToken cancellationToken)
    {
        var user = FindRecordUser(record);
        if (user is null)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await _userManager.DeleteUserAsync(user.Id).ConfigureAwait(false);
            _logger.LogInformation("ShareLinks: deleted guest user {UserName} for record {RecordId}.", user.Username, record.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ShareLinks: failed to delete guest user {UserName} for record {RecordId}.", user.Username, record.Id);
            throw;
        }
    }

    private User? FindRecordUser(ShareLinkRecord record)
    {
        if (record.GuestUserId.HasValue)
        {
            var user = _userManager.GetUserById(record.GuestUserId.Value);
            if (user is not null)
            {
                return user;
            }
        }

        return string.IsNullOrWhiteSpace(record.GuestUserName)
            ? null
            : _userManager.GetUserByName(record.GuestUserName);
    }

    private async Task ApplyPolicyAsync(User user, ShareLinkRecord record, bool disabled)
    {
        var config = Plugin.Instance!.Configuration;
        var policy = new UserPolicy
        {
            // Hand the account to a provider that refuses interactive sign-in. If the
            // plugin is ever disabled the id stops resolving and Jellyfin falls back to
            // its own InvalidAuthProvider, which also refuses, so this fails closed.
            AuthenticationProviderId = GuestAuthenticationProvider.ProviderId,
            PasswordResetProviderId = user.PasswordResetProviderId,
            AllowedTags = string.IsNullOrWhiteSpace(record.AllowedTag)
                ? Array.Empty<string>()
                : new[] { record.AllowedTag! },
            BlockedTags = Array.Empty<string>(),
            IsAdministrator = false,
            IsHidden = true,
            IsDisabled = disabled,
            EnableCollectionManagement = false,
            EnableSubtitleManagement = false,
            EnableLyricManagement = false,
            EnableUserPreferenceAccess = false,
            EnableSharedDeviceControl = false,
            EnableRemoteAccess = true,
            EnableRemoteControlOfOtherUsers = false,
            EnableLiveTvManagement = false,
            EnableLiveTvAccess = false,
            EnableMediaPlayback = true,
            EnableAudioPlaybackTranscoding = config.AllowTranscoding,
            EnableVideoPlaybackTranscoding = config.AllowTranscoding,
            EnablePlaybackRemuxing = config.AllowRemuxing,
            ForceRemoteSourceTranscoding = false,
            EnableContentDeletion = false,
            EnableContentDeletionFromFolders = Array.Empty<string>(),
            EnableContentDownloading = false,
            EnableSyncTranscoding = false,
            EnableMediaConversion = false,
            EnableAllChannels = false,
            EnabledChannels = Array.Empty<Guid>(),
            EnableAllDevices = true,
            EnabledDevices = Array.Empty<string>(),
            EnableAllFolders = true,
            EnabledFolders = Array.Empty<Guid>(),
            EnablePublicSharing = false,
            LoginAttemptsBeforeLockout = -1,
            // One viewer for a one-use link. A multi-use link needs a session per
            // viewer, and 0 is how Jellyfin spells "no limit" in its session check.
            MaxActiveSessions = record.OneUse ? 1 : 0,
            BlockUnratedItems = Array.Empty<UnratedItem>()
        };

        await _userManager.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
