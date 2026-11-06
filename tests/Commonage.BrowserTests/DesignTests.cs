using Microsoft.Playwright;

namespace Commonage.BrowserTests;

/// <summary>What M11's design promises, measured in the browser.</summary>
[Collection(nameof(BrowserCollection))]
public class DesignTests(BrowserFixture app)
{
    [Fact]
    public async Task The_language_menu_at_the_top_switches_the_language_and_stays_on_the_page()
    {
        var page = await app.NewPageAsync(390, 844);
        await page.GotoAsync("/search?q=flood");

        await page.ClickAsync("details.language-picker summary");
        await page.ClickAsync("button[value=de]");

        await page.WaitForSelectorAsync("html[lang=de]");
        Assert.Contains("q=flood", page.Url);
        Assert.Contains("Deutsch", await page.InnerTextAsync("details.language-picker summary"));
    }

    [Fact]
    public async Task The_language_menu_closes_with_escape_and_when_clicking_elsewhere()
    {
        var page = await app.NewPageAsync();
        await page.GotoAsync("/");

        await page.ClickAsync("details.language-picker summary");
        Assert.True(await page.Locator("button[value=de]").IsVisibleAsync());
        await page.Keyboard.PressAsync("Escape");
        Assert.False(await page.Locator("button[value=de]").IsVisibleAsync());

        await page.ClickAsync("details.language-picker summary");
        await page.ClickAsync("h1");
        Assert.False(await page.Locator("button[value=de]").IsVisibleAsync());
    }

    [Fact]
    public async Task Without_javascript_the_language_menu_still_works()
    {
        var page = await app.NewPageAsync(javaScript: false);
        await page.GotoAsync("/");

        await page.ClickAsync("details.language-picker summary");
        await page.ClickAsync("button[value=de]");

        Assert.Equal("de", await page.GetAttributeAsync("html", "lang"));
    }

    [Fact]
    public async Task Choosing_from_a_filter_list_applies_at_once()
    {
        var page = await app.NewPageAsync();
        await page.GotoAsync("/search");

        await page.SelectOptionAsync("#filter-sort", "OldestFirst");
        await page.WaitForURLAsync("**sort=OldestFirst**");

        Assert.Equal("OldestFirst", await page.InputValueAsync("#filter-sort"));
    }

    [Fact]
    public async Task A_filter_can_be_removed_on_its_own()
    {
        var page = await app.NewPageAsync();
        await page.GotoAsync("/search?from=1950&to=1970");

        await page.ClickAsync("a.tag-link:has-text('From year: 1950')");

        Assert.DoesNotContain("from=1950", page.Url);
        Assert.Contains("to=1970", page.Url);
    }

    [Fact]
    public async Task On_a_wide_screen_an_items_text_sits_beside_its_photo()
    {
        var page = await app.NewPageAsync();
        await page.GotoAsync("/items/1");

        var photo = await page.Locator(".item-media").BoundingBoxAsync();
        var text = await page.Locator(".item-text").BoundingBoxAsync();

        Assert.True(text!.X >= photo!.X + photo.Width);
        Assert.True(text.Y < photo.Y + photo.Height);
    }

    [Fact]
    public async Task On_a_phone_the_title_comes_first_then_the_photo_then_the_text()
    {
        var page = await app.NewPageAsync(390, 844);
        await page.GotoAsync("/items/1");

        var title = await page.Locator("h1").BoundingBoxAsync();
        var photo = await page.Locator(".item-media").BoundingBoxAsync();
        var text = await page.Locator(".item-text").BoundingBoxAsync();

        Assert.True(title!.Y < photo!.Y);
        Assert.True(photo.Y < text!.Y);
    }

    [Fact]
    public async Task The_first_photo_of_an_item_loads_at_once_and_cards_are_not_cut_to_fit()
    {
        var page = await app.NewPageAsync();
        await page.GotoAsync("/items/1");
        Assert.Equal("eager", await page.GetAttributeAsync(".media-preview", "loading"));

        await page.GotoAsync("/collections/1");
        var fit = await page.EvaluateAsync<string>("getComputedStyle(document.querySelector('.item-card img')).objectFit");

        Assert.Equal("contain", fit);
    }

    [Fact]
    public async Task A_fine_phone_screen_gets_the_larger_picture()
    {
        var context = await app.Chrome.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = app.BaseUrl,
            ViewportSize = new ViewportSize { Width = 390, Height = 844 },
            DeviceScaleFactor = 3,
            IsMobile = true,
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync("/collections/1");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var source = await page.EvaluateAsync<string>("document.querySelector('.item-card img').currentSrc");
        await context.CloseAsync();

        Assert.EndsWith("/card", source);
    }

    [Fact]
    public async Task Collections_are_found_from_the_navigation_and_lead_back()
    {
        var page = await app.NewPageAsync();
        await page.GotoAsync("/");

        await page.ClickAsync("nav.navbar a:text-is('Collections')");
        Assert.EndsWith("/collections", page.Url);

        await page.ClickAsync(".collection-card >> nth=0");
        await page.ClickAsync(".breadcrumb a");
        Assert.EndsWith("/collections", page.Url);
    }

    [Fact]
    public async Task The_longest_tag_someone_can_type_does_not_push_a_phone_page_sideways()
    {
        var tag = new string('x', 35) + Guid.NewGuid().ToString("N")[..15];
        var curator = await app.LogInAsync();
        await curator.GotoAsync("/Admin/Items/Edit");
        await curator.FillAsync("[name='Input.Title']", "An item with a very long tag");
        await curator.SelectOptionAsync("[name='Input.CollectionId']", new SelectOptionValue { Index = 1 });
        await curator.FillAsync("[name='Input.Tags']", tag);
        await curator.ClickAsync("#item-form button:text-is('Save and publish')");
        await curator.WaitForSelectorAsync(".alert-success");
        var id = curator.Url.Split('?')[0].Split('/').Last();

        var phone = await app.NewPageAsync(320, 640);
        foreach (var path in new[] { "/", $"/items/{id}", "/search", $"/search?tag={tag}" })
        {
            await phone.GotoAsync(path);
            var overflow = await phone.EvaluateAsync<int>("document.documentElement.scrollWidth - document.documentElement.clientWidth");
            Assert.True(overflow <= 0, $"{path} scrolls sideways by {overflow} px.");
        }

        // The tag must actually have been on the page for this to say anything.
        await phone.GotoAsync($"/items/{id}");
        Assert.Contains(tag, await phone.InnerTextAsync("main"));
    }
}
