using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.ShareLinks.Web;

/// <summary>
/// Adds the plugin's client script tag to the web client's index.html while
/// Jellyfin serves it, so nothing on disk has to be writable.
/// </summary>
/// <remarks>
/// It logs, once each, what it did with the page: tagged it, found the tag already
/// there, or left it alone and why. So a server log with no ShareLinks line about
/// index.html at all means this never saw the page, and something earlier in the
/// pipeline answered it.
/// </remarks>
public sealed class IndexHtmlScriptMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<IndexHtmlScriptMiddleware> _logger;
    private readonly ConcurrentDictionary<string, bool> _logged = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="IndexHtmlScriptMiddleware"/> class.</summary>
    public IndexHtmlScriptMiddleware(RequestDelegate next, ILogger<IndexHtmlScriptMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <summary>Invokes the middleware for one request.</summary>
    /// <param name="context">The current request's HTTP context.</param>
    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        if (!HttpMethods.IsGet(request.Method) || !IsIndexHtmlRequest(request.Path.Value))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Ask the inner pipeline for the full, uncompressed file: a cached 304
        // or a compressed body would leave nothing usable to rewrite.
        request.Headers.Remove(HeaderNames.AcceptEncoding);
        request.Headers.Remove(HeaderNames.IfNoneMatch);
        request.Headers.Remove(HeaderNames.IfModifiedSince);
        request.Headers.Remove(HeaderNames.IfRange);
        request.Headers.Remove(HeaderNames.Range);

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        var bytes = buffer.ToArray();
        var response = context.Response;
        var encoding = response.Headers.ContentEncoding.ToString();
        if (response.StatusCode != StatusCodes.Status200OK)
        {
            LogOnce("status:" + response.StatusCode, "ShareLinks: left index.html ({Path}) alone, it was answered with status {Status}.", request.Path.Value, response.StatusCode);
        }
        else if (response.ContentType is null || !response.ContentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            LogOnce("type:" + response.ContentType, "ShareLinks: left index.html ({Path}) alone, it was served as {ContentType}.", request.Path.Value, response.ContentType ?? "no content type");
        }
        else if (!string.IsNullOrEmpty(encoding) && !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase))
        {
            // Something further in compressed it anyway. Rewriting compressed bytes
            // as text would break the page.
            LogOnce("encoding:" + encoding, "ShareLinks: left index.html ({Path}) alone, it came back compressed ({Encoding}).", request.Path.Value, encoding);
        }
        else
        {
            bytes = AddScriptTag(context, bytes);
        }

        if (bytes.Length == 0)
        {
            return;
        }

        await originalBody.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }

    private static bool IsIndexHtmlRequest(string? path)
    {
        return !string.IsNullOrEmpty(path)
            && (path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase));
    }

    private byte[] AddScriptTag(HttpContext context, byte[] original)
    {
        try
        {
            var html = Encoding.UTF8.GetString(original);
            if (IndexHtmlScript.IsPresent(html))
            {
                // Already tagged, whether by File Transformation, the file on disk,
                // or by hand. Leave it alone.
                LogOnce("present", "ShareLinks: index.html already loads the client script.");
                return original;
            }

            var updated = Encoding.UTF8.GetBytes(IndexHtmlScript.AddTo(html));

            context.Response.Headers.Remove(HeaderNames.ETag);
            context.Response.Headers.Remove(HeaderNames.LastModified);
            context.Response.ContentLength = updated.Length;

            LogOnce("tagged", "ShareLinks: adding the client script to index.html as it is served.");
            return updated;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ShareLinks: could not add the client script to index.html.");
            return original;
        }
    }

    private void LogOnce(string key, string message, params object?[] args)
    {
        if (_logged.TryAdd(key, true))
        {
#pragma warning disable CA2254 // The templates are the constant strings above.
            _logger.LogInformation(message, args);
#pragma warning restore CA2254
        }
    }
}
