using System.Net;
using System.Text.RegularExpressions;
using Commonage.Web.Domain;
using Commonage.Web.Media;
using Commonage.Web.Operations;
using Commonage.Web.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Commonage.Tests;

/// <summary>Reading a scan at full size, photos in the right order and the right way up, and where to go from an item.</summary>
[Collection(nameof(AppCollection))]
public class ReadableItemTests(CommonageAppFactory app)
{
    /// <summary>Larger than a preview, wider than high, with embedded details that have to stay out of what visitors get.</summary>
    private static byte[] Scan(int width = 3600, int height = 2400)
    {
        using var black = NetVips.Image.Black(width, height, bands: 3);
        using var detailed = black.Mutate(image => image.Set(NetVips.GValue.GStrType, "exif-ifd0-Artist", "Someone private"));
        return detailed.JpegsaveBuffer(keep: NetVips.Enums.ForeignKeep.All);
    }

    private async Task<HttpClient> CuratorAsync()
    {
        var curator = app.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);
        return curator;
    }

    private async Task<(HttpClient Curator, ArchiveItem Item, List<MediaFile> Photos)> ItemWithPhotosAsync(params (string Name, byte[] Content)[] photos)
    {
        var item = await app.CreateContributionAsync(ItemStatus.Published);
        var curator = await CuratorAsync();
        foreach (var (name, content) in photos)
            await Browser.UploadAsync(curator, $"/Admin/Items/Edit/{item.Id}", "Upload", name, content);
        await WaitUntilAsync(() => app.WithDbAsync(db => db.MediaFiles.Where(f => f.ArchiveItemId == item.Id).AllAsync(f => f.Status == ProcessingStatus.Ready && f.Rotation == f.RenderedRotation)));
        return (curator, item, await PhotosOfAsync(item.Id));
    }

    private Task<List<MediaFile>> PhotosOfAsync(int itemId) =>
        app.WithDbAsync(db => db.MediaFiles.AsNoTracking().Where(f => f.ArchiveItemId == itemId).OrderBy(f => f.Position).ThenBy(f => f.Id).ToListAsync());

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 300; i++)
        {
            if (await condition())
                return;
            await Task.Delay(100);
        }
        throw new TimeoutException("It did not happen in time.");
    }

    private static Task<HttpResponseMessage> MoveAsync(HttpClient curator, int itemId, int photo, string direction) =>
        Browser.PostFormAsync(curator, $"/Admin/Items/Edit/{itemId}?handler=MovePhoto&mediaFileId={photo}", new() { ["direction"] = direction }, pageUrl: $"/Admin/Items/Edit/{itemId}");

    private static Task<HttpResponseMessage> TurnAsync(HttpClient curator, int itemId, int photo) =>
        Browser.PostFormAsync(curator, $"/Admin/Items/Edit/{itemId}?handler=TurnPhoto&mediaFileId={photo}", [], pageUrl: $"/Admin/Items/Edit/{itemId}");

    private async Task SetLargePhotosAsync(bool on)
    {
        await app.WithDbAsync(async db =>
        {
            var settings = await db.ArchiveSettings.FirstOrDefaultAsync();
            if (settings is null)
                db.ArchiveSettings.Add(settings = new ArchiveSettings());
            settings.LargePhotos = on;
            return await db.SaveChangesAsync();
        });
        app.Services.GetRequiredService<ArchiveProfile>().Forget();
    }

    private async Task<ArchiveItem> PublishedAsync(Collection collection, string title, string? date = null, DateTime? publishedAt = null)
    {
        return await app.WithDbAsync(async db =>
        {
            db.Attach(collection);
            var item = new ArchiveItem { Title = title, Collection = collection, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            Assert.True(item.TrySetDate(date));
            item.Publish(publishedAt ?? DateTime.UtcNow);
            db.Add(item);
            await db.SaveChangesAsync();
            return item;
        });
    }

    private Task<Collection> NewCollectionAsync() => app.WithDbAsync(async db =>
    {
        var collection = new Collection { Title = $"Collection {Guid.NewGuid():N}", CreatedAt = DateTime.UtcNow };
        db.Add(collection);
        await db.SaveChangesAsync();
        return collection;
    });

    [Fact]
    public async Task A_scan_can_be_opened_large_without_its_embedded_details_while_it_is_public()
    {
        var (curator, item, photos) = await ItemWithPhotosAsync(("letter.jpg", Scan()));
        using (curator)
        {
            using var visitor = app.CreateClient();

            var large = await visitor.GetAsync($"/media/{photos[0].Id}/large");

            Assert.Equal(HttpStatusCode.OK, large.StatusCode);
            using var picture = NetVips.Image.NewFromBuffer(await large.Content.ReadAsByteArrayAsync());
            Assert.Equal((3000, 2000), (picture.Width, picture.Height));
            Assert.DoesNotContain("exif-data", picture.GetFields());
            using var preview = NetVips.Image.NewFromBuffer(await visitor.GetByteArrayAsync($"/media/{photos[0].Id}/preview"));
            Assert.Equal(1600, preview.Width);
            Assert.Contains($"/media/{photos[0].Id}/large", await visitor.GetStringAsync($"/items/{item.Id}"));

            await app.WithDbAsync(async db =>
            {
                (await db.ArchiveItems.SingleAsync(i => i.Id == item.Id)).Unpublish();
                return await db.SaveChangesAsync();
            });

            Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/media/{photos[0].Id}/large")).StatusCode);
        }
    }

    [Fact]
    public async Task A_picture_opened_on_its_own_can_run_nothing_but_the_browser_can_show_it_properly()
    {
        var (curator, _, photos) = await ItemWithPhotosAsync(("letter.jpg", Scan()));
        using (curator)
        {
            using var visitor = app.CreateClient();

            var policy = string.Join(" ", (await visitor.GetAsync($"/media/{photos[0].Id}/large")).Headers.GetValues("Content-Security-Policy"));
            var curators = string.Join(" ", (await curator.GetAsync($"/admin/media/{photos[0].Id}/large")).Headers.GetValues("Content-Security-Policy"));
            var page = string.Join(" ", (await visitor.GetAsync("/")).Headers.GetValues("Content-Security-Policy"));

            Assert.Contains("default-src 'none'", policy);
            Assert.DoesNotContain("script-src", policy);
            Assert.Equal(policy, curators);
            Assert.DoesNotContain("unsafe-inline", page);
        }
    }

    [Fact]
    public async Task A_curator_looking_at_a_draft_opens_the_large_size_from_their_own_address()
    {
        var (curator, item, photos) = await ItemWithPhotosAsync(("letter.jpg", Scan()));
        using (curator)
        {
            await app.WithDbAsync(async db =>
            {
                (await db.ArchiveItems.SingleAsync(i => i.Id == item.Id)).Unpublish();
                return await db.SaveChangesAsync();
            });

            var preview = await curator.GetStringAsync($"/Admin/Items/Preview/{item.Id}");

            Assert.Contains($"/admin/media/{photos[0].Id}/large", preview);
            Assert.Equal(HttpStatusCode.OK, (await curator.GetAsync($"/admin/media/{photos[0].Id}/large")).StatusCode);
        }
    }

    [Fact]
    public async Task An_older_photo_only_gets_the_large_size_added_and_keeps_the_others_as_they_are()
    {
        var (curator, item, photos) = await ItemWithPhotosAsync(("letter.jpg", Scan()));
        using (curator)
        {
            var id = photos[0].Id;
            var storage = app.Services.GetRequiredService<IFileStorage>();
            storage.Delete(photos[0].LargeKey);
            var changedBefore = File.GetLastWriteTimeUtc(PathOf(photos[0].PreviewKey));
            await app.WithDbAsync(async db =>
            {
                var file = await db.MediaFiles.SingleAsync(f => f.Id == id);
                file.DerivativeVersion = 2;
                file.HasLarge = false;
                return await db.SaveChangesAsync();
            });
            using var visitor = app.CreateClient();
            Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/media/{id}/large")).StatusCode);

            app.Services.GetRequiredService<MediaProcessingQueue>().LookThroughOlderFilesAgain();
            await WaitUntilAsync(async () => (await PhotosOfAsync(item.Id))[0].HasLarge);

            Assert.Equal(MediaFile.CurrentDerivativeVersion, (await PhotosOfAsync(item.Id))[0].DerivativeVersion);
            Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync($"/media/{id}/large")).StatusCode);
            Assert.Equal(changedBefore, File.GetLastWriteTimeUtc(PathOf(photos[0].PreviewKey)));
        }

        string PathOf(string key) => Path.Combine(app.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["Storage:RootPath"]!, key);
    }

    [Fact]
    public async Task An_older_photo_in_an_archive_without_large_photos_costs_no_work_at_all()
    {
        await SetLargePhotosAsync(false);
        try
        {
            var (curator, item, photos) = await ItemWithPhotosAsync(("letter.jpg", Scan()));
            using (curator)
            {
                await app.WithDbAsync(async db =>
                {
                    (await db.MediaFiles.SingleAsync(f => f.Id == photos[0].Id)).DerivativeVersion = 2;
                    return await db.SaveChangesAsync();
                });

                app.Services.GetRequiredService<MediaProcessingQueue>().LookThroughOlderFilesAgain();
                await WaitUntilAsync(async () => (await PhotosOfAsync(item.Id))[0].DerivativeVersion == MediaFile.CurrentDerivativeVersion);

                Assert.False((await PhotosOfAsync(item.Id))[0].HasLarge);
            }
        }
        finally
        {
            await SetLargePhotosAsync(true);
        }
    }

    [Fact]
    public async Task A_photo_that_is_not_larger_than_its_preview_offers_nothing_more()
    {
        var (curator, item, photos) = await ItemWithPhotosAsync(("small.jpg", TestData.Photo));
        using (curator)
        {
            using var visitor = app.CreateClient();

            Assert.False(photos[0].HasLarge);
            Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/media/{photos[0].Id}/large")).StatusCode);
            Assert.DoesNotContain("/large", await visitor.GetStringAsync($"/items/{item.Id}"));
            Assert.DoesNotContain("Open at full size", await visitor.GetStringAsync($"/items/{item.Id}"));
        }
    }

    [Fact]
    public async Task An_archive_without_large_photos_keeps_them_from_visitors_and_does_not_make_them()
    {
        await SetLargePhotosAsync(false);
        try
        {
            var (curator, item, photos) = await ItemWithPhotosAsync(("letter.jpg", Scan()));
            using (curator)
            {
                using var visitor = app.CreateClient();

                Assert.False(photos[0].HasLarge);
                Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/media/{photos[0].Id}/large")).StatusCode);
                Assert.DoesNotContain("Open at full size", await visitor.GetStringAsync($"/items/{item.Id}"));
                using var preview = NetVips.Image.NewFromBuffer(await visitor.GetByteArrayAsync($"/media/{photos[0].Id}/preview"));
                Assert.Equal(1600, preview.Width);

                // Switched on in the settings, older photos follow in the background.
                await SetLargePhotosAsync(true);
                app.Services.GetRequiredService<MediaProcessingQueue>().LookThroughOlderFilesAgain();
                await WaitUntilAsync(async () => (await PhotosOfAsync(item.Id))[0].HasLarge);

                Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync($"/media/{photos[0].Id}/large")).StatusCode);

                // Switched off again, it is gone at once for visitors.
                await SetLargePhotosAsync(false);
                Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/media/{photos[0].Id}/large")).StatusCode);
            }
        }
        finally
        {
            await SetLargePhotosAsync(true);
        }
    }

    [Fact]
    public async Task Photos_go_in_the_order_curators_give_them_and_the_first_is_the_cover()
    {
        var (curator, item, photos) = await ItemWithPhotosAsync(("one.jpg", TestData.Photo), ("two.jpg", TestData.Photo), ("three.jpg", TestData.Photo));
        using (curator)
        {
            using var visitor = app.CreateClient();
            var (one, two, three) = (photos[0].Id, photos[1].Id, photos[2].Id);
            Assert.Equal([0, 1, 2], photos.Select(p => p.Position));
            var version = await app.WithDbAsync(db => db.ArchiveItems.Where(i => i.Id == item.Id).Select(i => i.Version).SingleAsync());

            await MoveAsync(curator, item.Id, three, "earlier");
            Assert.Equal([one, three, two], (await PhotosOfAsync(item.Id)).Select(p => p.Id));

            await MoveAsync(curator, item.Id, one, "later");
            Assert.Equal([three, one, two], (await PhotosOfAsync(item.Id)).Select(p => p.Id));

            await MoveAsync(curator, item.Id, two, "first");
            Assert.Equal([two, three, one], (await PhotosOfAsync(item.Id)).Select(p => p.Id));

            // Nothing happens at the ends.
            await MoveAsync(curator, item.Id, two, "earlier");
            await MoveAsync(curator, item.Id, one, "later");
            Assert.Equal([two, three, one], (await PhotosOfAsync(item.Id)).Select(p => p.Id));

            var page = await visitor.GetStringAsync($"/items/{item.Id}");
            Assert.True(page.IndexOf($"/media/{two}/preview", StringComparison.Ordinal) < page.IndexOf($"/media/{three}/preview", StringComparison.Ordinal));
            Assert.True(page.IndexOf($"/media/{three}/preview", StringComparison.Ordinal) < page.IndexOf($"/media/{one}/preview", StringComparison.Ordinal));

            var title = await app.WithDbAsync(db => db.ArchiveItems.Where(i => i.Id == item.Id).Select(i => i.Title).SingleAsync());
            var card = await app.WithDbAsync(db => db.PublishedItems.Where(i => i.Id == item.Id).Select(ItemSummary.FromItem).SingleAsync());
            Assert.Equal(two, card.ThumbnailMediaFileId);

            // An open form can still be saved, and putting photos in order is one thing that happened, not many.
            Assert.Equal(version, await app.WithDbAsync(db => db.ArchiveItems.Where(i => i.Id == item.Id).Select(i => i.Version).SingleAsync()));
            Assert.Equal(1, await app.WithDbAsync(db => db.AuditEntries.CountAsync(e => e.ArchiveItemId == item.Id && e.Action == AuditAction.PhotosArranged)));
            Assert.NotEmpty(title);
        }
    }

    [Fact]
    public async Task A_new_photo_comes_last_also_when_others_were_moved_or_are_in_the_bin()
    {
        var (curator, item, photos) = await ItemWithPhotosAsync(("one.jpg", TestData.Photo), ("two.jpg", TestData.Photo));
        using (curator)
        {
            await MoveAsync(curator, item.Id, photos[1].Id, "first");
            await Browser.PostFormAsync(curator, $"/Admin/Items/Edit/{item.Id}?handler=DeleteMedia&mediaFileId={photos[0].Id}", [], pageUrl: $"/Admin/Items/Edit/{item.Id}");

            await Browser.UploadAsync(curator, $"/Admin/Items/Edit/{item.Id}", "Upload", "three.jpg", TestData.Photo);

            var live = await PhotosOfAsync(item.Id);
            Assert.Equal(["two.jpg", "three.jpg"], live.Select(p => p.OriginalFileName));
            Assert.Equal(2, live[1].Position);
        }
    }

    [Fact]
    public async Task Turning_changes_what_is_shown_everywhere_and_never_the_original()
    {
        var scan = Scan();
        var (curator, item, photos) = await ItemWithPhotosAsync(("sideways.jpg", scan));
        using (curator)
        {
            using var visitor = app.CreateClient();
            var id = photos[0].Id;
            var before = (await visitor.GetAsync($"/media/{id}/preview")).Headers.ETag!;

            var turned = await TurnAsync(curator, item.Id, id);

            Assert.Equal(HttpStatusCode.OK, turned.StatusCode);
            await WaitUntilAsync(async () => !(await PhotosOfAsync(item.Id))[0].IsBeingTurned);
            var file = (await PhotosOfAsync(item.Id))[0];
            Assert.Equal((90, 90), (file.Rotation, file.RenderedRotation));
            Assert.Equal((1067, 1600), (file.PreviewWidth, file.PreviewHeight));
            foreach (var (size, height) in new[] { ("large", 3000), ("preview", 1600), ("card", 800), ("thumbnail", 400) })
            {
                using var picture = NetVips.Image.NewFromBuffer(await visitor.GetByteArrayAsync($"/media/{id}/{size}"));
                Assert.Equal((size, height), (size, picture.Height));
                Assert.True(picture.Width < picture.Height, $"The {size} is not turned.");
            }

            // A browser that has the picture from before gets the new one.
            Assert.NotEqual(before, (await visitor.GetAsync($"/media/{id}/preview")).Headers.ETag);
            Assert.Equal(scan, await curator.GetByteArrayAsync($"/admin/media/{id}/original"));

            for (var turn = 0; turn < 3; turn++)
                await TurnAsync(curator, item.Id, id);
            await WaitUntilAsync(async () => !(await PhotosOfAsync(item.Id))[0].IsBeingTurned);
            using var backAgain = NetVips.Image.NewFromBuffer(await visitor.GetByteArrayAsync($"/media/{id}/preview"));
            Assert.Equal((0, 1600, 1067), ((await PhotosOfAsync(item.Id))[0].Rotation, backAgain.Width, backAgain.Height));
        }
    }

    [Fact]
    public async Task A_turn_that_cannot_be_done_is_given_up_so_nobody_waits_for_it()
    {
        var (curator, item, photos) = await ItemWithPhotosAsync(("scan.jpg", Scan()));
        using (curator)
        {
            var storage = app.Services.GetRequiredService<IFileStorage>();
            storage.Delete(photos[0].OriginalKey);
            using var visitor = app.CreateClient();

            await TurnAsync(curator, item.Id, photos[0].Id);

            await WaitUntilAsync(async () => !(await PhotosOfAsync(item.Id))[0].IsBeingTurned);
            Assert.Equal(0, (await PhotosOfAsync(item.Id))[0].Rotation);
            Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync($"/media/{photos[0].Id}/preview")).StatusCode);
        }
    }

    [Fact]
    public async Task While_a_photo_is_being_turned_a_page_is_told_to_wait_for_it()
    {
        var (curator, item, photos) = await ItemWithPhotosAsync(("scan.jpg", TestData.Photo));
        using (curator)
        {
            await app.WithDbAsync(async db =>
            {
                (await db.MediaFiles.SingleAsync(f => f.Id == photos[0].Id)).Rotation = 90;
                return await db.SaveChangesAsync();
            });

            var status = await curator.GetStringAsync($"/admin/media/status?ids={photos[0].Id}");

            Assert.Contains($"\"waiting\":[{photos[0].Id}]", status);
            Assert.Contains("\"ready\":[]", status);
            Assert.Contains("Turning the photo", await curator.GetStringAsync($"/Admin/Items/Edit/{item.Id}"));

            app.Services.GetRequiredService<MediaProcessingQueue>().Enqueue(photos[0].Id);
            await WaitUntilAsync(async () => !(await PhotosOfAsync(item.Id))[0].IsBeingTurned);
        }
    }

    [Fact]
    public async Task A_caption_is_saved_with_the_item_and_read_under_the_photo_with_its_number()
    {
        var (curator, item, photos) = await ItemWithPhotosAsync(("one.jpg", TestData.Photo), ("two.jpg", TestData.Photo));
        using (curator)
        {
            using var visitor = app.CreateClient();
            var fields = new Dictionary<string, string>
            {
                ["Input.Title"] = item.Title,
                ["Input.CollectionId"] = item.CollectionId.ToString(),
                [$"Input.Captions[{photos[1].Id}]"] = "  The back of the letter  ",
                [$"Input.AltTexts[{photos[1].Id}]"] = "A page with a signature",
            };

            await Browser.PostFormAsync(curator, $"/Admin/Items/Edit/{item.Id}", fields);

            var page = await visitor.GetStringAsync($"/items/{item.Id}");
            Assert.Contains("The back of the letter", page);
            Assert.Contains("Photo 1 of 2", page);
            Assert.Contains("Photo 2 of 2", page);
            Assert.Contains("href=\"#photo-2\"", page);
            Assert.Contains("id=\"photo-2\"", page);
            // Without a description of its own, a photo still says which one it is.
            Assert.Contains($"alt=\"{item.Title}, photo 1 of 2\"", page);
            Assert.Contains("alt=\"A page with a signature\"", page);
            Assert.Equal("The back of the letter", (await PhotosOfAsync(item.Id))[1].Caption);

            fields[$"Input.Captions[{photos[1].Id}]"] = new string('x', MediaFile.MaxCaptionLength + 1);
            var tooLong = await Browser.PostFormAsync(curator, $"/Admin/Items/Edit/{item.Id}", fields);
            Assert.Contains("A caption can be at most", await tooLong.Content.ReadAsStringAsync());
            Assert.Equal("The back of the letter", (await PhotosOfAsync(item.Id))[1].Caption);
        }
    }

    [Fact]
    public async Task A_single_photo_has_no_number_and_no_strip()
    {
        var (curator, item, _) = await ItemWithPhotosAsync(("one.jpg", TestData.Photo));
        using (curator)
        {
            using var visitor = app.CreateClient();

            var page = await visitor.GetStringAsync($"/items/{item.Id}");

            Assert.DoesNotContain("Photo 1 of 1", page);
            Assert.DoesNotContain("photo-strip", page);
            Assert.DoesNotContain("<figcaption", page);
            Assert.Contains($"alt=\"{item.Title}\"", page);
        }
    }

    [Fact]
    public async Task An_item_leads_on_in_the_order_of_its_collection_and_never_to_what_is_not_public()
    {
        var collection = await NewCollectionAsync();
        var start = DateTime.UtcNow.AddDays(-10);
        var oldest = await PublishedAsync(collection, "The oldest in the list", publishedAt: start);
        var middle = await PublishedAsync(collection, "The middle of the list", publishedAt: start.AddDays(1));
        var newest = await PublishedAsync(collection, "The newest in the list", publishedAt: start.AddDays(2));
        var hidden = await PublishedAsync(collection, "Not public any more", publishedAt: start.AddHours(36));
        await app.WithDbAsync(async db =>
        {
            (await db.ArchiveItems.SingleAsync(i => i.Id == hidden.Id)).Unpublish();
            return await db.SaveChangesAsync();
        });
        using var visitor = app.CreateClient();

        var page = await visitor.GetStringAsync($"/items/{middle.Id}");
        var first = await visitor.GetStringAsync($"/items/{newest.Id}");
        var last = await visitor.GetStringAsync($"/items/{oldest.Id}");

        Assert.Contains("Next: The oldest in the list", page);
        Assert.Contains("Previous: The newest in the list", page);
        Assert.DoesNotContain("Not public any more", page);
        Assert.DoesNotContain("Previous:", first);
        Assert.Contains("Next: The middle of the list", first);
        Assert.DoesNotContain("Next:", last);
        Assert.Contains($"More from {collection.Title}", page);
        Assert.Contains($"/collections/{collection.Id}", page);

        var collectionPage = await visitor.GetStringAsync($"/collections/{collection.Id}");
        Assert.True(collectionPage.IndexOf("The newest", StringComparison.Ordinal) < collectionPage.IndexOf("The middle", StringComparison.Ordinal));
        Assert.True(collectionPage.IndexOf("The middle", StringComparison.Ordinal) < collectionPage.IndexOf("The oldest", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_item_on_its_own_in_a_collection_does_not_offer_empty_sections()
    {
        var collection = await NewCollectionAsync();
        var alone = await PublishedAsync(collection, "All alone in here", date: "1066");
        using var visitor = app.CreateClient();

        var page = await visitor.GetStringAsync($"/items/{alone.Id}");

        Assert.DoesNotContain("Next:", page);
        Assert.DoesNotContain("Previous:", page);
        Assert.DoesNotContain("More from", page);
        Assert.DoesNotContain("From the same time", page);
    }

    [Fact]
    public async Task From_the_same_time_finds_what_overlaps_with_the_closest_first()
    {
        var collection = await NewCollectionAsync();
        var elsewhere = await NewCollectionAsync();
        var marker = Guid.NewGuid().ToString("N")[..8];
        var item = await PublishedAsync(collection, $"Summer {marker}", date: "1753");
        await PublishedAsync(elsewhere, $"Long ago and close {marker}", date: "1753");
        await PublishedAsync(elsewhere, $"Long ago and wide {marker}", date: "1700–1800");
        await PublishedAsync(elsewhere, $"Long ago and too late {marker}", date: "1754");
        var draft = await PublishedAsync(elsewhere, $"Long ago and hidden {marker}", date: "1753");
        await app.WithDbAsync(async db =>
        {
            (await db.ArchiveItems.SingleAsync(i => i.Id == draft.Id)).Unpublish();
            return await db.SaveChangesAsync();
        });
        using var visitor = app.CreateClient();

        var page = await visitor.GetStringAsync($"/items/{item.Id}");

        Assert.Contains("From the same time", page);
        Assert.Contains($"Long ago and wide {marker}", page);
        Assert.DoesNotContain($"Long ago and too late {marker}", page);
        Assert.DoesNotContain($"Long ago and hidden {marker}", page);
        Assert.True(page.IndexOf($"Long ago and close {marker}", StringComparison.Ordinal) < page.IndexOf($"Long ago and wide {marker}", StringComparison.Ordinal));
        // The heading leads on to everything from that time, and the date of the item itself does too.
        Assert.Equal(2, Regex.Matches(page, Regex.Escape("/search?from=1753&amp;to=1753")).Count);
    }

    [Fact]
    public async Task A_date_with_an_open_end_links_to_search_but_finds_no_same_time()
    {
        var collection = await NewCollectionAsync();
        var item = await PublishedAsync(collection, "Some time before", date: "before 1900");
        using var visitor = app.CreateClient();

        var page = await visitor.GetStringAsync($"/items/{item.Id}");

        Assert.Contains("/search?to=1899", page);
        Assert.DoesNotContain("From the same time", page);
    }

    [Fact]
    public async Task A_curator_can_mark_what_needs_writing_out_and_visitors_do_not_see_that()
    {
        var item = await app.CreateContributionAsync(ItemStatus.Published);
        using var curator = await CuratorAsync();
        using var visitor = app.CreateClient();
        var title = await app.WithDbAsync(db => db.ArchiveItems.Where(i => i.Id == item.Id).Select(i => i.Title).SingleAsync());

        await Browser.PostFormAsync(curator, $"/Admin/Items/Edit/{item.Id}", new()
        {
            ["Input.Title"] = title,
            ["Input.CollectionId"] = item.CollectionId.ToString(),
            ["Input.NeedsTranscription"] = "true",
        });

        Assert.True(await app.WithDbAsync(db => db.ArchiveItems.Where(i => i.Id == item.Id).Select(i => i.NeedsTranscription).SingleAsync()));
        Assert.Contains(title, await curator.GetStringAsync($"/Admin?needs={ItemNeed.Transcription}"));
        Assert.DoesNotContain("transcription", await visitor.GetStringAsync($"/items/{item.Id}"), StringComparison.OrdinalIgnoreCase);

        await Browser.PostFormAsync(curator, $"/Admin/Items/Edit/{item.Id}", new()
        {
            ["Input.Title"] = title,
            ["Input.CollectionId"] = item.CollectionId.ToString(),
        });

        Assert.DoesNotContain(title, await curator.GetStringAsync($"/Admin?needs={ItemNeed.Transcription}"));
    }
}
