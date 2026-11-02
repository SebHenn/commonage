using System.Text.RegularExpressions;
using Commonage.Web.Domain;

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
        var item = await app.CreateContributionAsync(ItemStatus.Published);
        using var curator = app.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);
        using var visitor = app.CreateClient();
        var pages = new List<string>
        {
            await visitor.GetStringAsync("/"),
            await visitor.GetStringAsync("/search?q=flood"),
            await visitor.GetStringAsync($"/items/{item.Id}"),
            await visitor.GetStringAsync($"/collections/{item.CollectionId}"),
        };
        foreach (var url in new[]
        {
            "/Admin", $"/Admin/Items/Edit/{item.Id}", "/Admin/Items/Edit", "/Admin/Collections", "/Admin/Contributions",
            "/Admin/Contributions/Edit", "/Admin/Contributors?q=erika", "/Admin/Users", "/Admin/Users/Edit",
            "/Admin/Activity", "/Admin/Export", "/Account/Password",
        })
            pages.Add(await curator.GetStringAsync(url));

        foreach (var page in pages)
        {
            Assert.DoesNotMatch(InlineScript(), page);
            Assert.DoesNotMatch(EventHandlerAttribute(), page);
            Assert.DoesNotMatch(StyleAttributeOrElement(), page);
        }
    }

    [GeneratedRegex(@"<script(?![^>]*\ssrc=)[^>]*>")]
    private static partial Regex InlineScript();

    [GeneratedRegex(@"<[a-z][^>]*\son[a-z]+\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex EventHandlerAttribute();

    [GeneratedRegex(@"<style[\s>]|<[a-z][^>]*\sstyle\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex StyleAttributeOrElement();
}
