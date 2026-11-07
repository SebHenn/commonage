using Microsoft.Playwright;

namespace Commonage.BrowserTests;

/// <summary>Citing and sharing an item, and offering reuse on the contribution form.</summary>
[Collection(nameof(BrowserCollection))]
public class SharingBrowserTests(BrowserFixture app)
{
    private const string Item = "/items/3";
    private const string Share = "button[data-share]";
    private const string Copy = "button[data-copy-from]";

    [Fact]
    public async Task A_citation_can_be_copied_in_one_go()
    {
        var page = await app.NewPageAsync(390, 844);
        await page.Context.GrantPermissionsAsync(["clipboard-read", "clipboard-write"], new BrowserContextGrantPermissionsOptions { Origin = app.BaseUrl });
        await page.GotoAsync(Item);

        await page.ClickAsync("summary:text-is('Cite this item')");
        await page.ClickAsync(Copy);
        await page.WaitForSelectorAsync("#copy-status:text-is('Copied')");

        var copied = await page.EvaluateAsync<string>("navigator.clipboard.readText()");
        Assert.Contains("Krause's bakery", copied);
        Assert.Contains("1950s", copied);
        Assert.Contains("Lindenau History Group", copied);
        Assert.Contains($"{app.BaseUrl}{Item}", copied);
        Assert.Equal((await page.InnerTextAsync("#citation")).Trim(), copied.Trim());
        await AccessibilityTests.AssertNoViolationsAsync(page, "a citation");
    }

    [Fact]
    public async Task Where_a_device_cannot_share_the_link_is_copied_and_where_it_can_it_opens_its_own_list()
    {
        var page = await app.NewPageAsync();
        await page.Context.GrantPermissionsAsync(["clipboard-read", "clipboard-write"], new BrowserContextGrantPermissionsOptions { Origin = app.BaseUrl });
        await page.AddInitScriptAsync("delete Navigator.prototype.share; delete Navigator.prototype.canShare;");
        await page.GotoAsync(Item);

        await page.ClickAsync(Share);
        await page.WaitForSelectorAsync("#share-status:text-is('Link copied')");
        Assert.Equal($"{app.BaseUrl}{Item}", await page.EvaluateAsync<string>("navigator.clipboard.readText()"));

        var phone = await app.NewPageAsync(390, 844);
        await phone.AddInitScriptAsync("""
            Navigator.prototype.canShare = () => true;
            Navigator.prototype.share = shared => { window.sharedWith = shared; return Promise.resolve(); };
            """);
        await phone.GotoAsync(Item);

        await phone.ClickAsync(Share);

        await phone.WaitForFunctionAsync("() => window.sharedWith");
        Assert.Equal($"{app.BaseUrl}{Item}", await phone.EvaluateAsync<string>("window.sharedWith.url"));
        Assert.Equal("Krause's bakery", await phone.EvaluateAsync<string>("window.sharedWith.title"));
        Assert.Equal("", await phone.InnerTextAsync("#share-status"));
    }

    [Fact]
    public async Task Someone_who_changes_their_mind_about_sharing_is_not_told_off()
    {
        var page = await app.NewPageAsync(390, 844);
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        await page.AddInitScriptAsync("""
            Navigator.prototype.canShare = () => true;
            Navigator.prototype.share = () => { window.asked = true; return Promise.reject(new DOMException('No', 'AbortError')); };
            """);
        await page.GotoAsync(Item);

        await page.ClickAsync(Share);
        await page.WaitForFunctionAsync("() => window.asked");
        await page.WaitForTimeoutAsync(200);

        Assert.Empty(errors);
        Assert.Equal("", await page.InnerTextAsync("#share-status"));
    }

    [Fact]
    public async Task Without_javascript_the_citation_is_there_to_read_and_nothing_pretends_to_work()
    {
        var page = await app.NewPageAsync(390, 844, javaScript: false);
        await page.GotoAsync(Item);

        Assert.False(await page.Locator(Share).IsVisibleAsync());
        await page.ClickAsync("summary:text-is('Cite this item')");
        Assert.Contains("Krause's bakery", await page.InnerTextAsync("#citation"));
        Assert.False(await page.Locator(Copy).IsVisibleAsync());
        Assert.Contains("May I reuse this?", await page.InnerTextAsync("main"));
    }

    [Fact]
    public async Task The_rights_are_on_a_phone_page_without_pushing_it_sideways()
    {
        var page = await app.NewPageAsync(320, 640);
        await page.GotoAsync(Item);

        var overflow = await page.EvaluateAsync<int>("document.documentElement.scrollWidth - document.documentElement.clientWidth");

        Assert.True(overflow <= 0, $"Scrolls sideways by {overflow} px.");
        Assert.Contains("Free to use with credit (CC BY)", await page.InnerTextAsync(".item-rights"));
        Assert.Contains("Drawing: demo archive", await page.InnerTextAsync(".item-rights"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reuse_is_only_asked_about_when_it_can_be_theirs_to_offer(bool javaScript)
    {
        var page = await app.NewPageAsync(390, 844, javaScript);
        await page.GotoAsync(app.ContributionPath);
        var field = page.Locator("[name='Input.Reuse']");

        // Without JavaScript nothing can show it later, so it is there from the start.
        Assert.Equal(!javaScript, await field.IsVisibleAsync());

        await page.CheckAsync("[name='Input.Rights'] >> nth=0");
        Assert.True(await field.IsVisibleAsync());

        await page.CheckAsync("[name='Input.Rights'] >> nth=2");
        Assert.Equal(!javaScript, await field.IsVisibleAsync());

        // The form still fits on a phone, and the question has to pass the same checks as the rest.
        await page.CheckAsync("[name='Input.Rights'] >> nth=0");
        if (javaScript)
            await AccessibilityTests.AssertNoViolationsAsync(page, "the question about reuse");
    }
}
