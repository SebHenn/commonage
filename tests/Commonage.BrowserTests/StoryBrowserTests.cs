using Microsoft.Playwright;

namespace Commonage.BrowserTests;

/// <summary>The story editor for curators: moving parts by keyboard and without JavaScript, asking before a part goes, and warning about unsaved text.</summary>
[Collection(nameof(BrowserCollection))]
public class StoryBrowserTests(BrowserFixture app)
{
    private static async Task<IPage> StoryWithTwoTextsAsync(IPage page, string title)
    {
        await page.GotoAsync("/Admin/Stories");
        await page.FillAsync("[name='NewTitle']", title);
        await page.ClickAsync("main form button:text-is('New story')");
        await page.WaitForURLAsync("**/Admin/Stories/Edit/*");
        foreach (var text in new[] { "First part", "Second part" })
        {
            await page.FillAsync("#newText", text);
            await page.ClickAsync("main form button:text-is('Add text')");
            await page.WaitForLoadStateAsync();
        }

        return page;
    }

    private static Task<string[]> TextsAsync(IPage page) =>
        page.EvaluateAsync<string[]>("[...document.querySelectorAll('ol.list-unstyled textarea[id^=text-]')].map(t => t.value)");

    [Fact]
    public async Task Moving_a_part_with_the_keyboard_keeps_the_place_in_the_list()
    {
        var page = await app.LogInAsync();
        await StoryWithTwoTextsAsync(page, "Keyboard story");

        await page.FocusAsync("button[aria-label='Later: Text 1 of 2']");
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForFunctionAsync("document.activeElement.id.startsWith('block-')");

        Assert.Equal(["Second part", "First part"], await TextsAsync(page));
        var focused = await page.EvaluateAsync<string>("document.activeElement.id");
        Assert.StartsWith("block-", focused);
        Assert.Equal("First part", await page.InputValueAsync($"#{focused} textarea"));
    }

    [Fact]
    public async Task Opening_the_editor_does_not_move_focus_into_a_part()
    {
        var page = await app.LogInAsync();
        await StoryWithTwoTextsAsync(page, "Quiet story");

        await page.GotoAsync(page.Url.Split('#')[0].Split('?')[0]);

        Assert.Equal(0, await page.Locator("[data-focus-on-load]").CountAsync());
        Assert.DoesNotContain("block-", await page.EvaluateAsync<string>("document.activeElement.id"));
    }

    [Fact]
    public async Task Without_javascript_a_part_can_be_moved_and_removed()
    {
        var page = await app.LogInAsync(javaScript: false);
        await StoryWithTwoTextsAsync(page, "Plain story");

        await page.ClickAsync("button[aria-label='Earlier: Text 2 of 2']");
        Assert.Equal(["Second part", "First part"], await TextsAsync(page));

        await page.ClickAsync("button[aria-label='Remove: Text 1 of 2']");
        Assert.Equal(["First part"], await TextsAsync(page));
    }

    [Fact]
    public async Task Removing_a_part_asks_first_and_no_keeps_it()
    {
        var page = await app.LogInAsync();
        await StoryWithTwoTextsAsync(page, "Remove story");
        var dialogs = new List<string>();
        page.Dialog += async (_, dialog) => { dialogs.Add(dialog.Type); await dialog.DismissAsync(); };

        await page.ClickAsync("button[aria-label='Remove: Text 1 of 2']");
        await page.WaitForTimeoutAsync(300);

        Assert.Equal(["confirm"], dialogs);
        Assert.Equal(["First part", "Second part"], await TextsAsync(page));
    }

    [Fact]
    public async Task Leaving_with_an_edited_text_part_asks_first()
    {
        var page = await app.LogInAsync();
        await StoryWithTwoTextsAsync(page, "Warning story");
        var dialogs = new List<string>();
        page.Dialog += async (_, dialog) => { dialogs.Add(dialog.Type); await dialog.DismissAsync(); };

        var editUrl = page.Url;
        await page.ClickAsync(".breadcrumb a >> nth=1");
        await page.WaitForURLAsync("**/Admin/Stories");
        Assert.Empty(dialogs);

        await page.GotoAsync(editUrl);
        await page.FillAsync("textarea[id^=text-] >> nth=0", "Changed but not saved");
        await page.ClickAsync(".breadcrumb a >> nth=1");
        for (var i = 0; i < 100 && dialogs.Count == 0; i++)
            await Task.Delay(50);

        Assert.Equal(["beforeunload"], dialogs);
        Assert.Equal("Changed but not saved", await page.InputValueAsync("textarea[id^=text-] >> nth=0"));
    }

    [Fact]
    public async Task The_demo_story_reads_without_its_draft_item_and_curators_see_the_draft_with_its_reason()
    {
        const string draft = "Unsorted photos from the club house";
        var visitor = await app.NewPageAsync();
        await visitor.GotoAsync("/stories/1");
        Assert.Contains("Mill Street over thirty years", await visitor.InnerTextAsync("h1"));
        Assert.DoesNotContain(draft, await visitor.ContentAsync());
        Assert.DoesNotContain("Still to be sorted", await visitor.ContentAsync());
        await visitor.GotoAsync("/stories");
        Assert.True(await visitor.Locator("main a:has-text('Mill Street over thirty years')").IsVisibleAsync());

        var curator = await app.LogInAsync();
        await curator.GotoAsync("/Admin/Stories/Edit/1");
        Assert.Contains(draft, await curator.InnerTextAsync("main"));
        Assert.Contains("Visitors do not see this item: it is not published.", await curator.InnerTextAsync("main"));
    }
}
