using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

namespace Commonage.BrowserTests;

/// <summary>Every main page at phone and desktop width: no axe violations, no sideways scrolling, nothing the browser refused.</summary>
[Collection(nameof(BrowserCollection))]
public class AccessibilityTests(BrowserFixture app)
{
    public static TheoryData<string, int> PublicPages() => Widths(
        "/", "/collections/1", "/items/1", "/search", "/search?q=flood", "/search?q=zzzznothing", "/items/99999",
        "/Account/Login", "CONTRIBUTE", "/contribute/no-such-link");

    public static TheoryData<string, int> CuratorPages() => Widths(
        "/Admin", "/Admin?status=Submitted", "/Admin/Items/Edit", "/Admin/Items/Edit/1", "/Admin/Items/Preview/1", "/Admin/Collections", "/Admin/Contributions",
        "/Admin/Contributors?q=a", "/Admin/Bin", "/Admin/Users", "/Admin/Activity", "/Admin/Export", "/Account/Password");

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

        var results = await page.RunAxe(new AxeRunOptions
        {
            RunOnly = new RunOnlyOptions { Type = "tag", Values = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa", "best-practice"] },
        });
        var violations = results.Violations.Select(v => $"{v.Id}: {v.Nodes.First().Html}").ToList();
        Assert.True(violations.Count == 0, $"{path}:\n{string.Join("\n", violations)}");

        var overflow = await page.EvaluateAsync<int>("document.documentElement.scrollWidth - document.documentElement.clientWidth");
        Assert.True(overflow <= 0, $"{path} scrolls sideways by {overflow} px.");

        Assert.True(refused.Count == 0, $"{path}:\n{string.Join("\n", refused)}");

        // Without its stylesheet, a page would look broken but still pass all of the above.
        var font = await page.EvaluateAsync<string>("getComputedStyle(document.querySelector('.container')).maxWidth + getComputedStyle(document.querySelector('.container')).paddingLeft");
        Assert.NotEqual("none0px", font);
    }
}
