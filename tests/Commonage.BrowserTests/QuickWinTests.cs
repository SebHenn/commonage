using Microsoft.Playwright;

namespace Commonage.BrowserTests;

/// <summary>What M9 changed for people on the contribution form, on a phone and on curators' forms.</summary>
[Collection(nameof(BrowserCollection))]
public class QuickWinTests(BrowserFixture app)
{
    private const string Date = "[name='Input.Date']";

    private async Task FillContributionAsync(IPage page, string title, bool consent = true)
    {
        await page.GotoAsync(app.ContributionPath);
        await page.FillAsync("[name='Input.Title']", title);
        await page.CheckAsync("[name='Input.Rights'] >> nth=0");
        await page.FillAsync("[name='Input.Name']", "Ada Contributor");
        if (consent)
            await page.CheckAsync("[name='Input.Consent']");
    }

    [Fact]
    public async Task A_date_is_explained_while_it_is_typed()
    {
        var page = await app.NewPageAsync(390, 844);
        await page.GotoAsync(app.ContributionPath);

        await page.FillAsync(Date, "circa 1950");
        await page.WaitForSelectorAsync("#date-result:has-text('1945–1955')");
        Assert.Contains("Understood as", await page.InnerTextAsync("#date-result"));

        await page.FillAsync(Date, "the winter when I was small");
        await page.WaitForSelectorAsync("#date-result.text-danger");

        await page.FillAsync(Date, "");
        await page.WaitForSelectorAsync("#date-result:empty", new PageWaitForSelectorOptions { State = WaitForSelectorState.Attached });
    }

    [Fact]
    public async Task Forgetting_the_consent_is_noticed_before_chosen_photos_are_lost()
    {
        var page = await app.NewPageAsync(390, 844);
        await FillContributionAsync(page, "Without consent", consent: false);
        await page.SetInputFilesAsync("#Files", Path.Combine("Fixtures", "photo.jpg"));

        await page.ClickAsync("main form button[type=submit]");
        await page.WaitForSelectorAsync("[data-valmsg-for='Input.Consent']:has-text('Please agree')");

        Assert.Equal(1, await page.EvaluateAsync<int>("document.querySelector('#Files').files.length"));

        await page.CheckAsync("[name='Input.Consent']");
        await page.ClickAsync("main form button[type=submit]");
        await page.WaitForURLAsync("**/thanks");
    }

    [Fact]
    public async Task A_mistake_only_the_server_finds_leads_to_a_summary_that_has_the_focus()
    {
        var page = await app.NewPageAsync(390, 844);
        await FillContributionAsync(page, "With an unreadable date");
        await page.FillAsync(Date, "the winter when I was small");

        await page.ClickAsync("main form button[type=submit]");
        await page.WaitForSelectorAsync("[data-focus-on-load]");

        Assert.True(await page.EvaluateAsync<bool>("document.activeElement.hasAttribute('data-focus-on-load')"));
        await page.ClickAsync("[data-focus-on-load] a");
        Assert.Equal("Input_Date", await page.EvaluateAsync<string>("document.activeElement.id"));
    }

    [Fact]
    public async Task A_contribution_cannot_be_sent_twice_by_clicking_twice()
    {
        var page = await app.NewPageAsync(390, 844);
        await FillContributionAsync(page, "Clicked twice");
        var posts = 0;
        page.Request += (_, request) =>
        {
            if (request.Method == "POST")
                posts++;
        };

        // Two clicks straight after each other, the way an impatient finger does on a slow connection.
        await page.EvaluateAsync("""
            () => {
                const button = document.querySelector('main form button[type=submit]');
                button.click();
                button.click();
            }
            """);
        await page.WaitForURLAsync("**/thanks");

        Assert.Equal(1, posts);
    }

    [Fact]
    public async Task The_file_picker_speaks_the_language_of_the_page()
    {
        var page = await app.NewPageAsync(390, 844);
        await page.Context.AddCookiesAsync([new Cookie { Name = ".AspNetCore.Culture", Value = "c=de|uic=de", Url = app.BaseUrl }]);
        await page.GotoAsync(app.ContributionPath);

        var chosen = page.Locator("#Files-chosen");
        Assert.Equal("Keine Fotos ausgewählt", await chosen.InnerTextAsync());
        Assert.True(await page.Locator("button:text-is('Fotos auswählen')").IsVisibleAsync());

        await page.SetInputFilesAsync("#Files", Path.Combine("Fixtures", "photo.jpg"));

        Assert.Equal("photo.jpg", await chosen.InnerTextAsync());
    }

    [Fact]
    public async Task Without_javascript_the_browsers_own_file_control_stays()
    {
        var page = await app.NewPageAsync(390, 844, javaScript: false);
        await page.GotoAsync(app.ContributionPath);

        Assert.True(await page.Locator("#Files").IsVisibleAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task On_a_phone_visitors_reach_collections_and_search_directly(bool javaScript)
    {
        var page = await app.NewPageAsync(320, 640, javaScript);
        await page.GotoAsync("/items/1");

        Assert.True(await page.Locator("nav.navbar a:text-is('Collections')").IsVisibleAsync());
        await page.ClickAsync("nav.navbar a:text-is('Search')");

        Assert.Contains("/search", page.Url);
        Assert.Equal("page", await page.Locator("nav.navbar a:text-is('Search')").GetAttributeAsync("aria-current"));
    }

    [Fact]
    public async Task On_a_phone_without_javascript_search_filters_are_there()
    {
        var page = await app.NewPageAsync(390, 844, javaScript: false);
        await page.GotoAsync("/search");

        Assert.True(await page.Locator("#filter-collection").IsVisibleAsync());
        Assert.False(await page.Locator("button:text-is('Filters')").IsVisibleAsync());
    }

    [Fact]
    public async Task On_a_phone_a_signed_in_curator_reaches_the_account_menu()
    {
        var page = await app.LogInAsync(390, 844);

        await page.ClickAsync(".navbar-toggler");

        await page.WaitForSelectorAsync("#account-menu a:text-is('Manage archive')", new PageWaitForSelectorOptions { State = WaitForSelectorState.Visible });
    }

    [Fact]
    public async Task Every_management_page_shows_where_you_are()
    {
        var page = await app.LogInAsync();

        foreach (var (path, link) in new[] { ("/Admin", "Items"), ("/Admin/Bin", "Bin"), ("/Admin/Collections/Edit", "Collections"), ("/Admin/Items/Edit/1", "Items") })
        {
            await page.GotoAsync(path);
            Assert.Equal("page", await page.Locator($".admin-nav a:text-is('{link}')").GetAttributeAsync("aria-current"));
        }
    }

    [Fact]
    public async Task Text_items_get_a_tile_that_does_not_look_like_a_broken_picture()
    {
        var page = await app.NewPageAsync();
        await page.GotoAsync("/search?q=letter");

        Assert.Equal("Text", await page.InnerTextAsync(".text-tile"));
    }

    [Fact]
    public async Task The_browsers_font_size_setting_takes_effect()
    {
        var page = await app.NewPageAsync();
        await page.GotoAsync("/");

        // A fixed size on the root element would ignore people who set a larger font in their browser.
        var rootHasNoFixedSize = await page.EvaluateAsync<bool>("""
            () => [...document.styleSheets].flatMap(sheet => [...sheet.cssRules])
                .filter(rule => rule.selectorText === 'html' || rule.selectorText === ':root')
                .every(rule => !rule.style.fontSize || !rule.style.fontSize.endsWith('px'))
            """);

        Assert.True(rootHasNoFixedSize);
    }

    [Fact]
    public async Task Dates_for_curators_write_the_month_out()
    {
        var page = await app.LogInAsync();
        await page.GotoAsync("/Admin");

        var lastChanged = await page.InnerTextAsync("tbody tr td:last-child");

        Assert.Matches(@"\p{L}{3,}", lastChanged);
        Assert.DoesNotMatch(@"\d+/\d+/\d+", lastChanged);
    }

    [Fact]
    public async Task A_double_click_on_save_does_not_look_like_someone_elses_change()
    {
        var page = await app.LogInAsync();
        await page.GotoAsync("/Admin/Items/Edit/2");

        await page.EvaluateAsync("""
            () => {
                const button = [...document.querySelectorAll('main form button[type=submit]')].find(b => b.textContent.trim() === 'Save');
                button.click();
                button.click();
            }
            """);
        await page.WaitForSelectorAsync(".alert-success");

        Assert.DoesNotContain("Someone else changed", await page.InnerTextAsync("main"));
    }
}
