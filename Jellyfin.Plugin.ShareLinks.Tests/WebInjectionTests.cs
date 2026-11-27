using System.Text;
using Jellyfin.Plugin.ShareLinks.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ShareLinks.Tests;

/// <summary>The three ways the client script gets into index.html, and that they never add it twice.</summary>
public sealed class WebInjectionTests
{
    private const string Page = "<!doctype html><html><head></head><body><div id=\"app\"></div></body></html>";

    [Fact]
    public void TheTagGoesBeforeTheClosingBodyTagOnce()
    {
        var once = IndexHtmlScript.AddTo(Page);

        Assert.Contains("<script src=\"../ShareLinks/ClientScript\" defer></script>", once);
        Assert.True(once.IndexOf(IndexHtmlScript.Marker, StringComparison.Ordinal) < once.LastIndexOf("</body>", StringComparison.Ordinal));
        Assert.Equal(once, IndexHtmlScript.AddTo(once));
    }

    [Fact]
    public void FileTransformationGetsTheTaggedPageAndNeverAnEmptyOne()
    {
        var tagged = FileTransformationRegistration.TransformIndexHtml(new FileTransformationPayload { Contents = Page });
        Assert.Equal(IndexHtmlScript.AddTo(Page), tagged);

        // Already tagged by the middleware or the file on disk: left as it is.
        Assert.Equal(tagged, FileTransformationRegistration.TransformIndexHtml(new FileTransformationPayload { Contents = tagged }));
        Assert.Equal(string.Empty, FileTransformationRegistration.TransformIndexHtml(null!));
    }

    [Fact]
    public void FileTransformationMissingIsNotAFailure()
    {
        // The test host has no File Transformation assembly loaded.
        Assert.Equal(FileTransformationRegistration.Outcome.NotInstalled, FileTransformationRegistration.TryRegister(NullLogger.Instance));
    }

    [Fact]
    public async Task TheServedPageIsTagged()
    {
        var (body, context) = await Serve("/jf/web/", Page);

        Assert.Equal(IndexHtmlScript.AddTo(Page), body);
        Assert.Equal(Encoding.UTF8.GetByteCount(body), context.Response.ContentLength);
    }

    [Fact]
    public async Task APageAlreadyTaggedFurtherInIsLeftAlone()
    {
        var tagged = IndexHtmlScript.AddTo(Page);
        var (body, _) = await Serve("/web/index.html", tagged);

        Assert.Equal(tagged, body);
    }

    [Fact]
    public async Task ACompressedPageIsNeverRewritten()
    {
        var (body, _) = await Serve("/web/index.html", Page, response => response.Headers.ContentEncoding = "br");

        Assert.Equal(Page, body);
    }

    [Fact]
    public async Task OtherFilesAndAnswersPassThrough()
    {
        Assert.Equal(Page, (await Serve("/web/main.bundle.js", Page)).Body);
        Assert.Equal(Page, (await Serve("/web/", Page, response => response.StatusCode = StatusCodes.Status404NotFound)).Body);
        Assert.Equal(Page, (await Serve("/web/", Page, response => response.ContentType = "application/json")).Body);
    }

    [Fact]
    public async Task TheBrowserCacheCannotTurnThePageIntoAnEmpty304()
    {
        string? seen = null;
        var (body, _) = await Serve("/web/", Page, configureRequest: request => request.Headers.IfNoneMatch = "\"abc\"", onInner: request => seen = request.Headers.IfNoneMatch);

        Assert.True(string.IsNullOrEmpty(seen));
        Assert.Contains(IndexHtmlScript.Marker, body);
    }

    private static async Task<(string Body, HttpContext Context)> Serve(
        string path,
        string html,
        Action<HttpResponse>? configureResponse = null,
        Action<HttpRequest>? configureRequest = null,
        Action<HttpRequest>? onInner = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = path;
        configureRequest?.Invoke(context.Request);
        var output = new MemoryStream();
        context.Response.Body = output;

        var middleware = new IndexHtmlScriptMiddleware(
            async inner =>
            {
                onInner?.Invoke(inner.Request);
                inner.Response.StatusCode = StatusCodes.Status200OK;
                inner.Response.ContentType = "text/html";
                configureResponse?.Invoke(inner.Response);
                await inner.Response.WriteAsync(html);
            },
            NullLogger<IndexHtmlScriptMiddleware>.Instance);

        await middleware.InvokeAsync(context);
        return (Encoding.UTF8.GetString(output.ToArray()), context);
    }
}
