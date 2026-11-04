using System.Text.RegularExpressions;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Commonage.Tests;

[Collection(nameof(AppCollection))]
public partial class SecurityHeaderTests(CommonageAppFactory app)
{
    [Fact]
    public async Task Every_response_carries_the_security_headers_and_no_server_name()
    {
        using var visitor = app.CreateClient();

        foreach (var url in new[] { "/", "/health", "/Account/Login", "/does-not-exist" })
        {
            var response = await visitor.GetAsync(url);
            Assert.Contains("frame-ancestors 'none'", string.Join(" ", response.Headers.GetValues("Content-Security-Policy")));
            Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
            Assert.Equal("same-origin", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
            Assert.False(response.Headers.Contains("Server"), url);
        }
    }

    /// <summary>The Content Security Policy forbids inline script and style, so pages must not contain any.</summary>
    [Fact]
    public async Task Pages_contain_no_inline_script_or_style()
    {
        // The pages come from the app itself, so a new page is covered without anyone having to remember it.
        var routes = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<PageActionDescriptor>() is not null)
            .Select(endpoint => "/" + endpoint.RoutePattern.RawText)
            .Distinct()
            .ToList();
        Assert.True(routes.Count > 20, $"Only {routes.Count} pages found.");

        var item = await app.CreateContributionAsync(ItemStatus.Published);
        using var curator = app.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);
        var token = await CreateLinkAsync(item.CollectionId);

        var results = new List<string>();
        foreach (var route in routes)
        {
            var url = FillIn(route, item, token);
            using var response = await curator.GetAsync(url);
            var html = await response.Content.ReadAsStringAsync();
            if (InlineScript().IsMatch(html) || EventHandlerAttribute().IsMatch(html) || StyleAttributeOrElement().IsMatch(html))
                results.Add($"{url} contains inline script or style");
            // These two answer with an error status on purpose, and show their page all the same.
            var answersWithError = route.StartsWith("/Error", StringComparison.OrdinalIgnoreCase)
                || route.Equals("/Account/AccessDenied", StringComparison.OrdinalIgnoreCase);
            if (!response.IsSuccessStatusCode && !answersWithError)
                results.Add($"{url} answered {(int)response.StatusCode}, so it was not checked");
        }

        Assert.True(results.Count == 0, string.Join("\n", results));
    }

    private static string FillIn(string route, ArchiveItem item, string token)
    {
        var id = route.StartsWith("/Collections", StringComparison.OrdinalIgnoreCase) || route.Contains("Admin/Collections", StringComparison.OrdinalIgnoreCase)
            ? item.CollectionId.ToString()
            : item.Id.ToString();
        var url = route
            .Replace("{token}", token)
            .Replace("{id:int}", id)
            // The pages about the archive: this one always exists.
            .Replace("{slug:regex(^(about|contact|privacy|imprint)$)}", "privacy");
        // Optional parts are left out, which gives the form for a new record or the page without details.
        return OptionalPart().Replace(url, "").TrimEnd('/') is { Length: > 0 } filled ? filled : "/";
    }

    private Task<string> CreateLinkAsync(int collectionId) => app.WithDbAsync(async db =>
    {
        var link = new ContributionLink
        {
            Title = TestData.Unique("Share your photos"),
            Token = Guid.NewGuid().ToString("N"),
            CollectionId = collectionId,
            CreatedAt = DateTime.UtcNow,
        };
        db.ContributionLinks.Add(link);
        await db.SaveChangesAsync();
        return link.Token;
    });

    [GeneratedRegex(@"/?\{[^}]*\?\}")]
    private static partial Regex OptionalPart();

    [GeneratedRegex(@"<script(?![^>]*\ssrc=)[^>]*>")]
    private static partial Regex InlineScript();

    [GeneratedRegex(@"<[a-z][^>]*\son[a-z]+\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex EventHandlerAttribute();

    [GeneratedRegex(@"<style[\s>]|<[a-z][^>]*\sstyle\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex StyleAttributeOrElement();
}
