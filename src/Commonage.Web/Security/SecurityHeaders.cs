namespace Commonage.Web.Security;

/// <summary>
/// HTTP headers that make the browser refuse whole classes of attacks. Every response gets them, pages and files alike.
/// </summary>
public static class SecurityHeaders
{
    /// <summary>
    /// Scripts, styles, fonts and images only from this site; no inline script, no plugins, no framing. The app has no
    /// inline script or style on purpose (see the data-confirm attribute in site.js), so this policy breaks nothing.
    /// "data:" images are Bootstrap's built-in icons, such as the arrow of a select box.
    /// </summary>
    public const string ContentSecurityPolicy = UpToImages + AfterImages;

    /// <summary>The contribution form also shows the photos someone has chosen, which only exist on their device so far.</summary>
    public const string ContributionFormPolicy = UpToImages + " blob:" + AfterImages;

    private const string UpToImages = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:";

    private const string AfterImages =
        "; font-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            // Browsers must not guess content types, so an uploaded file can never be interpreted as a script or page.
            headers.XContentTypeOptions = "nosniff";
            headers.ContentSecurityPolicy = context.Request.Path.StartsWithSegments("/contribute")
                ? ContributionFormPolicy
                : ContentSecurityPolicy;
            headers.XFrameOptions = "DENY";
            // Addresses can carry setup links; other sites never see them.
            headers["Referrer-Policy"] = "same-origin";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            await next();
        });
}
