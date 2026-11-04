using Microsoft.Playwright;

namespace Commonage.BrowserTests;

/// <summary>What the JavaScript in wwwroot/js does, and that the main tasks also work without JavaScript.</summary>
[Collection(nameof(BrowserCollection))]
public class BehaviourTests(BrowserFixture app)
{
    private const string Description = "[name='Input.Description']";
    private const string BackToList = ".breadcrumb a";

    /// <summary>Answers every dialog as told and writes down which kinds appeared.</summary>
    private static List<string> AnswerDialogs(IPage page, bool accept)
    {
        var seen = new List<string>();
        page.Dialog += async (_, dialog) =>
        {
            seen.Add(dialog.Type);
            if (accept)
                await dialog.AcceptAsync();
            else
                await dialog.DismissAsync();
        };
        return seen;
    }

    private static string Button(string text) => $"main form button:text-is('{text}')";

    private async Task<int> CreateItemAsync(IPage page, string title)
    {
        await page.GotoAsync("/Admin/Items/Edit");
        await page.FillAsync("[name='Input.Title']", title);
        await page.SelectOptionAsync("[name='Input.CollectionId']", new SelectOptionValue { Index = 1 });
        await page.ClickAsync(Button("Save as draft"));
        await page.WaitForURLAsync("**/Admin/Items/Edit/*");
        return int.Parse(page.Url.Split('/').Last());
    }

    [Fact]
    public async Task Answering_no_to_a_confirmation_stops_the_action()
    {
        var page = await app.LogInAsync();
        var id = await CreateItemAsync(page, "Confirmation test");
        var dialogs = AnswerDialogs(page, accept: false);

        await page.ClickAsync(Button("Move to bin"));

        Assert.Equal(["confirm"], dialogs);
        Assert.Contains($"/Admin/Items/Edit/{id}", page.Url);
    }

    [Fact]
    public async Task Answering_yes_to_a_confirmation_goes_ahead()
    {
        var page = await app.LogInAsync();
        await CreateItemAsync(page, "Confirmation test yes");
        AnswerDialogs(page, accept: true);

        await page.ClickAsync(Button("Move to bin"));
        await page.WaitForURLAsync("**/Admin");

        Assert.Contains("The item is in the bin", await page.InnerTextAsync("main"));
    }

    [Fact]
    public async Task Leaving_with_unsaved_text_asks_first_and_staying_keeps_it()
    {
        var page = await app.LogInAsync();
        var id = await CreateItemAsync(page, "Unsaved text test");
        var dialogs = AnswerDialogs(page, accept: false);

        await page.ClickAsync(BackToList);
        await page.WaitForLoadStateAsync();
        Assert.Empty(dialogs);

        await page.GotoAsync($"/Admin/Items/Edit/{id}");
        await page.FillAsync(Description, "Typed but not saved");
        await page.ClickAsync(BackToList);
        await WaitForAsync(() => dialogs.Count == 1);

        Assert.Equal(["beforeunload"], dialogs);
        Assert.Contains($"/Admin/Items/Edit/{id}", page.Url);
        Assert.Equal("Typed but not saved", await page.InputValueAsync(Description));
    }

    [Fact]
    public async Task Uploading_a_photo_with_unsaved_text_asks_first()
    {
        var page = await app.LogInAsync();
        await CreateItemAsync(page, "Upload warning test");
        var dialogs = AnswerDialogs(page, accept: false);

        await page.FillAsync(Description, "Typed but not saved");
        await page.SetInputFilesAsync("#photo-upload", Path.Combine("Fixtures", "photo.jpg"));
        await page.ClickAsync(Button("Upload"));
        await WaitForAsync(() => dialogs.Count == 1);

        Assert.Equal(["beforeunload"], dialogs);
        Assert.Equal("Typed but not saved", await page.InputValueAsync(Description));
    }

    [Fact]
    public async Task Saving_the_form_itself_does_not_ask()
    {
        var page = await app.LogInAsync();
        await CreateItemAsync(page, "Saving test");
        var dialogs = AnswerDialogs(page, accept: false);

        await page.FillAsync(Description, "Saved on purpose");
        await page.ClickAsync(Button("Save as draft"));
        await page.WaitForSelectorAsync(".alert-success");

        Assert.Empty(dialogs);
        Assert.Equal("Saved on purpose", await page.InputValueAsync(Description));
    }

    [Fact]
    public async Task A_failed_validation_does_not_switch_off_the_warning()
    {
        var page = await app.LogInAsync();
        await CreateItemAsync(page, "Validation test");
        var dialogs = AnswerDialogs(page, accept: false);

        await page.FillAsync("[name='Input.Title']", "");
        await page.ClickAsync(Button("Save as draft"));
        await page.WaitForSelectorAsync(".field-validation-error");
        Assert.Empty(dialogs);

        await page.ClickAsync(BackToList);
        await WaitForAsync(() => dialogs.Count == 1);

        Assert.Equal(["beforeunload"], dialogs);
    }

    [Fact]
    public async Task Without_javascript_a_contribution_can_be_sent()
    {
        var page = await app.NewPageAsync(390, 844, javaScript: false);
        await page.GotoAsync(app.ContributionPath);

        await page.FillAsync("[name='Input.Title']", "Sent without JavaScript");
        await page.SetInputFilesAsync("#Files", Path.Combine("Fixtures", "photo.jpg"));
        await page.CheckAsync("[name='Input.Rights'] >> nth=0");
        await page.FillAsync("[name='Input.Name']", "Ada Contributor");
        await page.CheckAsync("[name='Input.Consent']");
        await page.ClickAsync("main form button[type=submit]");

        await page.WaitForURLAsync("**/thanks");
        Assert.Contains("Thank you", await page.InnerTextAsync("main"));
    }

    [Fact]
    public async Task Without_javascript_a_mistake_in_a_contribution_is_explained()
    {
        var page = await app.NewPageAsync(390, 844, javaScript: false);
        await page.GotoAsync(app.ContributionPath);

        await page.FillAsync("[name='Input.Title']", "With a date nobody can read");
        await page.FillAsync("[name='Input.Date']", "the winter when I was small");
        await page.CheckAsync("[name='Input.Rights'] >> nth=0");
        await page.FillAsync("[name='Input.Name']", "Ada Contributor");
        await page.CheckAsync("[name='Input.Consent']");
        await page.ClickAsync("main form button[type=submit]");

        Assert.Contains("This date could not be understood", await page.InnerTextAsync("main"));
        Assert.Equal("With a date nobody can read", await page.InputValueAsync("[name='Input.Title']"));
    }

    [Fact]
    public async Task Without_javascript_a_visitor_can_search_and_open_an_item()
    {
        var page = await app.NewPageAsync(javaScript: false);
        await page.GotoAsync("/");

        await page.FillAsync("main input[type=search]", "flood");
        await page.ClickAsync("main form[role=search] button[type=submit]");
        await page.ClickAsync("main a:text('Flood in Mill Street')");

        Assert.Contains("Flood in Mill Street", await page.InnerTextAsync("h1"));
        Assert.True(await page.Locator("main img").CountAsync() > 0);
    }

    [Fact]
    public async Task Without_javascript_a_curator_can_describe_and_publish_an_item()
    {
        var page = await app.LogInAsync(javaScript: false);

        await page.GotoAsync("/Admin/Items/Edit");
        await page.FillAsync("[name='Input.Title']", "Published without JavaScript");
        await page.SelectOptionAsync("[name='Input.CollectionId']", new SelectOptionValue { Index = 1 });
        await page.ClickAsync(Button("Save and publish"));

        Assert.Contains("The item is published", await page.InnerTextAsync("main"));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(50);
    }
}
