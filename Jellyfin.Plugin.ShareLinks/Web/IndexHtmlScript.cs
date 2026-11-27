using System;

namespace Jellyfin.Plugin.ShareLinks.Web;

/// <summary>
/// The client script tag and how it goes into the web client's index.html. Every
/// way the tag gets in (the file on disk, the serving middleware, File
/// Transformation) goes through here, so the page never ends up with two.
/// </summary>
public static class IndexHtmlScript
{
    /// <summary>What marks a page that already loads the script, however it got there.</summary>
    public const string Marker = "ShareLinks/ClientScript";

    /// <summary>The comment that opens the tag block.</summary>
    public const string Begin = "<!-- ShareLinks:begin -->";

    /// <summary>The comment that closes the tag block.</summary>
    public const string End = "<!-- ShareLinks:end -->";

    /// <summary>The block that is added, script tag included.</summary>
    public const string Snippet = "\n" + Begin + "\n<script src=\"../ShareLinks/ClientScript\" defer></script>\n" + End + "\n";

    /// <summary>Whether the page already loads the script.</summary>
    public static bool IsPresent(string html) => html.Contains(Marker, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the page with the script tag before its closing body tag, or
    /// unchanged when it is already there.
    /// </summary>
    public static string AddTo(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        if (IsPresent(html))
        {
            return html;
        }

        var bodyIndex = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return bodyIndex >= 0 ? html.Insert(bodyIndex, Snippet) : html + Snippet;
    }
}
