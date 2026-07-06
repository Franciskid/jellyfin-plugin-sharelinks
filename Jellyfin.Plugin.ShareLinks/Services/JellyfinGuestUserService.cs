using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ShareLinks.Configuration;
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
    public async Task<dynamic> EnsureGuestUserAsync(ShareLinkRecord record, string password, CancellationToken cancellationToken)
    {
        if (record is null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new ArgumentException("Password cannot be empty.", nameof(password));
        }

        var username = record.GuestUserName;
        if (string.IsNullOrWhiteSpace(username))
        {
            username = BuildGuestUsername(record);
            record.GuestUserName = username;
        }

        object? user = _userManager.GetUserByName(username);
        if (user is null)
        {
            user = await InvokeUserManagerAsync<object?>(
                    "create user",
                    cancellationToken,
                    new InvocationCandidate("CreateUserAsync", new object?[] { username }),
                    new InvocationCandidate("CreateUser", new object?[] { username }))
                .ConfigureAwait(false) ?? _userManager.GetUserByName(username);

            if (user is null)
            {
                throw new InvalidOperationException($"Unable to create temporary guest user '{username}'.");
            }
        }

        var existingUserId = GetUserId(user);
        if (existingUserId != Guid.Empty)
        {
            user = _userManager.GetUserById(existingUserId) ?? user;
        }

        // The password must be changed before the policy update: UpdatePolicyAsync bumps the
        // user's EF concurrency token server-side, and ChangePassword with a stale instance
        // throws DbUpdateConcurrencyException.
        await ChangePasswordAsync(user, password, cancellationToken).ConfigureAwait(false);
        await ApplyPolicyAsync(user, record, disabled: false, cancellationToken).ConfigureAwait(false);

        var userId = GetUserId(user);
        if (userId == Guid.Empty)
        {
            throw new InvalidOperationException("ShareLinks: created guest user did not expose a valid Id.");
        }

        user = _userManager.GetUserById(userId) ?? user;
        _logger.LogInformation("ShareLinks: ensured guest user {UserName} for record {RecordId}.", GetUserName(user), record.Id);
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

        try
        {
            await ApplyPolicyAsync(user, record, disabled: true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ShareLinks: failed to disable guest user {UserName} for record {RecordId}.", GetUserName(user), record.Id);
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

        try
        {
            await DeleteUserAsync(user, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("ShareLinks: deleted guest user {UserName} for record {RecordId}.", GetUserName(user), record.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ShareLinks: failed to delete guest user {UserName} for record {RecordId}.", GetUserName(user), record.Id);
            throw;
        }
    }

    private object? FindRecordUser(ShareLinkRecord record)
    {
        if (record.GuestUserId.HasValue)
        {
            var user = _userManager.GetUserById(record.GuestUserId.Value);
            if (user is not null)
            {
                return user;
            }
        }

        if (!string.IsNullOrWhiteSpace(record.GuestUserName))
        {
            return _userManager.GetUserByName(record.GuestUserName);
        }

        return null;
    }

    private async Task ApplyPolicyAsync(object user, ShareLinkRecord record, bool disabled, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        var policy = new UserPolicy();

        SetPolicyValue(policy, "AuthenticationProviderId", GetUserValue(user, "AuthenticationProviderId"));
        SetPolicyValue(policy, "PasswordResetProviderId", GetUserValue(user, "PasswordResetProviderId"));
        SetPolicyValue(policy, "AllowedTags", string.IsNullOrWhiteSpace(record.AllowedTag) ? Array.Empty<string>() : new[] { record.AllowedTag! });
        SetPolicyValue(policy, "BlockedTags", Array.Empty<string>());
        SetPolicyValue(policy, "IsAdministrator", false);
        SetPolicyValue(policy, "IsHidden", true);
        SetPolicyValue(policy, "IsDisabled", disabled);
        SetPolicyValue(policy, "EnableCollectionManagement", false);
        SetPolicyValue(policy, "EnableSubtitleManagement", false);
        SetPolicyValue(policy, "EnableLyricManagement", false);
        SetPolicyValue(policy, "EnableUserPreferenceAccess", false);
        SetPolicyValue(policy, "EnableSharedDeviceControl", false);
        SetPolicyValue(policy, "EnableRemoteAccess", true);
        SetPolicyValue(policy, "EnableRemoteControlOfOtherUsers", false);
        SetPolicyValue(policy, "EnableLiveTvManagement", false);
        SetPolicyValue(policy, "EnableLiveTvAccess", false);
        SetPolicyValue(policy, "EnableMediaPlayback", true);
        SetPolicyValue(policy, "EnableAudioPlaybackTranscoding", config.AllowTranscoding);
        SetPolicyValue(policy, "EnableVideoPlaybackTranscoding", config.AllowTranscoding);
        SetPolicyValue(policy, "EnablePlaybackRemuxing", config.AllowRemuxing);
        SetPolicyValue(policy, "ForceRemoteSourceTranscoding", false);
        SetPolicyValue(policy, "EnableContentDeletion", false);
        SetPolicyValue(policy, "EnableContentDeletionFromFolders", Array.Empty<string>());
        SetPolicyValue(policy, "EnableContentDownloading", false);
        SetPolicyValue(policy, "EnableSyncTranscoding", false);
        SetPolicyValue(policy, "EnableMediaConversion", false);
        SetPolicyValue(policy, "EnableAllChannels", false);
        SetPolicyValue(policy, "EnabledChannels", Array.Empty<Guid>());
        SetPolicyValue(policy, "EnableAllDevices", true);
        SetPolicyValue(policy, "EnabledDevices", Array.Empty<string>());
        SetPolicyValue(policy, "EnableAllFolders", true);
        SetPolicyValue(policy, "EnabledFolders", Array.Empty<Guid>());
        SetPolicyValue(policy, "EnablePublicSharing", false);
        SetPolicyValue(policy, "LoginAttemptsBeforeLockout", -1);
        SetPolicyValue(policy, "MaxActiveSessions", 1);
        SetPolicyValue(policy, "BlockUnratedItems", Array.Empty<Jellyfin.Data.Enums.UnratedItem>());

        await InvokeUserManagerAsync<object?>(
                "update policy",
                cancellationToken,
                new InvocationCandidate("UpdatePolicyAsync", new object?[] { GetUserId(user), policy }),
                new InvocationCandidate("UpdatePolicyAsync", new object?[] { user, policy }),
                new InvocationCandidate("UpdatePolicy", new object?[] { GetUserId(user), policy }),
                new InvocationCandidate("UpdatePolicy", new object?[] { user, policy }))
            .ConfigureAwait(false);
    }

    private async Task ChangePasswordAsync(object user, string password, CancellationToken cancellationToken)
    {
        await InvokeUserManagerAsync<object?>(
                "change password",
                cancellationToken,
                new InvocationCandidate("ChangePasswordAsync", new object?[] { user, password }),
                new InvocationCandidate("ChangePasswordAsync", new object?[] { GetUserId(user), password }),
                new InvocationCandidate("ChangePasswordAsync", new object?[] { user, string.Empty, password }),
                new InvocationCandidate("ChangePasswordAsync", new object?[] { GetUserId(user), string.Empty, password }),
                new InvocationCandidate("ChangePassword", new object?[] { user, password }),
                new InvocationCandidate("ChangePassword", new object?[] { GetUserId(user), password }),
                new InvocationCandidate("ChangePassword", new object?[] { user, string.Empty, password }),
                new InvocationCandidate("ChangePassword", new object?[] { GetUserId(user), string.Empty, password }))
            .ConfigureAwait(false);
    }

    private async Task DeleteUserAsync(object user, CancellationToken cancellationToken)
    {
        await InvokeUserManagerAsync<object?>(
                "delete user",
                cancellationToken,
                new InvocationCandidate("DeleteUserAsync", new object?[] { GetUserId(user) }),
                new InvocationCandidate("DeleteUserAsync", new object?[] { user }),
                new InvocationCandidate("DeleteUser", new object?[] { GetUserId(user) }),
                new InvocationCandidate("DeleteUser", new object?[] { user }))
            .ConfigureAwait(false);
    }

    private async Task<T?> InvokeUserManagerAsync<T>(
        string operationName,
        CancellationToken cancellationToken,
        params InvocationCandidate[] candidates)
    {
        var managerType = _userManager.GetType();
        var triedVariants = new List<string>();

        foreach (var candidate in candidates)
        {
            var methods = managerType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(method => string.Equals(method.Name, candidate.MethodName, StringComparison.Ordinal));

            var matchedMethod = false;
            foreach (var method in methods)
            {
                if (!TryBindArguments(method, candidate.Arguments, cancellationToken, out var invocationArguments))
                {
                    continue;
                }

                matchedMethod = true;
                var invocation = method.Invoke(_userManager, invocationArguments);
                if (invocation is Task task)
                {
                    await task.WaitAsync(cancellationToken).ConfigureAwait(false);

                    var resultProperty = invocation.GetType().GetProperty("Result", BindingFlags.Instance | BindingFlags.Public);
                    if (resultProperty is null)
                    {
                        return default;
                    }

                    var result = resultProperty.GetValue(invocation);
                    if (result is null)
                    {
                        return default;
                    }

                    if (result is T typedResult)
                    {
                        return typedResult;
                    }

                    throw new InvalidOperationException(
                        $"ShareLinks: {managerType.FullName}.{candidate.MethodName} returned incompatible result type {result.GetType().FullName} for {operationName}.");
                }

                if (invocation is T directResult)
                {
                    return directResult;
                }

                if (invocation is null)
                {
                    return default;
                }

                throw new InvalidOperationException(
                    $"ShareLinks: {managerType.FullName}.{candidate.MethodName} returned incompatible result type {invocation.GetType().FullName} for {operationName}.");
            }

            if (!matchedMethod)
            {
                triedVariants.Add($"{candidate.MethodName}({DescribeArguments(candidate.Arguments)})");
            }
        }

        _logger.LogWarning(
            "ShareLinks: {UserManagerType} does not expose a compatible {Operation} variant. Tried {Variants}.",
            managerType.FullName,
            operationName,
            string.Join("; ", triedVariants));
        throw new MissingMethodException(managerType.FullName, operationName);
    }

    private static string DescribeArguments(object?[] arguments)
    {
        return string.Join(", ", arguments.Select(argument => argument?.GetType().Name ?? "null"));
    }

    private static bool TryBindArguments(MethodInfo method, object?[] suppliedArguments, CancellationToken cancellationToken, out object?[] invocationArguments)
    {
        var parameters = method.GetParameters();
        if (suppliedArguments.Length > parameters.Length)
        {
            invocationArguments = Array.Empty<object?>();
            return false;
        }

        invocationArguments = new object?[parameters.Length];
        for (var index = 0; index < suppliedArguments.Length; index++)
        {
            if (!TryConvertValue(parameters[index].ParameterType, suppliedArguments[index], out var convertedArgument))
            {
                invocationArguments = Array.Empty<object?>();
                return false;
            }

            invocationArguments[index] = convertedArgument;
        }

        for (var index = suppliedArguments.Length; index < parameters.Length; index++)
        {
            var parameter = parameters[index];
            if (parameter.ParameterType == typeof(CancellationToken))
            {
                invocationArguments[index] = cancellationToken;
                continue;
            }

            if (parameter.IsOptional)
            {
                invocationArguments[index] = GetOptionalParameterValue(parameter);
                continue;
            }

            invocationArguments = Array.Empty<object?>();
            return false;
        }

        return true;
    }

    private static object? GetOptionalParameterValue(ParameterInfo parameter)
    {
        var defaultValue = parameter.DefaultValue;
        if (defaultValue is not null && defaultValue != DBNull.Value && defaultValue != Type.Missing)
        {
            return defaultValue;
        }

        return parameter.ParameterType.IsValueType
            ? Activator.CreateInstance(parameter.ParameterType)
            : null;
    }

    private static void SetPolicyValue(UserPolicy policy, string memberName, object? value)
    {
        var policyType = policy.GetType();

        var property = policyType.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public);
        if (property is not null && property.CanWrite && TryConvertValue(property.PropertyType, value, out var convertedPropertyValue))
        {
            property.SetValue(policy, convertedPropertyValue);
            return;
        }

        var field = policyType.GetField(memberName, BindingFlags.Instance | BindingFlags.Public);
        if (field is not null && TryConvertValue(field.FieldType, value, out var convertedFieldValue))
        {
            field.SetValue(policy, convertedFieldValue);
        }
    }

    private static Guid GetUserId(object user)
    {
        var value = GetUserValue(user, "Id");
        if (value is Guid guid)
        {
            return guid;
        }

        if (value is string text && Guid.TryParse(text, out var parsedGuid))
        {
            return parsedGuid;
        }

        return Guid.Empty;
    }

    private static string GetUserName(object user)
    {
        var value = GetUserValue(user, "Username");
        if (value is string username && !string.IsNullOrWhiteSpace(username))
        {
            return username;
        }

        value = GetUserValue(user, "Name");
        return value as string ?? string.Empty;
    }

    private static object? GetUserValue(object user, string propertyName)
    {
        var property = user.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        return property?.GetValue(user);
    }

    private static bool TryConvertValue(Type targetType, object? value, out object? converted)
    {
        if (targetType.IsByRef)
        {
            targetType = targetType.GetElementType() ?? targetType;
        }

        var nonNullableType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (value is null)
        {
            converted = null;
            return !nonNullableType.IsValueType || Nullable.GetUnderlyingType(targetType) is not null;
        }

        if (nonNullableType.IsInstanceOfType(value) || targetType.IsAssignableFrom(value.GetType()))
        {
            converted = value;
            return true;
        }

        if (nonNullableType.IsEnum)
        {
            if (value is string text)
            {
                converted = Enum.Parse(nonNullableType, text, ignoreCase: true);
                return true;
            }

            if (IsNumeric(value))
            {
                converted = Enum.ToObject(nonNullableType, value);
                return true;
            }
        }

        if (nonNullableType == typeof(Guid) && value is string guidText && Guid.TryParse(guidText, out var guid))
        {
            converted = guid;
            return true;
        }

        if (value is IConvertible)
        {
            try
            {
                converted = Convert.ChangeType(value, nonNullableType, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                // The caller will ignore the missing or incompatible policy member.
            }
        }

        converted = null;
        return false;
    }

    private static bool IsNumeric(object value)
    {
        return value is byte
            or sbyte
            or short
            or ushort
            or int
            or uint
            or long
            or ulong
            or float
            or double
            or decimal;
    }

    private sealed record InvocationCandidate(string MethodName, object?[] Arguments);

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
