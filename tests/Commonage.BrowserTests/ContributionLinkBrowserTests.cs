using Microsoft.Playwright;

namespace Commonage.BrowserTests;

/// <summary>What a curator does with a contribution link: copy it, print a poster for it.</summary>
[Collection(nameof(BrowserCollection))]
public class ContributionLinkBrowserTests(BrowserFixture app)
{
    [Theory]
    [InlineData(390)]
    [InlineData(1280)]
    public async Task The_address_of_a_link_can_be_copied_in_one_go(int width)
    {
        var page = await app.LogInAsync(width);
        await page.Context.GrantPermissionsAsync(["clipboard-read", "clipboard-write"], new BrowserContextGrantPermissionsOptions { Origin = app.BaseUrl });
        await page.GotoAsync("/Admin/Contributions");

        var button = page.Locator("button[data-copy-from]").First;
        await button.ClickAsync();

        var status = button.Locator("xpath=following-sibling::span[@role='status']");
        await Assertions.Expect(status).ToHaveTextAsync("Copied");
        var copied = await page.EvaluateAsync<string>("navigator.clipboard.readText()");
        Assert.StartsWith($"{app.BaseUrl}/contribute/", copied);
        await AccessibilityTests.AssertNoViolationsAsync(page, "The contribution links");
    }

    [Fact]
    public async Task The_poster_has_a_code_big_enough_to_scan_and_the_page_prints_without_the_screen_furniture()
    {
        var page = await app.LogInAsync(390);
        await page.GotoAsync("/Admin/Contributions/Poster/1");

        var qr = await page.Locator(".poster .qr-code").BoundingBoxAsync();
        Assert.True(qr!.Width >= 200 && Math.Abs(qr.Width - qr.Height) < 1, $"The code to scan is {qr.Width} by {qr.Height}.");
        Assert.True(await page.Locator("button[data-print]").IsVisibleAsync());
        Assert.StartsWith($"{app.BaseUrl}/contribute/", (await page.InnerTextAsync(".poster-address")).Trim());

        await page.EmulateMediaAsync(new PageEmulateMediaOptions { Media = Media.Print });
        Assert.False(await page.Locator("button[data-print]").IsVisibleAsync());
        Assert.False(await page.Locator(".admin-nav").IsVisibleAsync());
        Assert.True(await page.Locator(".poster").IsVisibleAsync());
    }

    [Fact]
    public async Task The_poster_works_without_JavaScript()
    {
        var page = await app.LogInAsync(javaScript: false);
        await page.GotoAsync("/Admin/Contributions/Poster/1");

        Assert.True(await page.Locator(".poster .qr-code").IsVisibleAsync());
        Assert.False(await page.Locator("button[data-print]").IsVisibleAsync());
    }
}
