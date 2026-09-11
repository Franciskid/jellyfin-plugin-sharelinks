using System;
using System.Reflection;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.ShareLinks.Services;

/// <summary>
/// Binds <c>IUserManager.ChangePassword</c> at runtime instead of at compile time.
/// </summary>
internal static class UserManagerCompat
{
    // Jellyfin 10.11.9 changed ChangePassword's parameter from the User itself to its Guid id.
    // The plugin compiles against 10.11.0 but has to load on servers both before and after that
    // change, and a call compiled against either signature throws MissingMethodException on a
    // server that only has the other one. Resolving the method once with reflection and calling
    // it through a delegate lets the plugin bind to whichever overload the running server has.
    private static readonly Lazy<Func<IUserManager, User, string, Task>> ChangePasswordInvoker =
        new(ResolveChangePassword);

    /// <summary>
    /// Changes a user's password, binding to whichever <c>IUserManager.ChangePassword</c>
    /// overload the running Jellyfin server exposes.
    /// </summary>
    /// <param name="userManager">The user manager to invoke.</param>
    /// <param name="user">The user whose password is being changed.</param>
    /// <param name="newPassword">The new password.</param>
    /// <returns>A task that completes when the password has been changed.</returns>
    public static Task ChangePasswordAsync(this IUserManager userManager, User user, string newPassword)
    {
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(user);

        return ChangePasswordInvoker.Value(userManager, user, newPassword);
    }

    /// <summary>Resolves the available <c>ChangePassword</c> overload into a single callable shape.</summary>
    private static Func<IUserManager, User, string, Task> ResolveChangePassword()
    {
        // Prefer the current (10.11.9+) overload first.
        var guidOverload = typeof(IUserManager).GetMethod(
            nameof(IUserManager.ChangePassword),
            new[] { typeof(Guid), typeof(string) });
        if (guidOverload is not null)
        {
            var call = guidOverload.CreateDelegate<Func<IUserManager, Guid, string, Task>>();
            return (manager, user, password) => call(manager, user.Id, password);
        }

        // Fall back to the older (10.11.0 - 10.11.8) overload.
        var userOverload = typeof(IUserManager).GetMethod(
            nameof(IUserManager.ChangePassword),
            new[] { typeof(User), typeof(string) });
        if (userOverload is not null)
        {
            return userOverload.CreateDelegate<Func<IUserManager, User, string, Task>>();
        }

        return (_, _, _) => throw new InvalidOperationException(
            "This Jellyfin version has neither IUserManager.ChangePassword(Guid, string) nor ChangePassword(User, string).");
    }
}
