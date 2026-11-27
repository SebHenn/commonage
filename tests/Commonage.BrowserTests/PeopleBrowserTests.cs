using Microsoft.Playwright;

namespace Commonage.BrowserTests;

/// <summary>The people on an item's page: suggestions while typing, and the plain step that works without them.</summary>
[Collection(nameof(BrowserCollection))]
public class PeopleBrowserTests(BrowserFixture app)
{
    [Fact]
    public async Task Typing_a_name_suggests_people_who_exist_and_suggests_nothing_else()
    {
        var page = await app.LogInAsync();
        await page.GotoAsync("/Admin/Items/Edit/1");

        await page.FillAsync("#person-name", "Karl");
        Assert.Contains("Karl Becker", await OptionsAsync(page, expectSome: true));

        await page.FillAsync("#person-name", "Zzzz nobody");
        Assert.Empty(await OptionsAsync(page, expectSome: false));
    }

    private static async Task<string[]> OptionsAsync(IPage page, bool expectSome)
    {
        string[] options = [];
        for (var i = 0; i < 40; i++)
        {
            options = await page.EvaluateAsync<string[]>("() => [...document.querySelectorAll('#person-suggestions option')].map(o => o.value)");
            if ((options.Length > 0) == expectSome)
                break;
            await Task.Delay(100);
        }

        return options;
    }

    [Fact]
    public async Task A_person_whose_items_are_not_public_is_not_in_the_pages_visitors_get_and_is_under_admin()
    {
        var page = await app.LogInAsync();
        await page.GotoAsync("/Admin/People");
        Assert.Contains("Hans Lindner", await page.TextContentAsync("main"));

        var visitor = await app.NewPageAsync();
        foreach (var path in new[] { "/", "/search?q=Lindner", "/items/1", "/items/2", "/feed.xml", "/sitemap.xml" })
        {
            var response = await visitor.APIRequest.GetAsync(app.BaseUrl + path);
            Assert.DoesNotContain("Hans Lindner", await response.TextAsync());
        }
    }

    [Fact]
    public async Task Visitors_without_javascript_go_from_the_item_to_the_person_and_back_to_the_items()
    {
        var page = await app.NewPageAsync(390, 844, javaScript: false);
        await page.GotoAsync("/people");
        await page.Locator("main a[href^='/people/']").First.ClickAsync();

        Assert.Contains("/people/", page.Url);
        var name = (await page.Locator("h1").TextContentAsync())?.Trim();
        Assert.False(string.IsNullOrEmpty(name));

        await page.Locator("main a[href^='/items/']").First.ClickAsync();
        Assert.Contains("/items/", page.Url);
        Assert.Contains(name!, await page.TextContentAsync("main"));
    }
}
