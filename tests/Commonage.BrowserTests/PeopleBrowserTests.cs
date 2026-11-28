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

    [Fact]
    public async Task The_review_list_decides_for_ticked_people_with_a_box_that_ticks_all()
    {
        var ids = await app.AddUnreviewedPeopleAsync("Review one", "Review two", "Review three");
        var page = await app.LogInAsync();
        await page.GotoAsync("/Admin/People?review=1");

        // With JavaScript there is a box for the whole list.
        var all = page.Locator("#tick-all");
        await all.CheckAsync();
        foreach (var id in ids)
            Assert.True(await page.Locator($"#person-{id}").IsCheckedAsync());
        await page.Locator($"#person-{ids[2]}").UncheckAsync();
        Assert.False(await all.IsCheckedAsync());

        // "Only curators" is one click.
        await page.ClickAsync("button[name=choice][value=Hidden]");
        await page.WaitForURLAsync(url => url.Contains("review=1") && !url.Contains("handler"));
        Assert.Equal(("Hidden", true), await app.DisclosureOfAsync(ids[0]));
        Assert.Equal(("Hidden", true), await app.DisclosureOfAsync(ids[1]));
        Assert.Equal(("Hidden", false), await app.DisclosureOfAsync(ids[2]));
        Assert.DoesNotContain("Review one", await page.TextContentAsync("main"));
        Assert.Contains("Review three", await page.TextContentAsync("main"));

        // "Has died" shows people to visitors, so it asks first, and nothing changes until it is answered.
        await page.Locator($"#person-{ids[2]}").CheckAsync();
        await page.ClickAsync("button[name=choice][value=Deceased]");
        await page.GetByRole(AriaRole.Button, new() { Name = "Yes, show them" }).WaitForAsync();
        Assert.Equal(("Hidden", false), await app.DisclosureOfAsync(ids[2]));
        await page.GetByRole(AriaRole.Button, new() { Name = "Yes, show them" }).ClickAsync();
        await page.WaitForURLAsync(url => url.Contains("review=1") && !url.Contains("handler"));
        Assert.Equal(("Deceased", true), await app.DisclosureOfAsync(ids[2]));
    }

    [Fact]
    public async Task The_review_list_works_without_javascript_one_person_at_a_time()
    {
        var ids = await app.AddUnreviewedPeopleAsync("Plain one", "Plain two");
        var page = await app.NewPageAsync(390, 844, javaScript: false);
        await page.GotoAsync("/Account/Login");
        await page.FillAsync("[name='Input.Email']", BrowserFixture.AdminEmail);
        await page.FillAsync("[name='Input.Password']", BrowserFixture.AdminPassword);
        await page.ClickAsync("main form button[type=submit]");
        await page.GotoAsync("/Admin/People?review=1");

        // The box for all people needs JavaScript and is not there without it.
        Assert.False(await page.Locator("#tick-all").IsVisibleAsync());
        await page.Locator($"#person-{ids[0]}").CheckAsync();
        await page.ClickAsync("button[name=choice][value=Deceased]");
        await page.GetByRole(AriaRole.Button, new() { Name = "Yes, show them" }).ClickAsync();

        Assert.Equal(("Deceased", true), await app.DisclosureOfAsync(ids[0]));
        Assert.Equal(("Hidden", false), await app.DisclosureOfAsync(ids[1]));
        Assert.DoesNotContain("Plain one", await page.TextContentAsync("main"));
        Assert.Contains("Plain two", await page.TextContentAsync("main"));
    }
}
