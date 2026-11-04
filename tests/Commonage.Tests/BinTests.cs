using System.Net;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Commonage.Tests;

[Collection(nameof(AppCollection))]
public class BinTests(CommonageAppFactory app)
{
    private async Task<(ArchiveItem Item, MediaFile File, HttpClient Curator)> PublishedItemWithPhotoAsync()
    {
        var item = await app.CreateContributionAsync(ItemStatus.Published);
        var curator = app.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);
        await Browser.UploadAsync(curator, $"/Admin/Items/Edit/{item.Id}", "Upload", "flood.jpg", TestData.Photo);
        var file = await app.WithDbAsync(db => db.MediaFiles.SingleAsync(f => f.ArchiveItemId == item.Id));
        return (item, file, curator);
    }

    private static string ItemPage(ArchiveItem item) => $"/Admin/Items/Edit/{item.Id}";

    private Task<ArchiveItem?> FindEvenInBinAsync(int id) =>
        app.WithDbAsync(db => db.ArchiveItems.IgnoreQueryFilters().FirstOrDefaultAsync(i => i.Id == id));

    [Fact]
    public async Task An_item_in_the_bin_is_gone_from_everywhere_until_it_is_restored()
    {
        var (item, _, curator) = await PublishedItemWithPhotoAsync();
        using (curator)
        using (var visitor = app.CreateClient())
        {
            await Browser.PostFormAsync(curator, $"{ItemPage(item)}?handler=Delete", new(), ItemPage(item));

            Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/items/{item.Id}")).StatusCode);
            Assert.DoesNotContain(item.Title, await visitor.GetStringAsync($"/collections/{item.CollectionId}"));
            Assert.DoesNotContain(item.Title, await curator.GetStringAsync("/Admin"));
            Assert.Contains(item.Title, await curator.GetStringAsync("/Admin/Bin"));
            var opened = await curator.GetAsync(ItemPage(item));
            Assert.EndsWith("/Admin/Bin", opened.RequestMessage!.RequestUri!.AbsolutePath);

            await Browser.PostFormAsync(curator, $"/Admin/Bin?handler=RestoreItem&id={item.Id}", new(), "/Admin/Bin");

            Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync($"/items/{item.Id}")).StatusCode);
            Assert.DoesNotContain(item.Title, await curator.GetStringAsync("/Admin/Bin"));
        }
    }

    [Fact]
    public async Task The_photos_of_an_item_in_the_bin_are_not_public_but_their_files_stay()
    {
        var (item, file, curator) = await PublishedItemWithPhotoAsync();
        using (curator)
        using (var visitor = app.CreateClient())
        {
            await Browser.PostFormAsync(curator, $"{ItemPage(item)}?handler=Delete", new(), ItemPage(item));

            Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/media/{file.Id}/preview")).StatusCode);
            using var original = await curator.GetAsync($"/admin/media/{file.Id}/original");
            Assert.Equal(TestData.Photo, await original.Content.ReadAsByteArrayAsync());
        }
    }

    [Fact]
    public async Task Deleting_from_the_bin_removes_the_item_and_its_files_for_good()
    {
        var (item, file, curator) = await PublishedItemWithPhotoAsync();
        using (curator)
        {
            await Browser.PostFormAsync(curator, $"{ItemPage(item)}?handler=Delete", new(), ItemPage(item));
            await Browser.PostFormAsync(curator, $"/Admin/Bin?handler=DeleteItem&id={item.Id}", new(), "/Admin/Bin");
        }

        Assert.Null(await FindEvenInBinAsync(item.Id));
        Assert.False(await app.WithDbAsync(db => db.Contributions.IgnoreQueryFilters().AnyAsync(c => c.ArchiveItemId == item.Id)));
        var storage = app.Services.GetRequiredService<IFileStorage>();
        Assert.Throws<FileNotFoundException>(() => storage.OpenRead(file.OriginalKey));
    }

    [Fact]
    public async Task A_photo_in_the_bin_leaves_the_public_page_and_can_come_back()
    {
        var (item, file, curator) = await PublishedItemWithPhotoAsync();
        using (curator)
        {
            await Browser.PostFormAsync(curator, $"{ItemPage(item)}?handler=DeleteMedia&mediaFileId={file.Id}", new(), ItemPage(item));

            Assert.Null(await app.WithDbAsync(db => db.MediaFiles.FirstOrDefaultAsync(f => f.Id == file.Id)));
            Assert.Contains(file.OriginalFileName, await curator.GetStringAsync("/Admin/Bin"));
            Assert.DoesNotContain("flood.jpg", await curator.GetStringAsync(ItemPage(item)));

            await Browser.PostFormAsync(curator, $"/Admin/Bin?handler=RestorePhoto&id={file.Id}", new(), "/Admin/Bin");

            Assert.NotNull(await app.WithDbAsync(db => db.MediaFiles.FirstOrDefaultAsync(f => f.Id == file.Id)));
            Assert.Contains("flood.jpg", await curator.GetStringAsync(ItemPage(item)));
        }
    }

    [Fact]
    public async Task Only_what_has_been_in_the_bin_long_enough_is_deleted_by_itself()
    {
        var (expired, expiredFile, curator) = await PublishedItemWithPhotoAsync();
        curator.Dispose();
        var recent = await app.CreateContributionAsync(ItemStatus.Draft);
        await app.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            (await db.ArchiveItems.SingleAsync(i => i.Id == expired.Id)).MoveToBin(now.AddDays(-ArchiveBin.RetentionDays - 1));
            (await db.ArchiveItems.SingleAsync(i => i.Id == recent.Id)).MoveToBin(now.AddDays(-ArchiveBin.RetentionDays + 1));
            return await db.SaveChangesAsync();
        });

        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ArchiveBin>().PurgeExpiredAsync();

        Assert.Null(await FindEvenInBinAsync(expired.Id));
        Assert.NotNull(await FindEvenInBinAsync(recent.Id));
        var storage = app.Services.GetRequiredService<IFileStorage>();
        Assert.Throws<FileNotFoundException>(() => storage.OpenRead(expiredFile.OriginalKey));
        var entry = await app.WithDbAsync(db => db.AuditEntries.SingleAsync(e => e.Subject == expired.Title && e.Action == AuditAction.ItemPurged));
        Assert.Null(entry.UserName);

        using var admin = app.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        var log = await admin.GetStringAsync("/Admin/Activity?p=1");
        Assert.Contains("Automatic", log);
    }

    [Fact]
    public async Task A_personal_data_search_also_finds_items_in_the_bin()
    {
        var name = TestData.Unique("Binned Person");
        var item = await app.CreateContributionAsync(ItemStatus.Draft, name: name);
        using var curator = app.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);
        await Browser.PostFormAsync(curator, $"{ItemPage(item)}?handler=Delete", new(), ItemPage(item));

        var found = await curator.GetStringAsync($"/Admin/Contributors?q={Uri.EscapeDataString(name)}");

        Assert.Contains(item.Title, found);
        Assert.Contains("In the bin", found);
    }

    [Fact]
    public async Task A_personal_data_search_finds_photographers_and_authors()
    {
        var item = await app.CreateContributionAsync(ItemStatus.Draft);
        var holder = TestData.Unique("Photographer");
        await app.WithDbAsync(async db =>
        {
            (await db.Contributions.SingleAsync(c => c.ArchiveItemId == item.Id)).RightsHolder = holder;
            return await db.SaveChangesAsync();
        });
        using var curator = app.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);

        var found = await curator.GetStringAsync($"/Admin/Contributors?q={Uri.EscapeDataString(holder)}");

        Assert.Contains(item.Title, found);
    }

    [Fact]
    public async Task A_visitor_cannot_open_the_bin()
    {
        using var visitor = app.CreateClient();

        var response = await visitor.GetAsync("/Admin/Bin");

        Assert.Contains("/Account/Login", response.RequestMessage!.RequestUri!.AbsolutePath);
    }
}
