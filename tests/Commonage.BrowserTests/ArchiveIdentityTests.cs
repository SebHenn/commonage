using Microsoft.Playwright;

namespace Commonage.BrowserTests;

/// <summary>What only a browser can tell: whether the colours and the logo really reach the page.</summary>
[Collection(nameof(BrowserCollection))]
public class ArchiveIdentityTests(BrowserFixture app)
{
    private static Task<string> BackgroundOfAsync(IPage page, string selector) =>
        page.EvaluateAsync<string>($"getComputedStyle(document.querySelector(\"{selector}\")).backgroundColor");

    private static Task<string> ColorOfAsync(IPage page, string selector) =>
        page.EvaluateAsync<string>($"getComputedStyle(document.querySelector(\"{selector}\")).color");

    private static async Task SetColorAsync(IPage page, string? color)
    {
        await page.GotoAsync("/Admin/Settings");
        await page.SetCheckedAsync("[name='Input.UseStandardColor']", color is null);
        if (color is not null)
            await page.FillAsync("[name='Input.AccentColor']", color);
        await page.ClickAsync("main form button[type=submit]");
    }

    [Fact]
    public async Task A_chosen_colour_really_changes_buttons_and_links_and_going_back_works()
    {
        var page = await app.LogInAsync();
        try
        {
            await SetColorAsync(page, "#1a5f7a");
            await page.WaitForSelectorAsync(".alert-success");
            await page.GotoAsync("/Admin/Items/Edit/1");

            Assert.Equal("rgb(26, 95, 122)", await BackgroundOfAsync(page, "main form button.btn-primary"));
            Assert.Equal("rgb(26, 95, 122)", await ColorOfAsync(page, ".breadcrumb a"));
            Assert.Equal("rgb(26, 95, 122)", await BackgroundOfAsync(page, ".admin-nav .nav-link[aria-current]"));

            var visitor = await app.NewPageAsync();
            await visitor.GotoAsync("/search");
            Assert.Equal("rgb(26, 95, 122)", await BackgroundOfAsync(visitor, "main form button.btn-primary"));
        }
        finally
        {
            page = await app.LogInAsync();
            await SetColorAsync(page, null);
        }

        await page.GotoAsync("/search");
        Assert.Equal("rgb(13, 110, 253)", await BackgroundOfAsync(page, "main form button.btn-primary"));
    }

    [Fact]
    public async Task A_logo_appears_in_the_navigation_and_as_the_tab_icon()
    {
        var page = await app.LogInAsync();
        await page.GotoAsync("/Admin/Settings");

        await page.SetInputFilesAsync("#Logo", Path.Combine("Fixtures", "photo.jpg"));
        await page.ClickAsync("main form button[type=submit]");
        await page.WaitForSelectorAsync(".alert-success");

        var logoLoaded = await page.EvaluateAsync<bool>("document.querySelector('.navbar-logo').naturalWidth > 0");
        Assert.True(logoLoaded);
        Assert.NotNull(await page.Locator("link[rel=icon]").GetAttributeAsync("href"));

        await page.CheckAsync("[name='Input.RemoveLogo']");
        await page.ClickAsync("main form button[type=submit]");
        await page.WaitForSelectorAsync(".alert-success");
        Assert.Equal(0, await page.Locator(".navbar-logo").CountAsync());
    }

    [Fact]
    public async Task The_contribution_form_says_whose_it_is_and_the_pages_are_linked()
    {
        var page = await app.NewPageAsync(390, 844);
        await page.GotoAsync(app.ContributionPath);

        Assert.Contains("This form belongs to Lindenau History Group", await page.InnerTextAsync("main"));
        Assert.True(await page.Locator("main a:text-is('How to reach the archive')").IsVisibleAsync());
        Assert.True(await page.Locator("footer a:text-is('Privacy')").IsVisibleAsync());
        Assert.Equal(0, await page.Locator("footer a:text-is('Imprint')").CountAsync());

        await page.ClickAsync("main a:text-is('How to reach the archive')");
        Assert.Contains("archive@example.org", await page.InnerTextAsync("main"));
        Assert.Equal("mailto:archive@example.org", await page.Locator("main a[href^='mailto:']").GetAttributeAsync("href"));
    }
}
