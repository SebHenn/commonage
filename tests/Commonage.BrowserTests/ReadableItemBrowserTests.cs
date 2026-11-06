using Microsoft.Playwright;

namespace Commonage.BrowserTests;

/// <summary>Reading a scanned letter on a phone, and a curator putting its pages in order.</summary>
[Collection(nameof(BrowserCollection))]
public class ReadableItemBrowserTests(BrowserFixture app)
{
    private const string Cards = "[data-photo-card]";

    private static FilePayload Photo(string name, int width = 0) => new()
    {
        Name = name,
        MimeType = "image/jpeg",
        Buffer = [.. File.ReadAllBytes(Path.Combine("Fixtures", "photo.jpg")), .. Guid.NewGuid().ToByteArray()],
    };

    private static Task WaitForPicturesAsync(IPage page, string selector, int count) =>
        page.WaitForFunctionAsync(
            "([selector, count]) => { const pictures = [...document.querySelectorAll(selector)]; return pictures.length >= count && pictures.every(p => p.complete && p.naturalWidth > 0); }",
            new object[] { selector, count },
            new PageWaitForFunctionOptions { Timeout = 60000 });

    /// <summary>The demo's letter, once its pages have their previews.</summary>
    private static async Task OpenLetterAsync(IPage page)
    {
        await page.GotoAsync("/search?q=pitch");
        await page.ClickAsync("main a:has-text('Letter about the new pitch')");
        for (var i = 0; i < 60 && await page.Locator("figure .photo-link").CountAsync() < 2; i++)
        {
            await page.WaitForTimeoutAsync(1000);
            await page.ReloadAsync();
        }
    }

    private async Task<IPage> ItemWithPhotosAsync(string title, params string[] names)
    {
        var page = await app.LogInAsync();
        await page.GotoAsync("/Admin/Items/Edit");
        await page.FillAsync("[name='Input.Title']", title);
        await page.SelectOptionAsync("[name='Input.CollectionId']", new SelectOptionValue { Index = 1 });
        await page.ClickAsync("#item-form button[type=submit]:text-is('Save as draft')");
        await page.WaitForSelectorAsync(".alert-success");
        await page.SetInputFilesAsync("#photo-upload", [.. names.Select(name => Photo(name))]);
        await page.ClickAsync("form[data-upload-queue] button[type=submit]");
        await page.WaitForFunctionAsync("count => document.querySelectorAll('[data-photo-card]').length === count", names.Length, new PageWaitForFunctionOptions { Timeout = 30000 });
        await WaitForPicturesAsync(page, $"{Cards} img", names.Length);
        return page;
    }

    private static Task<string[]> OrderAsync(IPage page) =>
        page.EvaluateAsync<string[]>("[...document.querySelectorAll('[data-photo-card] .text-truncate')].map(name => name.textContent.trim())");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task On_a_phone_a_letter_can_be_read_large_page_by_page(bool javaScript)
    {
        var page = await app.NewPageAsync(390, 844, javaScript);

        await OpenLetterAsync(page);

        Assert.Contains("Photo 1 of 2", await page.InnerTextAsync("#photo-1 figcaption"));
        Assert.Contains("First page", await page.InnerTextAsync("#photo-1 figcaption"));
        Assert.Contains("Photo 2 of 2", await page.InnerTextAsync("#photo-2 figcaption"));
        Assert.Contains("the town council has agreed", await page.InnerTextAsync("main"));
        Assert.Equal("A handwritten letter in blue ink, first page, folded once across the middle.", await page.GetAttributeAsync("#photo-1 img", "alt"));

        var overflow = await page.EvaluateAsync<int>("document.documentElement.scrollWidth - document.documentElement.clientWidth");
        Assert.True(overflow <= 0, $"Scrolls sideways by {overflow} px.");

        // The strip goes to the page.
        await page.ClickAsync(".photo-strip a[aria-label='Photo 2 of 2']");
        await page.WaitForURLAsync("**#photo-2");

        // And the large one is a picture as big as it says, in the browser's own view, which can zoom.
        await page.ClickAsync("#photo-2 .photo-link");
        await page.WaitForURLAsync("**/large");
        if (javaScript)
        {
            var size = await page.EvaluateAsync<int[]>("[document.images[0].naturalWidth, document.images[0].naturalHeight]");
            Assert.Equal([2118, 3000], size);
        }
    }

    [Fact]
    public async Task The_letters_page_passes_the_accessibility_check_and_prints_without_navigation()
    {
        var page = await app.NewPageAsync();

        await OpenLetterAsync(page);
        await AccessibilityTests.AssertNoViolationsAsync(page, "an item with several pages");

        await page.EmulateMediaAsync(new PageEmulateMediaOptions { Media = Media.Print });
        Assert.False(await page.Locator(".photo-strip").IsVisibleAsync());
        Assert.False(await page.Locator(".item-neighbours").IsVisibleAsync());
        Assert.True(await page.Locator("#photo-1 img").IsVisibleAsync());
    }

    [Fact]
    public async Task The_large_picture_opens_in_the_browsers_own_view_which_can_zoom()
    {
        var page = await app.NewPageAsync();
        await OpenLetterAsync(page);

        await page.ClickAsync("#photo-1 .photo-link");
        await page.WaitForURLAsync("**/large");
        await page.WaitForFunctionAsync("() => document.images[0]?.complete");

        // Centred and fitted to the window. The security policy of a page would stop the browser from doing both.
        var fitted = await page.EvaluateAsync<double[]>("(() => { const box = document.images[0].getBoundingClientRect(); return [box.left, box.width]; })()");
        Assert.True(fitted[0] > 0, "The picture is not centred.");
        Assert.True(fitted[1] < 2118, "The picture is not fitted to the window.");

        await page.ClickAsync("img");

        await page.WaitForFunctionAsync("() => document.images[0].getBoundingClientRect().width === 2118");
    }

    [Fact]
    public async Task A_date_leads_to_everything_from_that_time()
    {
        var page = await app.NewPageAsync(390, 844);
        await page.GotoAsync("/search?q=bakery");
        await page.ClickAsync("main a:has-text('bakery')");

        await page.ClickAsync("main .item-date");

        Assert.Contains("from=1950", page.Url);
        Assert.Contains("to=1959", page.Url);
        Assert.Contains("bakery", await page.InnerTextAsync("main"));
    }

    [Fact]
    public async Task An_item_leads_on_to_others()
    {
        var page = await app.NewPageAsync(390, 844);
        await page.GotoAsync("/search?q=bakery");
        await page.ClickAsync("main a:has-text('bakery')");
        var title = await page.InnerTextAsync("h1");

        Assert.True(await page.Locator("h2:has-text('More from Mill Street')").IsVisibleAsync());
        Assert.True(await page.Locator("h2:has-text('From the same time')").IsVisibleAsync());
        await AccessibilityTests.AssertNoViolationsAsync(page, "an item with what surrounds it");

        await page.ClickAsync(".item-neighbours a >> nth=0");

        Assert.NotEqual(title, await page.InnerTextAsync("h1"));
    }

    [Fact]
    public async Task A_curator_moves_photos_without_losing_what_was_typed_or_the_place_on_the_page()
    {
        var page = await ItemWithPhotosAsync($"Three pages {Guid.NewGuid():N}", "one.jpg", "two.jpg", "three.jpg");
        var navigations = 0;
        page.FrameNavigated += (_, _) => navigations++;
        var typed = $"{Cards}:has-text('one.jpg') textarea[name^='Input.Captions']";
        await page.FillAsync(typed, "Not saved yet");

        await page.ClickAsync($"{Cards}:has-text('one.jpg') button[value=later]");
        await page.WaitForFunctionAsync("() => document.querySelector('[data-photo-card] .text-truncate').textContent.trim() === 'two.jpg'");

        Assert.Equal(["two.jpg", "one.jpg", "three.jpg"], await OrderAsync(page));
        Assert.Equal("Not saved yet", await page.InputValueAsync(typed));
        Assert.Equal(0, navigations);
        Assert.Equal("later", await page.EvaluateAsync<string>("document.activeElement.value"));
        Assert.Contains("Photo 2 of 3", await page.Locator($"{Cards}:has-text('one.jpg') [data-photo-number]").InnerTextAsync());
        Assert.Equal("Photo 2 of 3", await page.InnerTextAsync("#photo-moved"));

        // The last one cannot go later any more, and the focus is not lost with the button that stopped working.
        await page.ClickAsync($"{Cards}:has-text('one.jpg') button[value=later]");
        await page.WaitForFunctionAsync("() => document.querySelectorAll('[data-photo-card] .text-truncate')[2].textContent.trim() === 'one.jpg'");
        Assert.True(await page.Locator($"{Cards}:has-text('one.jpg') button[value=later]").IsDisabledAsync());
        Assert.Equal("earlier", await page.EvaluateAsync<string>("document.activeElement.value"));

        await page.ClickAsync($"{Cards}:has-text('three.jpg') button[value=first]");
        await page.WaitForFunctionAsync("() => document.querySelector('[data-photo-card] .text-truncate').textContent.trim() === 'three.jpg'");
        Assert.True(await page.Locator($"{Cards}:has-text('three.jpg') [data-photo-cover]").IsVisibleAsync());
        Assert.False(await page.Locator($"{Cards}:has-text('two.jpg') [data-photo-cover]").IsVisibleAsync());
        Assert.False(await page.Locator($"{Cards}:has-text('three.jpg') button[value=first]").IsVisibleAsync());
        // Away from the buttons and past their colour change on hover, which would otherwise be measured halfway.
        await page.Mouse.MoveAsync(0, 0);
        await page.WaitForTimeoutAsync(400);
        await AccessibilityTests.AssertNoViolationsAsync(page, "photos being put in order");

        // Saved after all that, the order is what it looks like and nobody is told that someone else changed the item.
        await page.ClickAsync("#item-form button[type=submit] >> nth=0");
        await page.WaitForSelectorAsync(".alert-success");
        Assert.Equal(["three.jpg", "two.jpg", "one.jpg"], await OrderAsync(page));
        Assert.Equal("Not saved yet", await page.InputValueAsync(typed));
    }

    [Fact]
    public async Task A_curator_turns_a_photo_and_sees_it_turned_without_a_reload()
    {
        var page = await ItemWithPhotosAsync($"A sideways scan {Guid.NewGuid():N}", "sideways.jpg");
        // A page opened later, with nothing else it is waiting for.
        await page.ReloadAsync();
        await WaitForPicturesAsync(page, $"{Cards} img", 1);
        var navigations = 0;
        page.FrameNavigated += (_, _) => navigations++;
        var before = await page.EvaluateAsync<int[]>($"(() => {{ const p = document.querySelector('{Cards} img'); return [p.naturalWidth, p.naturalHeight]; }})()");

        await page.ClickAsync($"{Cards} form[data-photo-turn] button");
        await page.WaitForFunctionAsync(
            "([cards, width, height]) => { const p = document.querySelector(cards + ' img'); return p && p.complete && p.naturalWidth === height && p.naturalHeight === width; }",
            new object[] { Cards, before[0], before[1] },
            new PageWaitForFunctionOptions { Timeout = 30000 });

        Assert.Equal(0, navigations);
    }

    [Fact]
    public async Task Without_javascript_a_curator_moves_and_turns_a_photo_and_comes_back_to_it()
    {
        var page = await ItemWithPhotosAsync($"Without a script {Guid.NewGuid():N}", "one.jpg", "two.jpg");
        var url = page.Url;
        var context = await app.Chrome.NewContextAsync(new BrowserNewContextOptions { BaseURL = app.BaseUrl, JavaScriptEnabled = false, Locale = "en", StorageState = await page.Context.StorageStateAsync() });
        var plain = await context.NewPageAsync();
        await plain.GotoAsync(url);

        await plain.ClickAsync($"{Cards}:has-text('two.jpg') button[value=earlier]");

        Assert.Contains("#photo-", plain.Url);
        Assert.Equal(["two.jpg", "one.jpg"], await plain.Locator($"{Cards} .text-truncate").AllInnerTextsAsync());

        await plain.ClickAsync($"{Cards}:has-text('two.jpg') form[data-photo-turn] button");

        Assert.Contains("Turning the photo", await plain.InnerTextAsync($"{Cards}:has-text('two.jpg')"));
        await context.CloseAsync();
    }
}
