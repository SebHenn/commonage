using System.Text;
using Microsoft.Playwright;

namespace Commonage.BrowserTests;

/// <summary>The dry run is a state the page only reaches by using it, so the page-load checks never see it.</summary>
[Collection(nameof(BrowserCollection))]
public class ImportBrowserTests(BrowserFixture app)
{
    private const string Sheet =
        "title,date,files\n" +
        "Barn raising,1953,\n" +
        "Bad date,not a date at all,\n" +
        "\"עבודה בשדה — a very long title that has to wrap on a phone without pushing the table out of the screen, " +
        "even when nobody ever breaks it into words: Donaudampfschifffahrtsgesellschaftskapitänsmützenabzeichen\",1950,\n" +
        "Lost file,1950,missing-scan.jpg\n";

    [Theory]
    [InlineData(390, true)]
    [InlineData(1280, true)]
    [InlineData(390, false)]
    public async Task The_dry_run_lists_the_problems_and_offers_the_import(int width, bool javaScript)
    {
        var page = await app.LogInAsync(width, javaScript: javaScript);
        await page.GotoAsync("/Admin/Items/Import");
        await page.SetInputFilesAsync("#sheet", new FilePayload { Name = "rows.csv", MimeType = "text/csv", Buffer = Encoding.UTF8.GetBytes(Sheet) });
        await page.SelectOptionAsync("#CollectionId", new SelectOptionValue { Index = 1 });

        await page.ClickAsync("main form button[type=submit]");
        await page.WaitForSelectorAsync("#dry-run-heading");

        Assert.Contains("not a date at all", await page.TextContentAsync("main"));
        Assert.Contains("missing-scan.jpg", await page.TextContentAsync("main"));
        Assert.True(await page.GetByRole(AriaRole.Button, new() { NameRegex = new System.Text.RegularExpressions.Regex("^Import the") }).IsVisibleAsync());
        // axe is itself a script, so without JavaScript only the layout can be checked.
        if (javaScript)
            await AccessibilityTests.AssertNoViolationsAsync(page, $"the dry run at {width} px");
        else
        {
            var table = await page.Locator("main table").First.BoundingBoxAsync();
            Assert.True(table!.X + table.Width <= width + 1, "The problems table is wider than the screen without JavaScript.");
        }

        if (Environment.GetEnvironmentVariable("COMMONAGE_SCREENSHOTS") is { Length: > 0 } folder)
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(folder, $"import-dry-run-{width}-{(javaScript ? "js" : "nojs")}.png"), FullPage = true });
    }
}
