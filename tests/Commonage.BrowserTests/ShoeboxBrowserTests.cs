using Microsoft.Playwright;

namespace Commonage.BrowserTests;

/// <summary>What only a browser can prove about turning a shoebox of scans into drafts.</summary>
[Collection(nameof(BrowserCollection))]
public class ShoeboxBrowserTests(BrowserFixture app)
{
    private const string ManyPage = "/Admin/Items/AddMany";
    private const string Rows = ".upload-queue li";

    /// <summary>A picture nothing else has sent: the same image with something else after its end.</summary>
    private static FilePayload Scan(string name) => new()
    {
        Name = name,
        MimeType = "image/jpeg",
        Buffer = [.. File.ReadAllBytes(Path.Combine("Fixtures", "photo.jpg")), .. Guid.NewGuid().ToByteArray()],
    };

    private static string Tag() => $"box{Guid.NewGuid():N}";

    private static async Task FillManyFormAsync(IPage page, string? tag = null)
    {
        await page.GotoAsync(ManyPage);
        await page.SelectOptionAsync("[name='Input.CollectionId']", new SelectOptionValue { Index = 1 });
        if (tag is not null)
            await page.FillAsync("[name='Input.Tags']", tag);
    }

    private static async Task SendAsync(IPage page, params FilePayload[] files)
    {
        await page.SetInputFilesAsync("#photo-upload", files);
        await page.ClickAsync("main form button:text-is('Add these photos')");
    }

    private static Task WaitForTextAsync(IPage page, string text) =>
        page.Locator($"main :text('{text}')").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 30000 });

    /// <summary>Real pictures, loaded and all: an img element alone says nothing about what a person sees.</summary>
    private static Task WaitForPicturesAsync(IPage page, string selector, int count) =>
        page.WaitForFunctionAsync(
            "([selector, count]) => { const pictures = [...document.querySelectorAll(selector)]; return pictures.length >= count && pictures.every(p => p.complete && p.naturalWidth > 0); }",
            new object[] { selector, count },
            new PageWaitForFunctionOptions { Timeout = 30000 });

    private async Task<IPage> WithUntitledDraftsAsync(string tag, int count)
    {
        var page = await app.LogInAsync();
        await FillManyFormAsync(page, tag);
        await SendAsync(page, [.. Enumerable.Range(1, count).Select(n => Scan($"scan-{n}.jpg"))]);
        await WaitForTextAsync(page, $"Finished. Added: {count}.");
        return page;
    }

    [Fact]
    public async Task A_batch_goes_one_file_at_a_time_and_shows_what_became_of_each()
    {
        var page = await app.LogInAsync();
        var posts = 0;
        page.Request += (_, request) =>
        {
            if (request.Method == "POST" && request.Url.EndsWith(ManyPage))
                posts++;
        };
        await FillManyFormAsync(page);
        var scan = Scan("first.jpg");
        var again = new FilePayload { Name = "first-again.jpg", MimeType = scan.MimeType, Buffer = scan.Buffer };

        await SendAsync(page, scan, Scan("second.jpg"), again, new FilePayload { Name = "notes.jpg", MimeType = "image/jpeg", Buffer = "not a picture"u8.ToArray() });
        await WaitForTextAsync(page, "Finished. Added: 2. Already in the archive: 1. Refused: 1.");

        Assert.Equal(4, posts);
        // A batch that went well is not told off for the file field being empty again.
        Assert.Equal("", (await page.InnerTextAsync("[data-valmsg-for=files]")).Trim());
        Assert.Equal("1", await page.EvaluateAsync<string>("String(document.querySelector('.upload-progress').value)"));
        // What needs a person is at the top of the list, and scrolled into view.
        var top = await page.Locator(Rows).First.GetAttributeAsync("data-state");
        Assert.True(top is "identical" or "refused", top);
        Assert.Contains("notes.jpg", await page.Locator($"{Rows}[data-state=refused]").InnerTextAsync());
        Assert.Contains("The archive already has this file", await page.Locator($"{Rows}[data-state=identical]").InnerTextAsync());

        await AccessibilityTests.AssertNoViolationsAsync(page, "the upload queue");

        await page.ClickAsync($"{Rows}[data-state=identical] button");
        await WaitForTextAsync(page, "Finished. Added: 3. Already in the archive: 0. Refused: 1.");

        // Previews come in by themselves, without a reload.
        await WaitForPicturesAsync(page, ".upload-queue img.upload-thumb", 3);

        await page.ClickAsync("main a:text-is('Go to the drafts without a title')");
        await page.WaitForURLAsync("**/Admin?status=Draft&needs=Title");
        Assert.Contains("first.jpg", await page.InnerTextAsync("main table"));
    }

    [Fact]
    public async Task A_lost_connection_stops_the_queue_and_it_goes_on_from_there_without_doubles()
    {
        var page = await app.LogInAsync();
        await FillManyFormAsync(page);
        var posts = 0;
        var waitingToLose = true;
        await page.RouteAsync($"**{ManyPage}", async route =>
        {
            if (route.Request.Method != "POST" || ++posts != 2 || !waitingToLose)
            {
                await route.ContinueAsync();
                return;
            }
            waitingToLose = false;
            // The worst moment: the photo has arrived, but nobody hears the answer.
            await route.FetchAsync();
            await route.AbortAsync();
        });

        await SendAsync(page, Scan("a.jpg"), Scan("b.jpg"), Scan("c.jpg"));
        await page.Locator("main [role=alert]:has-text('The connection to the archive was lost')").WaitForAsync();

        Assert.Equal(1, await page.Locator($"{Rows}[data-state=added]").CountAsync());
        Assert.Equal(2, await page.Locator($"{Rows}[data-state=waiting]").CountAsync());

        await page.ClickAsync("main button:text-is('Continue')");
        await WaitForTextAsync(page, "Finished. Added: 2. Already in the archive: 1. Refused: 0.");
    }

    [Fact]
    public async Task A_login_that_ran_out_is_said_plainly()
    {
        var page = await app.LogInAsync();
        await FillManyFormAsync(page);
        await page.Context.ClearCookiesAsync();

        await SendAsync(page, Scan("a.jpg"));

        await page.Locator("main [role=alert]:has-text('You are no longer logged in')").WaitForAsync();
        Assert.Equal("_blank", await page.GetAttributeAsync("main [role=alert] a", "target"));
        Assert.Equal(1, await page.Locator($"{Rows}[data-state=waiting]").CountAsync());
    }

    [Fact]
    public async Task Leaving_while_photos_are_on_their_way_asks_first()
    {
        var page = await app.LogInAsync();
        await FillManyFormAsync(page);
        var hold = new TaskCompletionSource();
        await page.RouteAsync($"**{ManyPage}", async route =>
        {
            if (route.Request.Method == "POST")
                await hold.Task;
            await route.ContinueAsync();
        });
        var dialogs = new List<string>();
        page.Dialog += async (_, dialog) =>
        {
            dialogs.Add(dialog.Type);
            await dialog.DismissAsync();
        };

        await SendAsync(page, Scan("a.jpg"));
        await page.Locator($"{Rows}[data-state=sending]").WaitForAsync();
        await page.ClickAsync(".breadcrumb a");
        for (var i = 0; i < 100 && dialogs.Count == 0; i++)
            await Task.Delay(50);
        Assert.Equal(["beforeunload"], dialogs);

        dialogs.Clear();
        hold.SetResult();
        await WaitForTextAsync(page, "Finished. Added: 1.");
        await page.ClickAsync(".breadcrumb a");
        await page.WaitForURLAsync("**/Admin");
        Assert.Empty(dialogs);
    }

    [Fact]
    public async Task Photos_added_to_an_item_appear_without_a_reload_and_keep_what_was_typed()
    {
        var page = await app.LogInAsync();
        await page.GotoAsync("/Admin/Items/Edit");
        await page.FillAsync("[name='Input.Title']", "An album");
        await page.SelectOptionAsync("[name='Input.CollectionId']", new SelectOptionValue { Index = 1 });
        await page.ClickAsync("#item-form button:text-is('Save as draft')");
        await page.WaitForURLAsync("**/Admin/Items/Edit/*");
        Assert.False(await page.Locator("[data-photos-present]").IsVisibleAsync());
        await page.EvaluateAsync("window.sameLoad = true");
        await page.FillAsync("[name='Input.Description']", "Typed but not saved");

        await page.SetInputFilesAsync("#photo-upload", new[] { Scan("one.jpg"), Scan("two.jpg") });
        await page.ClickAsync("main form button:text-is('Upload')");
        await WaitForPicturesAsync(page, "#photo-cards img", 2);

        Assert.True(await page.EvaluateAsync<bool>("window.sameLoad === true"));
        Assert.Equal("Typed but not saved", await page.InputValueAsync("[name='Input.Description']"));
        Assert.True(await page.Locator("[data-photos-present]").IsVisibleAsync());

        // A card that arrived this way does everything a card does: its description is saved with the item.
        await page.FillAsync("#photo-cards textarea >> nth=1", "The second photo");
        await page.ClickAsync("#item-form button[type=submit] >> nth=0");
        await page.WaitForSelectorAsync(".alert-success");
        Assert.Equal("The second photo", await page.InputValueAsync("#photo-cards textarea >> nth=1"));
        Assert.Equal("Typed but not saved", await page.InputValueAsync("[name='Input.Description']"));
    }

    [Fact]
    public async Task A_new_item_takes_its_photos_on_the_form_and_says_when_they_are_too_much()
    {
        var page = await app.LogInAsync();
        await page.GotoAsync("/Admin/Items/Edit");
        await page.FillAsync("[name='Input.Title']", "With a photo from the start");
        await page.SelectOptionAsync("[name='Input.CollectionId']", new SelectOptionValue { Index = 1 });
        await page.SetInputFilesAsync("#files", Scan("start.jpg"));

        // The real limit is a gigabyte, and a test need not send one.
        await page.EvaluateAsync("document.getElementById('files').dataset.maxTotalBytes = '10'");
        await page.ClickAsync("#item-form button:text-is('Save as draft')");
        await page.Locator("[data-valmsg-for=files]:has-text('Save the item first')").WaitForAsync();
        Assert.EndsWith("/Admin/Items/Edit", page.Url);

        await page.EvaluateAsync("document.getElementById('files').dataset.maxTotalBytes = '1073741824'");
        await page.ClickAsync("#item-form button:text-is('Save as draft')");
        await page.WaitForURLAsync("**/Admin/Items/Edit/*");

        Assert.Contains("start.jpg", await page.InnerTextAsync("#photo-cards"));
        Assert.Contains("Photos uploaded: 1", await page.InnerTextAsync("main"));
    }

    [Fact]
    public async Task An_untitled_draft_shows_the_photo_first_and_can_be_published_once_named()
    {
        var tag = Tag();
        var page = await WithUntitledDraftsAsync(tag, 1);

        await page.GotoAsync($"/Admin?tag={tag}");
        await page.ClickAsync("main table a:text-is('Untitled')");
        await page.WaitForURLAsync("**/Admin/Items/Edit/*");

        Assert.Contains("Edit: Untitled", await page.InnerTextAsync("h1"));
        Assert.Contains("has no title yet", await page.InnerTextAsync("main"));
        var photosBeforeForm = await page.EvaluateAsync<bool>(
            "!!(document.querySelector('#photo-cards').compareDocumentPosition(document.querySelector('#item-form')) & Node.DOCUMENT_POSITION_FOLLOWING)");
        Assert.True(photosBeforeForm);

        await page.FillAsync("[name='Input.Title']", "The harvest of 1950");
        await page.ClickAsync("#item-form button:text-is('Save and publish')");
        await page.WaitForSelectorAsync(".alert-success");

        var id = page.Url.Split('?')[0].Split('/').Last();
        var visitor = await app.NewPageAsync();
        await visitor.GotoAsync($"/items/{id}");
        Assert.Contains("The harvest of 1950", await visitor.InnerTextAsync("h1"));
    }

    [Fact]
    public async Task Pressing_enter_after_typing_a_title_goes_on_to_the_next_draft()
    {
        var tag = Tag();
        var page = await WithUntitledDraftsAsync(tag, 2);
        await page.GotoAsync($"/Admin?tag={tag}&sort=OldestFirst");
        await page.ClickAsync("main table a:text-is('Untitled') >> nth=0");
        await page.WaitForURLAsync("**/Admin/Items/Edit/*");
        var first = page.Url.Split('?')[0].Split('/').Last();

        await page.FillAsync("[name='Input.Title']", "The first photo");
        await page.PressAsync("[name='Input.Title']", "Enter");
        await page.WaitForURLAsync(url => url.Split('?')[0].Split('/').Last() != first);

        Assert.Contains("Edit: Untitled", await page.InnerTextAsync("h1"));
        await page.GotoAsync($"/Admin/Items/Edit/{first}");
        Assert.Equal("The first photo", await page.InputValueAsync("[name='Input.Title']"));
    }

    [Fact]
    public async Task Ticked_items_can_be_changed_together_after_seeing_how_many()
    {
        var tag = Tag();
        var page = await WithUntitledDraftsAsync(tag, 3);
        await page.GotoAsync($"/Admin?tag={tag}");

        await page.CheckAsync("[data-tick-all]");
        Assert.Equal(3, await page.Locator("[name=selected]:checked").CountAsync());
        await page.UncheckAsync("[name=selected] >> nth=0");
        Assert.False(await page.IsCheckedAsync("[data-tick-all]"));

        await page.SelectOptionAsync("#bulk-action", "SetDate");
        await page.ClickAsync("#bulk-form button:text-is('For the ticked items')");
        await page.Locator("main :text('Items chosen: 2')").WaitForAsync();
        await AccessibilityTests.AssertNoViolationsAsync(page, "the choice for many items");

        await page.FillAsync("[name=Date]", "1950s");
        await page.ClickAsync("main form button:text-is('Change: 2')");
        await page.Locator("main :text('Items changed: 2. Left as they were: 0.')").WaitForAsync();

        Assert.Contains($"tag={tag}", page.Url);
    }

    [Fact]
    public async Task Publishing_a_whole_list_of_untitled_drafts_offers_nothing_to_press()
    {
        var tag = Tag();
        var page = await WithUntitledDraftsAsync(tag, 2);
        await page.GotoAsync($"/Admin?tag={tag}");

        await page.SelectOptionAsync("#bulk-action", "Publish");
        await page.ClickAsync("#bulk-form button:has-text('For all in this list: 2')");
        await page.Locator("main :text('no title yet: 2.')").WaitForAsync();

        Assert.Equal(0, await page.Locator("main form button[type=submit]").CountAsync());
        var text = await page.InnerTextAsync("main");
        Assert.Contains("These become public at once: 0.", text);
        Assert.Contains("scan-1.jpg", text);
    }

    [Fact]
    public async Task Without_javascript_many_photos_still_become_drafts_and_the_limit_is_stated()
    {
        var tag = Tag();
        var page = await app.LogInAsync(javaScript: false);
        await FillManyFormAsync(page, tag);
        Assert.Contains("at most 1 GB", await page.InnerTextAsync("main form"));

        await SendAsync(page, Scan("a.jpg"), Scan("b.jpg"));
        await page.WaitForURLAsync("**/Admin?status=Draft&needs=Title");
        Assert.Contains("Drafts created: 2", await page.InnerTextAsync("main"));

        // A list without JavaScript: boxes to tick, but no box that could only pretend to tick them all.
        await page.GotoAsync($"/Admin?tag={tag}");
        Assert.False(await page.Locator("[data-tick-all]").IsVisibleAsync());
        await page.CheckAsync("[name=selected] >> nth=0");
        await page.SelectOptionAsync("#bulk-action", "AddTags");
        await page.ClickAsync("#bulk-form button:text-is('For the ticked items')");
        await page.FillAsync("[name=Tags]", "by hand");
        await page.ClickAsync("main form button:text-is('Change: 1')");
        await page.Locator("main :text('Items changed: 1.')").WaitForAsync();
    }

    [Fact]
    public async Task The_queue_fits_on_a_phone_with_long_file_names()
    {
        var page = await app.LogInAsync(width: 390, height: 800);
        await FillManyFormAsync(page);
        var longName = $"{new string('x', 120)}.jpg";

        await SendAsync(page, Scan(longName), Scan("short.jpg"));
        await WaitForTextAsync(page, "Finished. Added: 2.");

        var overflow = await page.EvaluateAsync<int>("document.documentElement.scrollWidth - document.documentElement.clientWidth");
        Assert.True(overflow <= 0, $"The page scrolls sideways by {overflow} px.");
    }
}
