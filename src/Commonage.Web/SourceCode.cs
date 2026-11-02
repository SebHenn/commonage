namespace Commonage.Web;

/// <summary>
/// Where visitors get the source code of the Commonage version this archive runs. The AGPL (section 13) requires that
/// everyone who uses a changed version over the network can get its source, so the footer links here on every page.
/// Archives that change the code set <c>Source:Url</c> (<c>COMMONAGE_SOURCE_URL</c> in Docker) to their own version.
/// </summary>
public static class SourceCode
{
    public const string DefaultUrl = "https://github.com/SebHenn/commonage";

    public static string UrlFrom(IConfiguration configuration) =>
        configuration["Source:Url"] is { Length: > 0 } url && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme is "https" or "http"
            ? url
            : DefaultUrl;
}
