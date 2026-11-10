using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

namespace Commonage.BrowserTests;

/// <summary>Every main page at phone and desktop width: no axe violations, no sideways scrolling, nothing the browser refused.</summary>
[Collection(nameof(BrowserCollection))]
public class AccessibilityTests(BrowserFixture app)
{
    public static TheoryData<string, int> PublicPages() => Widths(
        "/", "/collections", "/collections/1", "/items/1", "/items/2", "/items/6", "/search", "/search?q=flood", "/search?q=1950s", "/search?q=zzzznothing", "/items/99999", "/places", "/people",
        "/Account/Login", "CONTRIBUTE", "/contribute/no-such-link", "/about", "/contact", "/privacy");

    public static TheoryData<string, int> CuratorPages() => Widths(
        "/Admin", "/Admin?status=Submitted", "/Admin/Items/Edit/11", "/Admin/Items/Edit", "/Admin/Items/AddMany", "/Admin/Items/Edit/1", "/Admin/Items/Preview/1", "/Admin/Collections", "/Admin/Tags", "/Admin/Tags/Edit/1", "/Admin/Contributions",
        "/Admin/Contributors?q=a", "/Admin/Bin", "/Admin/Settings", "/Admin/Users", "/Admin/Activity", "/Admin/Activity?what=Items", "/Admin/Export", "/Admin/System", "/Account/Password", "/Account/TwoFactor");

    private static TheoryData<string, int> Widths(params string[] paths)
    {
        var data = new TheoryData<string, int>();
        foreach (var path in paths)
        {
            data.Add(path, 390);
            data.Add(path, 1280);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(PublicPages))]
    public async Task A_public_page_works_for_everyone(string path, int width)
    {
        var page = await app.NewPageAsync(width);
        await CheckAsync(page, path == "CONTRIBUTE" ? app.ContributionPath : path);
    }

    [Theory]
    [MemberData(nameof(CuratorPages))]
    public async Task A_curator_page_works_for_everyone(string path, int width)
    {
        var page = await app.LogInAsync(width);
        await CheckAsync(page, path);
    }

    [Theory]
    [MemberData(nameof(MainPages))]
    public async Task A_page_works_from_right_to_left(string path)
    {
        var page = await app.NewPageAsync(390);
        await page.Context.AddCookiesAsync([new Cookie { Name = ".AspNetCore.Culture", Value = "c=ar|uic=ar", Url = app.BaseUrl }]);

        await CheckAsync(page, path == "CONTRIBUTE" ? app.ContributionPath : path);

        Assert.Equal("rtl", await page.GetAttributeAsync("html", "dir"));
        // The site name starts on the right, as the reading does.
        var brand = await page.Locator(".navbar-brand").BoundingBoxAsync();
        Assert.True(brand!.X + brand.Width > 390 / 2);
    }

    [Theory]
    [MemberData(nameof(MainPages))]
    public async Task A_page_holds_up_with_much_longer_text(string path)
    {
        var page = await app.NewPageAsync(320, 640);
        await page.Context.AddCookiesAsync([new Cookie { Name = ".AspNetCore.Culture", Value = $"c={LongTextLanguage.Code}|uic={LongTextLanguage.Code}", Url = app.BaseUrl }]);

        await page.GotoAsync(path == "CONTRIBUTE" ? app.ContributionPath : path);

        var overflow = await page.EvaluateAsync<int>("document.documentElement.scrollWidth - document.documentElement.clientWidth");
        var tooWide = await page.EvaluateAsync<string[]>("""
            [...document.querySelectorAll('body *')]
                .filter(e => e.getBoundingClientRect().right > document.documentElement.clientWidth + 1 && !e.closest('.contact-field'))
                .slice(0, 5).map(e => e.tagName + '.' + e.className + ': ' + e.innerText?.slice(0, 40))
            """);
        Assert.True(overflow <= 0, $"{path} scrolls sideways by {overflow} px: {string.Join(" | ", tooWide)}");
    }

    public static TheoryData<string> MainPages() => new() { "/", "/collections", "/collections/1", "/items/1", "/search?q=flood", "CONTRIBUTE", "/privacy" };

    /// <summary>For states a page only reaches by using it, as well as for each page as it loads.</summary>
    internal static async Task AssertNoViolationsAsync(IPage page, string what)
    {
        // Away from any button and past its colour change on hover, or its contrast is measured halfway through.
        await page.Mouse.MoveAsync(0, 0);
        await page.WaitForTimeoutAsync(250);

        var results = await page.RunAxe(new AxeRunOptions
        {
            RunOnly = new RunOnlyOptions { Type = "tag", Values = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa", "best-practice"] },
        });
        var violations = results.Violations.Select(v => $"{v.Id}: {v.Nodes.First().Html}").ToList();
        Assert.True(violations.Count == 0, $"{what}:\n{string.Join("\n", violations)}");

        var overflow = await page.EvaluateAsync<int>("document.documentElement.scrollWidth - document.documentElement.clientWidth");
        Assert.True(overflow <= 0, $"{what} scrolls sideways by {overflow} px.");
    }

    private static async Task CheckAsync(IPage page, string path)
    {
        var refused = new List<string>();
        page.Console += (_, message) =>
        {
            if (message.Type == "error" && !message.Text.Contains("404"))
                refused.Add(message.Text);
        };

        await page.GotoAsync(path);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await AssertNoViolationsAsync(page, path);

        Assert.True(refused.Count == 0, $"{path}:\n{string.Join("\n", refused)}");

        // Without its stylesheet, a page would look broken but still pass all of the above.
        var font = await page.EvaluateAsync<string>("getComputedStyle(document.querySelector('.container')).maxWidth + getComputedStyle(document.querySelector('.container')).paddingLeft");
        Assert.NotEqual("none0px", font);
    }
}
