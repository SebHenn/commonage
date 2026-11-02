using System.Net;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Commonage.Tests;

[Collection(nameof(AppCollection))]
public class ErrorPageTests(CommonageAppFactory app)
{
    [Fact]
    public async Task Missing_pages_say_so_in_the_visitors_language_and_keep_the_status_code()
    {
        using var visitor = app.CreateClient();
        var english = await visitor.GetAsync("/items/999999");
        using var german = new HttpRequestMessage(HttpMethod.Get, "/no/such/page");
        german.Headers.Add("Cookie", ".AspNetCore.Culture=c%3Dde%7Cuic%3Dde");
        var germanResponse = await visitor.SendAsync(german);

        Assert.Equal(HttpStatusCode.NotFound, english.StatusCode);
        Assert.Contains("Page not found", await english.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, germanResponse.StatusCode);
        Assert.Contains("Seite nicht gefunden", await germanResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Images_with_more_pixels_than_the_limit_get_no_preview()
    {
        await using var strictServer = app.WithWebHostBuilder(b => b.UseSetting("Uploads:MaxPreviewMegapixels", "0"));
        using var curator = strictServer.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);
        var item = await app.CreateContributionAsync(ItemStatus.Submitted);

        await Browser.UploadAsync(curator, $"/Admin/Items/Edit/{item.Id}", "Upload", "huge.jpg", TestData.Photo);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        MediaFile file;
        while (true)
        {
            file = await app.WithDbAsync(db => db.MediaFiles.AsNoTracking().SingleAsync(f => f.ArchiveItemId == item.Id));
            if (file.Status != ProcessingStatus.Pending || DateTime.UtcNow > deadline)
                break;
            await Task.Delay(100);
        }
        Assert.Equal(ProcessingStatus.Failed, file.Status);
        Assert.Contains("megapixels", file.ProcessingError);
    }
}
