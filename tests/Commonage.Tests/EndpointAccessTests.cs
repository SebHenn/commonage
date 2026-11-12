using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticAssets;
using Microsoft.Extensions.DependencyInjection;

namespace Commonage.Tests;

/// <summary>
/// A new page or address is open to everyone unless something says otherwise, and nothing would show that it should not
/// have been. So every address the app answers is either listed here as meant for visitors, or asks for a login.
/// </summary>
[Collection(nameof(AppCollection))]
public class EndpointAccessTests(CommonageAppFactory app)
{
    private static readonly string[] MeantForVisitors =
    [
        "/", "/Index", "/search", "/items/{id:int}", "/collections", "/collections/{id:int}",
        "/{slug:regex(^(about|contact|privacy|imprint)$)}", "/SetLanguage", "/Error/{code:int?}",
        // Published pictures only, see MediaTests.
        "/media/{id:int}/{variant:regex(^(preview|thumbnail|card|large)$)}",
        "/branding.css", "/branding/{id:guid}/{variant}", "/robots.txt", "/feed.xml", "/sitemap.xml", "/health", "/date-check",
        // Open to whoever has the link.
        "/contribute/{token}", "/contribute/{token}/thanks", "/Account/SetPassword",
        "/Account/Login", "/Account/LoginTwoFactor", "/Account/Logout", "/Account/AccessDenied",
        // Where a file in wwwroot that the build does not know is looked for.
        "/{**path:file}",
    ];

    [Fact]
    public void Every_address_asks_for_a_login_unless_it_is_meant_for_visitors()
    {
        // Starts the app, which is what fills in the list of addresses.
        using var client = app.CreateClient();
        var open = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            // The scripts, styles and fonts in wwwroot are for everyone.
            .Where(endpoint => endpoint.Metadata.GetMetadata<StaticAssetDescriptor>() is null)
            .Where(endpoint => endpoint.Metadata.GetMetadata<IAuthorizeData>() is null)
            .Select(endpoint => "/" + endpoint.RoutePattern.RawText!.TrimStart('/'))
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(open.SequenceEqual(MeantForVisitors.Order(StringComparer.Ordinal)),
            "These addresses are open to everyone. Is each of them meant to be? If so, list it in MeantForVisitors; "
            + "if not, give it a policy in Program.cs or RequireAuthorization.\n" + string.Join('\n', open));
    }
}
