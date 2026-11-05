using Microsoft.Playwright;

namespace Commonage.BrowserTests;

/// <summary>What only a browser can prove about the catalogue fields.</summary>
[Collection(nameof(BrowserCollection))]
public class CatalogueFormTests(BrowserFixture app)
{
    private const string AltText = "textarea[name^='Input.AltTexts']";

    [Fact]
    public async Task A_photo_description_outside_the_form_is_saved_with_it_and_warned_about_like_the_rest()
    {
        var page = await app.LogInAsync();
        await page.GotoAsync("/Admin/Items/Edit/2");
        await page.WaitForSelectorAsync(AltText);
        var dialogs = new List<string>();
        page.Dialog += async (_, dialog) =>
        {
            dialogs.Add(dialog.Type);
            await dialog.DismissAsync();
        };

        await page.FillAsync(AltText, "A crowd on the new bridge, seen from the bank");
        await page.ClickAsync(".breadcrumb a");
        for (var i = 0; i < 100 && dialogs.Count == 0; i++)
            await Task.Delay(50);
        Assert.Equal(["beforeunload"], dialogs);

        dialogs.Clear();
        await page.ClickAsync("#item-form button[type=submit] >> nth=0");
        await page.WaitForSelectorAsync(".alert-success");

        Assert.Empty(dialogs);
        Assert.Equal("A crowd on the new bridge, seen from the bank", await page.InputValueAsync(AltText));

        var visitor = await app.NewPageAsync();
        await visitor.GotoAsync("/items/2");
        Assert.Equal("A crowd on the new bridge, seen from the bank", await visitor.GetAttributeAsync(".media-preview", "alt"));
    }

    [Fact]
    public async Task Rights_and_catalogue_sections_open_when_they_hold_something_and_stay_out_of_the_way_when_empty()
    {
        var page = await app.LogInAsync();

        await page.GotoAsync("/Admin/Items/Edit");
        Assert.False(await page.Locator("#catalogue-details[open]").IsVisibleAsync());

        await page.GotoAsync("/Admin/Items/Edit/1");
        Assert.True(await page.Locator("#catalogue-details[open]").IsVisibleAsync());
    }

    [Fact]
    public async Task A_contribution_suggests_a_statement_and_says_so()
    {
        var page = await app.LogInAsync();
        await page.GotoAsync("/Admin?status=Submitted");
        await page.ClickAsync("main table a");

        Assert.Equal("http://rightsstatements.org/vocab/InC/1.0/", await page.InputValueAsync("[name='Input.RightsUri']"));
        Assert.Contains("Suggested from what the contributor answered", await page.InnerTextAsync("#rights-help"));
    }
}
