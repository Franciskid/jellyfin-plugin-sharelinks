using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShareLinks.Web;

/// <summary>What File Transformation hands a callback: the file as it stands so far.</summary>
public sealed class FileTransformationPayload
{
    /// <summary>Gets or sets the file's contents.</summary>
    public string? Contents { get; set; }
}

/// <summary>
/// Adds the client script through the File Transformation plugin when it is
/// installed. Some servers serve index.html in a way the middleware never sees,
/// while File Transformation's edits still reach the page there, so with it
/// installed this is the path most likely to work.
/// </summary>
/// <remarks>
/// Plugins load into separate assembly contexts, so File Transformation cannot be
/// referenced directly: it is found and called by reflection, as its README
/// describes. Its payload type is a Newtonsoft JObject from its own context, built
/// here from the parameter type of its registration method so this plugin needs
/// no Newtonsoft reference of its own.
/// </remarks>
public static class FileTransformationRegistration
{
    /// <summary>The id this plugin's transformation is registered under.</summary>
    public static readonly Guid TransformationId = new("5e3c8f0a-7c1d-4a52-9b8e-2f6d1c0a9e47");

    /// <summary>The outcome of trying to register.</summary>
    public enum Outcome
    {
        /// <summary>File Transformation is not installed.</summary>
        NotInstalled,

        /// <summary>Registered.</summary>
        Registered,

        /// <summary>Installed, but registering did not work.</summary>
        Failed
    }

    /// <summary>
    /// Called by File Transformation each time it serves index.html. Public and
    /// static because it is looked up by name.
    /// </summary>
    public static string TransformIndexHtml(FileTransformationPayload payload)
    {
        var contents = payload?.Contents ?? string.Empty;
        if (string.IsNullOrWhiteSpace(contents))
        {
            // File Transformation makes up an empty file when the real one is
            // missing. A page holding nothing but this tag is worse than its 404.
            return contents;
        }

        try
        {
            return IndexHtmlScript.AddTo(contents);
        }
        catch (Exception)
        {
            // Never break the page.
            return contents;
        }
    }

    /// <summary>Registers the transformation when File Transformation is installed.</summary>
    public static Outcome TryRegister(ILogger logger)
    {
        var assembly = AssemblyLoadContext.All
            .SelectMany(context => context.Assemblies)
            .FirstOrDefault(candidate => candidate.FullName?.Contains(".FileTransformation", StringComparison.Ordinal) ?? false);
        if (assembly is null)
        {
            return Outcome.NotInstalled;
        }

        try
        {
            var register = assembly.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface")?.GetMethod("RegisterTransformation");
            var payloadType = register?.GetParameters().FirstOrDefault()?.ParameterType;
            var parse = payloadType?.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, new[] { typeof(string) });
            if (register is null || parse is null)
            {
                logger.LogWarning("ShareLinks: File Transformation is installed but its registration method was not found; relying on the other ways of adding the client script.");
                return Outcome.Failed;
            }

            var json = JsonSerializer.Serialize(new
            {
                id = TransformationId,
                fileNamePattern = "^index\\.html$",
                callbackAssembly = typeof(FileTransformationRegistration).Assembly.FullName,
                callbackClass = typeof(FileTransformationRegistration).FullName,
                callbackMethod = nameof(TransformIndexHtml)
            });

            register.Invoke(null, new[] { parse.Invoke(null, new object[] { json }) });
            logger.LogInformation("ShareLinks: registered the client script with File Transformation.");
            return Outcome.Registered;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex is TargetInvocationException { InnerException: { } inner } ? inner : ex, "ShareLinks: could not register the client script with File Transformation; relying on the other ways of adding it.");
            return Outcome.Failed;
        }
    }
}
