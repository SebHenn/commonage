using System.Net;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Commonage.Tests;

[Collection(nameof(AppCollection))]
public class MediaTests(CommonageAppFactory app)
{
    // Synthetic 320x240 image whose EXIF data contains made-up GPS coordinates.
    private static readonly byte[] PhotoWithGps = File.ReadAllBytes(Path.Combine("Fixtures", "photo-with-gps.jpg"));

    [Fact]
    public async Task Photos_of_unpublished_items_are_not_served_to_visitors()
    {
        using var curator = app.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);
        var item = await CreateItemAsync(ItemStatus.Draft);
        var mediaFileId = await UploadAndWaitForPreviewAsync(curator, item.Id);
        using var visitor = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var previewWhileDraft = await visitor.GetAsync($"/media/{mediaFileId}/preview");
        var original = await visitor.GetAsync($"/admin/media/{mediaFileId}/original");
        await PublishAsync(item.Id);
        var previewWhenPublished = await visitor.GetAsync($"/media/{mediaFileId}/preview");

        Assert.Equal(HttpStatusCode.NotFound, previewWhileDraft.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, original.StatusCode);
        Assert.Equal(HttpStatusCode.OK, previewWhenPublished.StatusCode);
    }

    [Fact]
    public async Task Previews_carry_no_metadata_while_the_original_is_kept_byte_for_byte()
    {
        using var curator = app.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);
        var item = await CreateItemAsync(ItemStatus.Published);
        var mediaFileId = await UploadAndWaitForPreviewAsync(curator, item.Id);

        var original = await curator.GetByteArrayAsync($"/admin/media/{mediaFileId}/original");
        var preview = await curator.GetByteArrayAsync($"/media/{mediaFileId}/preview");

        Assert.Equal(PhotoWithGps, original);
        using var originalImage = NetVips.Image.NewFromBuffer(original);
        Assert.Contains("exif-ifd3-GPSLatitude", originalImage.GetFields());
        using var previewImage = NetVips.Image.NewFromBuffer(preview);
        Assert.DoesNotContain(previewImage.GetFields(), field => field.StartsWith("exif", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_file_that_only_pretends_to_be_a_photo_is_rejected()
    {
        using var curator = app.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);
        var item = await CreateItemAsync(ItemStatus.Draft);

        await Browser.UploadAsync(curator, $"/Admin/Items/Edit/{item.Id}", "Upload", "holiday.jpg", "<script>alert(1)</script>"u8.ToArray());

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommonageDbContext>();
        Assert.False(await db.MediaFiles.AnyAsync(f => f.ArchiveItemId == item.Id));
    }

    private async Task<int> UploadAndWaitForPreviewAsync(HttpClient curator, int itemId)
    {
        await Browser.UploadAsync(curator, $"/Admin/Items/Edit/{itemId}", "Upload", "mill.jpg", PhotoWithGps);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CommonageDbContext>();
            var file = await db.MediaFiles.SingleAsync(f => f.ArchiveItemId == itemId);
            if (file.Status == ProcessingStatus.Ready)
                return file.Id;
            Assert.True(file.Status == ProcessingStatus.Pending, $"Preview generation failed: {file.ProcessingError}");
            Assert.True(DateTime.UtcNow < deadline, "Preview generation did not finish in time.");
            await Task.Delay(100);
        }
    }

    private async Task<ArchiveItem> CreateItemAsync(ItemStatus status)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommonageDbContext>();
        var now = DateTime.UtcNow;
        var item = new ArchiveItem
        {
            Title = "The mill",
            Collection = new Collection { Title = "Mill Street", CreatedAt = now },
            CreatedAt = now,
            UpdatedAt = now,
        };
        if (status == ItemStatus.Published)
            item.Publish(now);
        db.ArchiveItems.Add(item);
        await db.SaveChangesAsync();
        return item;
    }

    private async Task PublishAsync(int itemId)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommonageDbContext>();
        var item = await db.ArchiveItems.SingleAsync(i => i.Id == itemId);
        item.Publish(DateTime.UtcNow);
        await db.SaveChangesAsync();
    }
}
