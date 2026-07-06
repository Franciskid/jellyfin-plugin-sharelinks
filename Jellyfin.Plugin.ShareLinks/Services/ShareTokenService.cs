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

    /// <summary>Computes the stored hash for a presented token.</summary>
    public async Task<string> HashTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("Token cannot be empty.", nameof(token));
        }

        var secret = await GetSecretAsync(cancellationToken).ConfigureAwait(false);
        var tokenBytes = Base64UrlDecode(token);
        return ComputeHash(secret, tokenBytes);
    }

    /// <summary>Validates a token against an expected hash.</summary>
    public async Task<bool> VerifyTokenAsync(string token, string expectedHash, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expectedHash))
        {
            return false;
        }

        try
        {
            var actualHash = await HashTokenAsync(token, cancellationToken).ConfigureAwait(false);
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(actualHash),
                Encoding.UTF8.GetBytes(expectedHash));
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Encrypts sensitive text using the shared plugin secret.</summary>
    public async Task<string> ProtectStringAsync(string value, CancellationToken cancellationToken = default)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        var secret = await GetSecretAsync(cancellationToken).ConfigureAwait(false);
        var plaintext = Encoding.UTF8.GetBytes(value);
        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[16];

        using (var aes = new AesGcm(secret, 16))
        {
            aes.Encrypt(nonce, plaintext, cipher, tag);
        }

        var payload = new byte[nonce.Length + cipher.Length + tag.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
        Buffer.BlockCopy(cipher, 0, payload, nonce.Length, cipher.Length);
        Buffer.BlockCopy(tag, 0, payload, nonce.Length + cipher.Length, tag.Length);
        return Base64UrlEncode(payload);
    }

    /// <summary>Decrypts a sensitive string protected by <see cref="ProtectStringAsync"/>.</summary>
    public async Task<string> UnprotectStringAsync(string protectedValue, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            throw new ArgumentException("Protected value cannot be empty.", nameof(protectedValue));
        }

        var payload = Base64UrlDecode(protectedValue);
        if (payload.Length < 12 + 16)
        {
            throw new CryptographicException("Protected payload is invalid.");
        }

        var secret = await GetSecretAsync(cancellationToken).ConfigureAwait(false);
        var nonce = payload[..12];
        var tag = payload[^16..];
        var cipher = payload[12..^16];
        var plaintext = new byte[cipher.Length];

        using (var aes = new AesGcm(secret, 16))
        {
            aes.Decrypt(nonce, cipher, tag, plaintext);
        }

        return Encoding.UTF8.GetString(plaintext);
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
