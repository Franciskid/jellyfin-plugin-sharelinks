using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ShareLinks.Models;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShareLinks.Services;

/// <summary>
/// Generates raw share tokens and their persisted HMAC hashes.
/// </summary>
public sealed class ShareTokenService
{
    private readonly string _secretPath;
    private readonly ILogger<ShareTokenService> _logger;
    private readonly SemaphoreSlim _secretGate = new(1, 1);
    private byte[]? _secretKey;

    /// <summary>Initializes a new instance of the <see cref="ShareTokenService"/> class.</summary>
    public ShareTokenService(IApplicationPaths applicationPaths, ILogger<ShareTokenService> logger)
    {
        _secretPath = Path.Combine(applicationPaths.DataPath, "sharelinks", "token-secret.key");
        _logger = logger;
    }

    /// <summary>Creates a new 256-bit token and its HMAC hash.</summary>
    public async Task<ShareTokenMaterial> GenerateAsync(CancellationToken cancellationToken = default)
    {
        var secret = await GetSecretAsync(cancellationToken).ConfigureAwait(false);
        var tokenBytes = new byte[32];
        RandomNumberGenerator.Fill(tokenBytes);

        var token = Base64UrlEncode(tokenBytes);
        var hash = ComputeHash(secret, tokenBytes);

        return new ShareTokenMaterial
        {
            Token = token,
            TokenHash = hash
        };
    }

    /// <summary>
    /// Computes the stored hash for a presented token, or <see langword="null"/> if the token
    /// is missing or not well-formed base64url (treated as "no match" rather than an error).
    /// </summary>
    public async Task<string?> HashTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        byte[] tokenBytes;
        try
        {
            tokenBytes = Base64UrlDecode(token);
        }
        catch (FormatException)
        {
            return null;
        }

        var secret = await GetSecretAsync(cancellationToken).ConfigureAwait(false);
        return ComputeHash(secret, tokenBytes);
    }

    /// <summary>Validates a token against an expected hash.</summary>
    public async Task<bool> VerifyTokenAsync(string token, string expectedHash, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expectedHash))
        {
            return false;
        }

        var actualHash = await HashTokenAsync(token, cancellationToken).ConfigureAwait(false);
        if (actualHash is null)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(actualHash),
            Encoding.UTF8.GetBytes(expectedHash));
    }

    private async Task<byte[]> GetSecretAsync(CancellationToken cancellationToken)
    {
        if (_secretKey is not null)
        {
            return _secretKey;
        }

        await _secretGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_secretKey is not null)
            {
                return _secretKey;
            }

            if (File.Exists(_secretPath))
            {
                try
                {
                    var secretText = await File.ReadAllTextAsync(_secretPath, cancellationToken).ConfigureAwait(false);
                    _secretKey = Base64UrlDecode(secretText.Trim());
                    if (_secretKey.Length >= 16)
                    {
                        return _secretKey;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ShareLinks: could not load the token secret; a new one will be generated.");
                }
            }

            var generated = new byte[32];
            RandomNumberGenerator.Fill(generated);
            Directory.CreateDirectory(Path.GetDirectoryName(_secretPath)!);
            await File.WriteAllTextAsync(_secretPath, Base64UrlEncode(generated), cancellationToken).ConfigureAwait(false);
            _secretKey = generated;
            return _secretKey;
        }
        finally
        {
            _secretGate.Release();
        }
    }

    private static string ComputeHash(byte[] secret, ReadOnlySpan<byte> tokenBytes)
    {
        using var hmac = new HMACSHA256(secret);
        return Base64UrlEncode(hmac.ComputeHash(tokenBytes.ToArray()));
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2:
                padded += "==";
                break;
            case 3:
                padded += "=";
                break;
        }

        return Convert.FromBase64String(padded);
    }
}
