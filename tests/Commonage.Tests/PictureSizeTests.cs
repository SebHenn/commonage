using System.Net;
using System.Net.Http.Headers;
using Commonage.Web.Domain;
using Commonage.Web.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Commonage.Tests;

[Collection(nameof(AppCollection))]
public class PictureSizeTests(CommonageAppFactory app)
{
    /// <summary>A published item with a photo whose previews are finished.</summary>
    private async Task<MediaFile> PublishedPhotoAsync()
    {
        var item = await app.CreateContributionAsync(ItemStatus.Published);
        using var curator = app.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);
        await Browser.UploadAsync(curator, $"/Admin/Items/Edit/{item.Id}", "Upload", "flood.jpg", TestData.Photo);
        for (var i = 0; i < 100; i++)
        {
            var file = await app.WithDbAsync(db => db.MediaFiles.AsNoTracking().SingleAsync(f => f.ArchiveItemId == item.Id));
            if (file.Status == ProcessingStatus.Ready)
                return file;
            await Task.Delay(100);
        }
        throw new TimeoutException("The previews were not made.");
    }

    [Fact]
    public async Task A_new_photo_gets_a_card_size_without_any_metadata()
    {
        var file = await PublishedPhotoAsync();
        using var visitor = app.CreateClient();

        var card = await visitor.GetByteArrayAsync($"/media/{file.Id}/card");

        Assert.Equal(MediaFile.CurrentDerivativeVersion, file.DerivativeVersion);
        using var image = NetVips.Image.NewFromBuffer(card);
        Assert.True(Math.Max(image.Width, image.Height) <= 800);
        Assert.DoesNotContain("exif-data", image.GetFields());
    }

    [Fact]
    public async Task An_unchanged_picture_is_not_sent_again_but_unpublishing_still_ends_access()
    {
        var file = await PublishedPhotoAsync();
        using var visitor = app.CreateClient();
        var first = await visitor.GetAsync($"/media/{file.Id}/thumbnail");
        var etag = first.Headers.ETag!;

        using var again = new HttpRequestMessage(HttpMethod.Get, $"/media/{file.Id}/thumbnail");
        again.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag.Tag));
        var second = await visitor.SendAsync(again);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Contains("max-age=600", first.Headers.CacheControl!.ToString());

        await app.WithDbAsync(async db =>
        {
            (await db.ArchiveItems.SingleAsync(i => i.Id == file.ArchiveItemId)).Unpublish();
            return await db.SaveChangesAsync();
        });
        using var afterUnpublishing = new HttpRequestMessage(HttpMethod.Get, $"/media/{file.Id}/thumbnail");
        afterUnpublishing.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag.Tag));

        Assert.Equal(HttpStatusCode.NotFound, (await visitor.SendAsync(afterUnpublishing)).StatusCode);
    }

    [Fact]
    public async Task An_older_photo_keeps_being_served_and_gets_the_new_size_in_the_background()
    {
        var file = await PublishedPhotoAsync();
        var storage = app.Services.GetRequiredService<IFileStorage>();
        storage.Delete(file.CardKey);
        await app.WithDbAsync(async db =>
        {
            (await db.MediaFiles.SingleAsync(f => f.Id == file.Id)).DerivativeVersion = 1;
            return await db.SaveChangesAsync();
        });
        using var visitor = app.CreateClient();

        // Without the new size yet, the smaller one stands in, and the photo never goes away.
        var thumbnail = await visitor.GetByteArrayAsync($"/media/{file.Id}/thumbnail");
        var standIn = await visitor.GetByteArrayAsync($"/media/{file.Id}/card");
        Assert.Equal(thumbnail, standIn);

        // The app looks for older photos when it starts.
        await using var restarted = app.WithWebHostBuilder(_ => { });
        restarted.CreateClient().Dispose();
        var version = 1;
        for (var i = 0; i < 100 && version == 1; i++)
        {
            await Task.Delay(100);
            version = await app.WithDbAsync(db => db.MediaFiles.Where(f => f.Id == file.Id).Select(f => f.DerivativeVersion).SingleAsync());
            Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync($"/media/{file.Id}/preview")).StatusCode);
        }

        Assert.Equal(MediaFile.CurrentDerivativeVersion, version);
        await using var card = storage.OpenRead(file.CardKey);
        Assert.True(card.Length > 0);
    }

    [Fact]
    public async Task The_start_page_shows_recent_photos_and_only_collections_with_something_public()
    {
        var published = await PublishedPhotoAsync();
        var title = await app.WithDbAsync(db => db.ArchiveItems.Where(i => i.Id == published.ArchiveItemId).Select(i => i.Title).SingleAsync());
        var empty = await app.CreateContributionAsync(ItemStatus.Submitted);
        var emptyCollection = await app.WithDbAsync(db => db.Collections.Where(c => c.Id == empty.CollectionId).Select(c => c.Title).SingleAsync());
        using var visitor = app.CreateClient();

        var home = await visitor.GetStringAsync("/");

        Assert.Contains(title, home);
        Assert.Contains($"/media/{published.Id}/card 800w", home);
        Assert.DoesNotContain(emptyCollection, home);
        Assert.DoesNotContain(emptyCollection, await visitor.GetStringAsync("/collections"));
    }

    [Fact]
    public async Task Search_shows_each_filter_to_remove_and_offers_a_way_on_when_nothing_is_found()
    {
        var published = await PublishedPhotoAsync();
        using var visitor = app.CreateClient();
        await app.WithDbAsync(async db =>
        {
            var item = await db.ArchiveItems.Include(i => i.Tags).SingleAsync(i => i.Id == published.ArchiveItemId);
            var name = "Popular " + Guid.NewGuid().ToString("N")[..12];
            item.SetTags([new Tag { Name = name, NormalizedName = Tag.Normalize(name) }]);
            return await db.SaveChangesAsync();
        });

        var page = await visitor.GetStringAsync("/search?from=1950&to=1960&q=zzzznothing");

        Assert.Contains("From year: 1950", page);
        Assert.Contains("To year: 1960", page);
        Assert.Contains("Filters active: 2", page);
        Assert.Contains("Or start from one of these", page);
        Assert.Contains("Popular ", page);
    }

    [Theory]
    [InlineData("en", "English")]
    [InlineData("de", "Deutsch")]
    public void A_language_is_named_in_itself(string code, string name)
    {
        Assert.Equal(name, Commonage.Web.SupportedLanguages.NameOf(code));
    }
}
