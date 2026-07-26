using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Authentication;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShareLinks.Services;

/// <summary>
/// The authentication provider assigned to ShareLinks guest accounts. It refuses
/// every interactive sign-in, so a guest account cannot be used on the normal
/// login page even if its name and password were to leak. Guest sessions are
/// minted server side through <c>ISessionManager.AuthenticateDirect</c>, which
/// does not enforce a password and so never reaches a provider at all.
/// </summary>
public sealed class GuestAuthenticationProvider : IAuthenticationProvider
{
    private readonly ILogger<GuestAuthenticationProvider> _logger;

    /// <summary>Initializes a new instance of the <see cref="GuestAuthenticationProvider"/> class.</summary>
    public GuestAuthenticationProvider(ILogger<GuestAuthenticationProvider> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets the value Jellyfin stores on a user to select this provider. Jellyfin
    /// matches it against the provider's full type name.
    /// </summary>
    public static string ProviderId => typeof(GuestAuthenticationProvider).FullName!;

    /// <inheritdoc />
    public string Name => "ShareLinks guest accounts (blocks sign-in)";

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public Task<ProviderAuthenticationResult> Authenticate(string username, string password)
    {
        _logger.LogWarning("ShareLinks: refused an interactive sign-in attempt for guest account {UserName}.", username);
        return Task.FromException<ProviderAuthenticationResult>(
            new AuthenticationException("ShareLinks guest accounts cannot sign in interactively."));
    }

    /// <summary>
    /// Reports the account as having a password so nothing offers it as a
    /// passwordless login.
    /// </summary>
    public bool HasPassword(User user) => true;

    /// <inheritdoc />
    public Task ChangePassword(User user, string newPassword) => Task.CompletedTask;
}
