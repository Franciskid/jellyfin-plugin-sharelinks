using System;
using System.Linq;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.ShareLinks.Configuration;
using Jellyfin.Plugin.ShareLinks.Models;
using Jellyfin.Plugin.ShareLinks.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.ShareLinks.Security;

/// <summary>How much a share's creator is trusted right now.</summary>
public enum CreatorStanding
{
    /// <summary>An administrator, or a record from before creators were tracked: nothing to check.</summary>
    Unrestricted,

    /// <summary>A regular user allowed to share. The share never reaches past what they can open.</summary>
    Restricted,

    /// <summary>The creator is gone or may no longer share, so the share must end.</summary>
    Revoked
}

/// <summary>
/// The rules for regular users creating share links. An administrator can open
/// everything, so sharing never widened anyone's access. A regular user cannot, so
/// each of their shares is held to what that user can open themselves, for as long
/// as it lives.
/// </summary>
public static class ShareCreatorPolicy
{
    /// <summary>Gets a value indicating whether a user is an administrator.</summary>
    public static bool IsAdministrator(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.HasPermission(PermissionKind.IsAdministrator);
    }

    /// <summary>
    /// Gets a value indicating whether an account is one of this plugin's guests. A
    /// guest is a regular Jellyfin user too, and one that could share would hand its
    /// own access on with a fresh expiry.
    /// </summary>
    public static bool IsGuestAccount(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return string.Equals(user.AuthenticationProviderId, GuestAuthenticationProvider.ProviderId, StringComparison.Ordinal);
    }

    /// <summary>Gets a value indicating whether a user may create share links at all.</summary>
    public static bool CanCreate(PluginConfiguration config, User? user)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (user is null || IsGuestAccount(user) || user.HasPermission(PermissionKind.IsDisabled))
        {
            return false;
        }

        if (IsAdministrator(user))
        {
            return true;
        }

        if (config.AllowAllUsersToShare)
        {
            return true;
        }

        return (config.SharingUserIds ?? Array.Empty<string>())
            .Any(value => Guid.TryParse(value, out var id) && id.Equals(user.Id));
    }

    /// <summary>
    /// Gets a value indicating whether a regular user can open an item themselves:
    /// library access, parental rating, blocked tags and playlist privacy, the same
    /// test Jellyfin's own API applies before it answers for an item.
    /// </summary>
    public static bool CanOpen(User user, BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(item);

        try
        {
            return item.IsVisibleStandalone(user);
        }
        catch (Exception)
        {
            // Fail closed: an item whose visibility cannot be worked out is not shared.
            return false;
        }
    }

    /// <summary>Gets a value indicating whether a regular user may share this item as this kind.</summary>
    public static bool CanShareItem(User user, BaseItem item, ShareKind kind)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (kind == ShareKind.LiveTv && !user.HasPermission(PermissionKind.EnableLiveTvAccess))
        {
            return false;
        }

        return user.HasPermission(PermissionKind.EnableMediaPlayback) && CanOpen(user, item);
    }

    /// <summary>Gets a value indicating whether a regular user may open a SyncPlay group.</summary>
    public static bool CanHostWatchParty(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.SyncPlayAccess == SyncPlayUserAccessType.CreateAndJoinGroups;
    }

    /// <summary>
    /// Works out where a share's creator stands today. Asked again on every use of
    /// the link, so taking a user's sharing right away, or deleting them, ends what
    /// they shared.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="record">The share.</param>
    /// <param name="userManager">Jellyfin's user manager.</param>
    /// <param name="creator">The creator, when the standing is <see cref="CreatorStanding.Restricted"/>.</param>
    public static CreatorStanding Evaluate(PluginConfiguration config, ShareLinkRecord record, IUserManager userManager, out User? creator)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(userManager);

        creator = null;
        var user = record.CreatedByUserId.HasValue ? userManager.GetUserById(record.CreatedByUserId.Value) : null;
        if (user is null)
        {
            // Admin links have always outlived their creator. A regular user's do not:
            // nobody is left whose access the share could be held to.
            return record.RestrictedCreator ? CreatorStanding.Revoked : CreatorStanding.Unrestricted;
        }

        if (IsAdministrator(user))
        {
            return CreatorStanding.Unrestricted;
        }

        if (!CanCreate(config, user))
        {
            return CreatorStanding.Revoked;
        }

        creator = user;
        return CreatorStanding.Restricted;
    }
}
