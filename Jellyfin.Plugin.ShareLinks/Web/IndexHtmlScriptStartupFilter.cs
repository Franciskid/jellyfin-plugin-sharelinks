using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Jellyfin.Plugin.ShareLinks.Web;

/// <summary>
/// Puts <see cref="IndexHtmlScriptMiddleware"/> in front of Jellyfin's own pipeline.
/// </summary>
public sealed class IndexHtmlScriptStartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.UseMiddleware<IndexHtmlScriptMiddleware>();
            next(app);
        };
    }
}
