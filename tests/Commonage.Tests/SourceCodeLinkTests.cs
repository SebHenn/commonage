using Commonage.Web;
using Microsoft.AspNetCore.Hosting;

namespace Commonage.Tests;

/// <summary>The AGPL requires that everyone using the archive can get its source code, so every page links to it.</summary>
[Collection(nameof(AppCollection))]
public class SourceCodeLinkTests(CommonageAppFactory app)
{
    [Fact]
    public async Task Every_page_links_to_the_source_code()
    {
        using var visitor = app.CreateClient();

        foreach (var url in new[] { "/", "/search", "/Account/Login", "/no-such-page" })
        {
            var response = await visitor.GetAsync(url);
            Assert.Contains($"href=\"{SourceCode.DefaultUrl}\"", await response.Content.ReadAsStringAsync());
        }
    }

    [Theory]
    [InlineData("https://git.example.org/our-archive/commonage", "https://git.example.org/our-archive/commonage")]
    [InlineData("javascript:alert(1)", SourceCode.DefaultUrl)]
    [InlineData("", SourceCode.DefaultUrl)]
    public async Task Archives_that_change_the_code_link_to_their_own_version(string setting, string expected)
    {
        await using var changed = app.WithWebHostBuilder(b => b.UseSetting("Source:Url", setting));
        using var visitor = changed.CreateClient();

        Assert.Contains($"href=\"{expected}\"", await visitor.GetStringAsync("/"));
    }
}
