using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Export;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

namespace Commonage.Tests;

[Collection(nameof(AppCollection))]
public class ExportTests(CommonageAppFactory app)
{
    [Fact]
    public async Task The_export_holds_every_original_and_all_descriptions()
    {
        var item = await app.CreateContributionAsync(ItemStatus.Published, name: "Export Person", email: "export-person@example.org");
        using var admin = app.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        await Browser.UploadAsync(admin, $"/Admin/Items/Edit/{item.Id}", "Upload", "Flood: Mill Street?.jpg", TestData.Photo);
        var file = await app.WithDbAsync(db => db.MediaFiles.SingleAsync(f => f.ArchiveItemId == item.Id));

        using var zip = await ExportAsync(admin, publishedOnly: false, includePersonalData: true);

        var exported = FindItem(zip, item.Id);
        var exportedFile = exported.GetProperty("files")[0];
        var path = exportedFile.GetProperty("path").GetString()!;
        Assert.Equal($"files/{item.Id}/{file.Id}-Flood_ Mill Street_.jpg", path);
        Assert.Equal(file.Sha256, Convert.ToHexStringLower(SHA256.HashData(Read(zip, path))));
        Assert.Equal(JsonValueKind.Null, exportedFile.GetProperty("problem").ValueKind);
        Assert.Equal("Flood: Mill Street?.jpg", exportedFile.GetProperty("originalFileName").GetString());
        Assert.Equal(item.Title, exported.GetProperty("title").GetString());
        Assert.Equal("Published", exported.GetProperty("status").GetString());
        Assert.Equal("export-person@example.org", exported.GetProperty("contribution").GetProperty("contributorEmail").GetString());
        Assert.Equal("Heinz Muster", exported.GetProperty("contribution").GetProperty("rightsHolder").GetString());
        Assert.Contains(item.Title, Encoding.UTF8.GetString(Read(zip, "items.csv")));
        Assert.NotNull(zip.GetEntry("README.txt"));
        Assert.Contains("Exported the archive with contributors", await admin.GetStringAsync("/Admin/Activity"));
    }

    [Fact]
    public async Task The_export_says_in_which_order_photos_go_and_how_they_are_turned()
    {
        var item = await app.CreateContributionAsync(ItemStatus.Published);
        using var admin = app.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        await Browser.UploadAsync(admin, $"/Admin/Items/Edit/{item.Id}", "Upload", "first.jpg", TestData.Photo);
        await Browser.UploadAsync(admin, $"/Admin/Items/Edit/{item.Id}", "Upload", "second.jpg", TestData.Photo);
        await app.WithDbAsync(async db =>
        {
            var files = await db.MediaFiles.Where(f => f.ArchiveItemId == item.Id).OrderBy(f => f.Id).ToListAsync();
            files[0].Position = 1;
            files[1].Position = 0;
            files[1].Rotation = 270;
            files[1].Caption = "The back of it";
            (await db.ArchiveItems.SingleAsync(i => i.Id == item.Id)).NeedsTranscription = true;
            return await db.SaveChangesAsync();
        });

        using var zip = await ExportAsync(admin, publishedOnly: false, includePersonalData: true);
        using var publicZip = await ExportAsync(admin, publishedOnly: true, includePersonalData: false);

        var exported = FindItem(zip, item.Id);
        var first = exported.GetProperty("files")[0];
        Assert.Equal("second.jpg", first.GetProperty("originalFileName").GetString());
        Assert.Equal(0, first.GetProperty("position").GetInt32());
        Assert.Equal(270, first.GetProperty("rotation").GetInt32());
        Assert.Equal("The back of it", first.GetProperty("caption").GetString());
        Assert.Equal("first.jpg", exported.GetProperty("files")[1].GetProperty("originalFileName").GetString());
        Assert.True(exported.GetProperty("needsTranscription").GetBoolean());
        // What curators still have to do is nobody else's business.
        Assert.Equal(JsonValueKind.Null, FindItem(publicZip, item.Id).GetProperty("needsTranscription").ValueKind);
    }

    [Fact]
    public async Task Without_personal_data_no_name_or_email_is_in_the_export()
    {
        var item = await app.CreateContributionAsync(ItemStatus.Submitted, name: "Hidden Person", email: "hidden-person@example.org");
        using var admin = app.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);

        using var zip = await ExportAsync(admin, publishedOnly: false, includePersonalData: false);

        var contribution = FindItem(zip, item.Id).GetProperty("contribution");
        Assert.Equal(JsonValueKind.Null, contribution.GetProperty("contributorName").ValueKind);
        Assert.Equal("PermissionFromRightsHolder", contribution.GetProperty("rights").GetString());
        foreach (var entry in zip.Entries.Where(e => !e.FullName.StartsWith("files/")))
        {
            var text = Encoding.UTF8.GetString(Read(zip, entry.FullName));
            Assert.DoesNotContain("Hidden Person", text);
            Assert.DoesNotContain("hidden-person@example.org", text);
            Assert.DoesNotContain("Heinz Muster", text);
        }
    }

    /// <summary>
    /// A new field has to be exported or be left out on purpose. Otherwise an archive would lose it when it moves away,
    /// and nobody would notice until then. Names are as they appear in archive.json.
    /// </summary>
    [Theory]
    [InlineData(typeof(ArchiveItem), "item", "id collectionId title description status createdAt updatedAt publishedAt rejectedOrWithdrawnAt contribution files tags people rightsUri rightsHolder credit referenceNumber originalLocation receivedFrom receivedOn internalNotes needsTranscription helpWanted mediaLevel publicFrom notes",
        "Collection: by collectionId. MediaFiles: as files. People: as people, the numbers of the people in the list \"people\". DateText, DateFrom, DateTo: as date. TagNames: repeats tags. "
        + "CanBePublished: follows from status. IsUntitled: follows from the empty title. MayTravel: follows from the contribution. FileText: the text of the PDFs, found again from the files. DeletedAt: the bin is not exported. Version: only for open forms.")]
    [InlineData(typeof(MediaFile), "file", "id originalFileName contentType sizeBytes sha256 uploadedAt altText caption position rotation pageCount",
        "ArchiveItemId, ArchiveItem: the file is listed under its item. StorageId, OriginalKey, PreviewKey, ThumbnailKey: storage details. DerivativeKeys, StoredKeys, PageKey, AudioKey, IsAudio, IsPdf, PdfContentType, MaxPdfPages: follow from the file or are fixed values. ExtractedText: the text of a PDF, found again from the file. "
        + "Status, ProcessingError, ProcessingAttempts, MaxProcessingAttempts, PreviewWidth, PreviewHeight, CardKey, LargeKey, HasLarge, RenderedRotation, IsBeingTurned, DerivativeVersion, CurrentDerivativeVersion, MaxCaptionLength: previews are made anew. DeletedAt: the bin is not exported.")]
    [InlineData(typeof(ArchiveSettings), "settings", "defaultLanguage contentLanguage otherContentLanguages accentColor timeZone defaultRightsUri allowSearchEngines requireTwoFactor largePhotos privacyReviewed imprintNotNeeded restoreTestedAt activityLogKeptYears rejectedKeptMonths acceptVisitorNotes logo texts",
        "Id, TheOnlyId: there is only one. LogoId: as logo.")]
    [InlineData(typeof(Contribution), "contribution", "contributorName contributorEmail showName rights rightsHolder consentText reuse consentCoversSharing referenceCode submittedAt reviewedAt personalDetailsRemovedAt contributionLink",
        "Id, ArchiveItemId, ArchiveItem: listed under its item. ContributionLinkId: the link is named. SendingFingerprint: only there to recognise a form sent twice.")]
    [InlineData(typeof(Collection), "collection", "id title description createdAt position sort coverFileId", "Items: each item names its collection. CoverMediaFileId: as coverFileId.")]
    [InlineData(typeof(Tag), "tag", "name kind", "Id: items name their tags. NormalizedName: follows from the name. Items: each item lists its tags.")]
    [InlineData(typeof(VisitorNote), "note", "kind message name email receivedAt handledAt personalDetailsRemovedAt", "Id, ArchiveItemId, ArchiveItem: listed under its item. RemovePersonalDetails: a method.")]
    [InlineData(typeof(Story), "story", "id title summary status createdAt publishedAt position blocks",
        "UpdatedAt: made again where the story arrives. DeletedAt: the bin is not exported. Version: only for open forms. IsUntitled: follows from the empty title. CanBePublished: follows from the blocks and the title.")]
    [InlineData(typeof(StoryBlock), "block", "kind text itemId caption",
        "Id, StoryId, Story: the block is listed under its story. ArchiveItemId: as itemId. Position: follows from the order they are listed in. ArchiveItem: as itemId, and only when that item is in the export.")]
    [InlineData(typeof(Person), "person", "id publicId name sortName disclosure born died biography curatorNote createdAt names",
        "FoldedName: follows from the name. DisclosureChangedAt, UpdatedAt, Version: only for open forms and the order of the work. NeedsReview: follows from DisclosureChangedAt, which stays out because it only tells the review list of this archive who still waits. BornText, BornFrom, BornTo: as born. DiedText, DiedFrom, DiedTo: as died. DeletedAt: the bin is not exported. "
        + "Links: each item lists its people. HasDied, IsHidden, MayBeShown: follow from disclosure.")]
    [InlineData(typeof(PersonName), "personName", "name kind language shownToVisitors", "Id, PersonId, Person: the name is listed under its person. FoldedName: follows from the name.")]
    public async Task Every_field_is_exported_or_left_out_on_purpose(Type type, string part, string exported, string leftOut)
    {
        var item = await app.CreateContributionAsync(ItemStatus.Published);
        var storyTitle = TestData.Unique("Fields story");
        using var admin = app.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        await Browser.UploadAsync(admin, $"/Admin/Items/Edit/{item.Id}", "Upload", "photo.jpg", TestData.Photo);
        await app.WithDbAsync(async db =>
        {
            var tagged = await db.ArchiveItems.Include(i => i.Tags).SingleAsync(i => i.Id == item.Id);
            var name = TestData.Unique("place");
            tagged.SetTags([new Tag { Name = name, NormalizedName = Tag.Normalize(name), Kind = TagKind.Place }]);
            var someone = db.AddHiddenPerson(TestData.Unique("Someone"), DateTime.UtcNow);
            someone.Names.Add(new PersonName { Name = TestData.Unique("Other"), Kind = PersonNameKind.Nickname, Language = "de", ShownToVisitors = true });
            someone.CuratorNote = "A note for curators";
            db.ItemPeople.Add(new ItemPerson { ArchiveItemId = item.Id, Person = someone, CreatedAt = DateTime.UtcNow });
            db.VisitorNotes.Add(new VisitorNote { ArchiveItemId = item.Id, Kind = VisitorNoteKind.Information, Message = "A note", ReceivedAt = DateTime.UtcNow });
            var story = new Story { Title = storyTitle, Status = StoryStatus.Draft, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            story.Blocks.Add(new StoryBlock { Position = 0, Kind = StoryBlockKind.Item, ArchiveItemId = item.Id, Caption = "A caption" });
            db.Stories.Add(story);
            return await db.SaveChangesAsync();
        });
        using var zip = await ExportAsync(admin, publishedOnly: false, includePersonalData: true);
        var exportedItem = FindItem(zip, item.Id);
        var exportedStory = JsonDocument.Parse(Read(zip, "archive.json")).RootElement.GetProperty("stories").EnumerateArray()
            .Single(s => s.GetProperty("title").GetString() == storyTitle);
        var json = part switch
        {
            "item" => exportedItem,
            "file" => exportedItem.GetProperty("files")[0],
            "contribution" => exportedItem.GetProperty("contribution"),
            "settings" => JsonDocument.Parse(Read(zip, "archive.json")).RootElement.GetProperty("settings"),
            "note" => exportedItem.GetProperty("notes")[0],
            "story" => exportedStory,
            "block" => exportedStory.GetProperty("blocks")[0],
            "tag" => JsonDocument.Parse(Read(zip, "archive.json")).RootElement.GetProperty("tags")[0],
            "person" => JsonDocument.Parse(Read(zip, "archive.json")).RootElement.GetProperty("people").EnumerateArray()
                .First(p => p.GetProperty("id").GetInt32() == exportedItem.GetProperty("people")[0].GetInt32()),
            "personName" => JsonDocument.Parse(Read(zip, "archive.json")).RootElement.GetProperty("people").EnumerateArray()
                .First(p => p.GetProperty("id").GetInt32() == exportedItem.GetProperty("people")[0].GetInt32()).GetProperty("names")[0],
            _ => JsonDocument.Parse(Read(zip, "archive.json")).RootElement.GetProperty("collections")[0],
        };

        foreach (var name in exported.Split(' '))
            Assert.True(json.TryGetProperty(name, out _), $"{name} is said to be exported, but is not in archive.json.");

        var known = exported.Split(' ').Select(n => char.ToUpperInvariant(n[0]) + n[1..]).ToHashSet();
        var missing = type.GetProperties()
            .Select(p => p.Name)
            .Where(name => !known.Contains(name) && !System.Text.RegularExpressions.Regex.IsMatch(leftOut, $@"\b{name}\b"))
            .ToList();
        Assert.True(missing.Count == 0, $"{type.Name} has fields that are neither exported nor left out on purpose: {string.Join(", ", missing)}. "
            + "Add them to the export and docs/operations.md, or say here why they stay out.");
    }

    [Fact]
    public async Task People_are_exported_by_the_kind_of_copy_and_never_with_what_is_only_for_curators_in_a_public_copy()
    {
        await using var archive = await app.NewArchiveAsync();
        using var admin = archive.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        await archive.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            var collection = new Collection { Title = "Club", CreatedAt = now };
            ArchiveItem Item(string title, bool published)
            {
                var item = new ArchiveItem { Title = title, Collection = collection, CreatedAt = now, UpdatedAt = now, Status = ItemStatus.Draft };
                if (published)
                    item.Publish(now);
                return item;
            }

            var shown = Item("Shown match", true);
            var draft = Item("Draft match", false);
            db.ArchiveItems.AddRange(shown, draft);
            db.AddDemoPerson("Zora Quillfeather", hasDied: true, born: "1931", died: "2009", "Zora wrote the club songs.", note: "Note-for-curators-Zora",
                [new ImportedPersonOtherName("Zorchen", PersonNameKind.Nickname, "de")], shown, now);
            db.AddDemoPerson("Ulf Marrowby", hasDied: false, born: "1950", died: null, "Biography-of-Ulf", note: "Note-for-curators-Ulf",
                [], draft, now);
            await db.SaveChangesAsync();
            return 0;
        });

        using var publicZip = await ExportAsync(admin, publishedOnly: true, includePersonalData: false);
        using var plainZip = await ExportAsync(admin, publishedOnly: false, includePersonalData: false);
        using var fullZip = await ExportAsync(admin, publishedOnly: false, includePersonalData: true);

        var publicText = Encoding.UTF8.GetString(Read(publicZip, "archive.json"));
        var publicPeople = JsonDocument.Parse(publicText).RootElement.GetProperty("people").EnumerateArray().ToList();
        var zora = Assert.Single(publicPeople);
        Assert.Equal("Zora Quillfeather", zora.GetProperty("name").GetString());
        Assert.Contains("Zorchen", publicText);
        foreach (var hidden in new[] { "Ulf Marrowby", "Biography-of-Ulf", "Note-for-curators-Zora", "Note-for-curators-Ulf" })
            Assert.DoesNotContain(hidden, publicText);
        Assert.False(zora.TryGetProperty("curatorNote", out var note) && note.ValueKind == JsonValueKind.String);
        Assert.False(zora.TryGetProperty("publicId", out var publicId) && publicId.ValueKind == JsonValueKind.String);

        var plainText = Encoding.UTF8.GetString(Read(plainZip, "archive.json"));
        Assert.DoesNotContain("Ulf Marrowby", plainText);
        Assert.DoesNotContain("Note-for-curators", plainText);

        var fullText = Encoding.UTF8.GetString(Read(fullZip, "archive.json"));
        var fullPeople = JsonDocument.Parse(fullText).RootElement.GetProperty("people").EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToList();
        Assert.Equal(["Ulf Marrowby", "Zora Quillfeather"], fullPeople.Order().ToList());
        Assert.Contains("Note-for-curators-Ulf", fullText);
    }

    [Fact]
    public async Task Items_in_the_bin_are_not_exported()
    {
        var binned = await app.CreateContributionAsync(ItemStatus.Published);
        using var admin = app.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        var page = $"/Admin/Items/Edit/{binned.Id}";
        await Browser.PostFormAsync(admin, $"{page}?handler=Delete", new(), page);

        using var zip = await ExportAsync(admin, publishedOnly: false, includePersonalData: false);

        Assert.DoesNotContain(binned.Title, Encoding.UTF8.GetString(Read(zip, "archive.json")));
    }

    /// <summary>The earlier texts of an item are left out on purpose: the archive moves with what is true now.</summary>
    [Fact]
    public async Task Earlier_texts_are_not_exported()
    {
        var item = await app.CreateContributionAsync(ItemStatus.Published);
        var earlier = $"EARLIER-{Guid.NewGuid():N}";
        await app.WithDbAsync(async db =>
        {
            db.ItemRevisions.Add(new ItemRevision { ArchiveItemId = item.Id, At = DateTime.UtcNow, Field = RevisionField.Description, OldValue = earlier });
            return await db.SaveChangesAsync();
        });
        using var admin = app.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);

        using var zip = await ExportAsync(admin, publishedOnly: false, includePersonalData: true);

        Assert.DoesNotContain(earlier, Encoding.UTF8.GetString(Read(zip, "archive.json")));
    }

    [Fact]
    public async Task A_published_only_export_leaves_out_drafts_and_contributions()
    {
        var published = await app.CreateContributionAsync(ItemStatus.Published);
        var waiting = await app.CreateContributionAsync(ItemStatus.Submitted);
        using var admin = app.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);

        using var zip = await ExportAsync(admin, publishedOnly: true, includePersonalData: false);

        var document = JsonDocument.Parse(Read(zip, "archive.json")).RootElement;
        var ids = document.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()).ToList();
        var statuses = document.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("status").GetString()).Distinct();
        Assert.Contains(published.Id, ids);
        Assert.DoesNotContain(waiting.Id, ids);
        Assert.Equal(["Published"], statuses);
        Assert.Equal(10, document.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task A_copy_of_what_is_public_holds_nothing_of_drafts_however_many_batches_it_takes()
    {
        var first = await app.CreateContributionAsync(ItemStatus.Published);
        var second = await app.CreateContributionAsync(ItemStatus.Published);
        var third = await app.CreateContributionAsync(ItemStatus.Published);
        var draft = await app.CreateContributionAsync(ItemStatus.Submitted);
        using var admin = app.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        foreach (var item in new[] { first, second, third, draft })
            await Browser.UploadAsync(admin, $"/Admin/Items/Edit/{item.Id}", "Upload", "photo.jpg", TestData.Photo);
        var tagName = TestData.Unique("only-in-a-draft");
        await app.WithDbAsync(async db =>
        {
            var stored = await db.ArchiveItems.Include(i => i.Tags).SingleAsync(i => i.Id == draft.Id);
            stored.SetTags([new Tag { Name = tagName, NormalizedName = Tag.Normalize(tagName) }]);
            var draftFile = await db.MediaFiles.SingleAsync(f => f.ArchiveItemId == draft.Id);
            (await db.Collections.SingleAsync(c => c.Id == draft.CollectionId)).CoverMediaFileId = draftFile.Id;
            return await db.SaveChangesAsync();
        });

        // Two items at a time, so that the files and the descriptions each take several rounds.
        var zip = await app.WithDbAsync(async db =>
        {
            var exporter = new ArchiveExporter(
                db, app.Services.GetRequiredService<Commonage.Web.Media.IFileStorage>(), TimeProvider.System,
                app.Services.GetRequiredService<IStringLocalizer<Commonage.Web.SharedResource>>(),
                app.Services.GetRequiredService<ILogger<ArchiveExporter>>()) { BatchSize = 2 };
            var stream = new MemoryStream();
            await exporter.WriteAsync(stream, new ExportOptions(PublishedOnly: true, IncludePersonalData: false));
            stream.Position = 0;
            return new ZipArchive(stream);
        });

        var document = JsonDocument.Parse(Read(zip, "archive.json")).RootElement;
        var ids = document.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal(ids.Order(), ids);
        Assert.All(new[] { first, second, third }, item =>
        {
            Assert.Contains(item.Id, ids);
            Assert.Single(zip.Entries, e => e.FullName.StartsWith($"files/{item.Id}/"));
        });
        Assert.DoesNotContain(draft.Id, ids);
        Assert.DoesNotContain(zip.Entries, e => e.FullName.StartsWith($"files/{draft.Id}/"));
        var csv = Encoding.UTF8.GetString(Read(zip, "items.csv"));
        Assert.All(new[] { first, second, third }, item => Assert.Contains($"\n{item.Id},", csv));
        Assert.DoesNotContain($"\n{draft.Id},", csv);
        Assert.DoesNotContain(tagName, document.GetProperty("tags").EnumerateArray().Select(t => t.GetProperty("name").GetString()));
        // The draft's collection may or may not be in the copy, but never with a cover that is a draft's photo.
        Assert.All(
            document.GetProperty("collections").EnumerateArray().Where(c => c.GetProperty("id").GetInt32() == draft.CollectionId),
            collection => Assert.Equal(JsonValueKind.Null, collection.GetProperty("coverFileId").ValueKind));
    }

    [Fact]
    public async Task Text_sent_in_by_anyone_cannot_become_a_spreadsheet_formula()
    {
        var item = await app.CreateContributionAsync(ItemStatus.Submitted);
        await app.WithDbAsync(async db =>
        {
            var stored = await db.ArchiveItems.SingleAsync(i => i.Id == item.Id);
            stored.Title = "=HYPERLINK(\"https://example.org\", \"Click\")";
            return await db.SaveChangesAsync();
        });
        using var admin = app.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);

        using var zip = await ExportAsync(admin, publishedOnly: false, includePersonalData: false);

        var csv = Encoding.UTF8.GetString(Read(zip, "items.csv"));
        Assert.Contains($"{item.Id},", csv);
        Assert.Contains("\"'=HYPERLINK(\"\"https://example.org\"\", \"\"Click\"\")\"", csv);
        Assert.Equal("=HYPERLINK(\"https://example.org\", \"Click\")", FindItem(zip, item.Id).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Only_admins_can_export()
    {
        using var admin = app.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        var (curator, _, _, _) = await app.CreateAccountAsync(admin);

        var response = await Browser.SendFormAsync(curator, "/Admin/Export", new(), pageUrl: "/Admin");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotEqual("application/zip", response.Content.Headers.ContentType?.MediaType);
        curator.Dispose();
    }

    [Fact]
    public async Task The_README_is_in_the_language_of_whoever_exports_with_an_English_copy()
    {
        using var admin = app.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        using var english = await ExportAsync(admin, publishedOnly: true, includePersonalData: false);
        await Browser.SendFormAsync(admin, "/SetLanguage", new() { ["culture"] = "de", ["returnUrl"] = "/" }, pageUrl: "/");

        using var german = await ExportAsync(admin, publishedOnly: true, includePersonalData: false);

        var readme = Encoding.UTF8.GetString(Read(english, "README.txt"));
        Assert.StartsWith("Commonage archive export\n========================\n", readme);
        Assert.Contains("archive.json\n    Everything that is known", readme);
        Assert.All(readme.Split('\n'), line => Assert.True(line.Length <= 76, line));
        Assert.Null(english.GetEntry("README.en.txt"));
        Assert.StartsWith("Export eines Commonage-Archivs\n", Encoding.UTF8.GetString(Read(german, "README.txt")));
        Assert.Contains("files/<item id>/<file id>-<original file name>", Encoding.UTF8.GetString(Read(german, "README.txt")));
        Assert.Equal(readme, Encoding.UTF8.GetString(Read(german, "README.en.txt")));
    }

    /// <summary>
    /// What a copy holds of stories: a copy of what is public holds the published stories that visitors could find, with only the
    /// parts whose item is in the copy; a copy of everything holds drafts too, but nothing from the bin. No part names an item
    /// that is not in the same file.
    /// </summary>
    [Fact]
    public async Task Stories_travel_with_the_rules_of_what_is_public_and_name_only_items_that_are_exported()
    {
        await using var archive = await app.NewArchiveAsync();
        using var admin = archive.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        var shownId = await archive.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            var collection = new Collection { Title = "Streets", CreatedAt = now };
            ArchiveItem Item(string title, bool published, DateOnly? from = null)
            {
                var item = new ArchiveItem { Title = title, Collection = collection, CreatedAt = now, UpdatedAt = now, Status = ItemStatus.Draft, PublicFrom = from };
                if (published)
                    item.Publish(now);
                return item;
            }

            var shown = Item("Shown flood", true);
            var draft = Item("Draft flood", false);
            var embargoed = Item("Embargoed flood", true, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30));
            var binned = Item("Binned flood", true);
            binned.MoveToBin(now);
            db.ArchiveItems.AddRange(shown, draft, embargoed, binned);
            Story Make(string title, StoryStatus status, params (StoryBlockKind Kind, string? Text, ArchiveItem? Item, string? Caption)[] blocks)
            {
                var story = new Story { Title = title, Status = StoryStatus.Draft, CreatedAt = now, UpdatedAt = now };
                if (status == StoryStatus.Published)
                    story.Publish(now);
                foreach (var (kind, text, item, caption) in blocks)
                    story.Blocks.Add(new StoryBlock { Position = story.Blocks.Count, Kind = kind, Text = text, ArchiveItem = item, Caption = caption });
                db.Stories.Add(story);
                return story;
            }

            const StoryBlockKind T = StoryBlockKind.Text, I = StoryBlockKind.Item;
            Make("Public story", StoryStatus.Published, (T, "Opening words", null, null), (I, null, shown, "Shown caption"), (I, null, draft, "Draft caption"),
                (I, null, embargoed, "Embargoed caption"), (I, null, binned, "Binned caption"));
            Make("Only hidden items", StoryStatus.Published, (I, null, draft, "Hidden one"), (I, null, embargoed, "Hidden two"));
            Make("A draft story", StoryStatus.Draft, (T, "Draft words", null, null), (I, null, draft, "Draft story caption"));
            var gone = Make("A story in the bin", StoryStatus.Published, (T, "Bin words", null, null));
            gone.MoveToBin(now);
            await db.SaveChangesAsync();
            return shown.Id;
        });

        using var publicZip = await ExportAsync(admin, publishedOnly: true, includePersonalData: false);
        using var fullZip = await ExportAsync(admin, publishedOnly: false, includePersonalData: true);

        var publicJson = JsonDocument.Parse(Read(publicZip, "archive.json")).RootElement;
        var publicStories = publicJson.GetProperty("stories").EnumerateArray().ToList();
        Assert.Equal(["Public story"], publicStories.Select(s => s.GetProperty("title").GetString()));
        var parts = publicStories[0].GetProperty("blocks").EnumerateArray().ToList();
        Assert.Equal(["Text", "Item"], parts.Select(p => p.GetProperty("kind").GetString()));
        Assert.Equal(shownId, parts[1].GetProperty("itemId").GetInt32());
        Assert.Equal("Shown caption", parts[1].GetProperty("caption").GetString());
        var publicText = Encoding.UTF8.GetString(Read(publicZip, "archive.json"));
        foreach (var hidden in new[] { "Draft caption", "Embargoed caption", "Binned caption", "Hidden one", "Hidden two", "Draft words", "Draft story caption", "Bin words", "Only hidden items", "A draft story" })
            Assert.DoesNotContain(hidden, publicText);

        var fullJson = JsonDocument.Parse(Read(fullZip, "archive.json")).RootElement;
        var fullStories = fullJson.GetProperty("stories").EnumerateArray().ToList();
        Assert.Equal(["Public story", "Only hidden items", "A draft story"], fullStories.Select(s => s.GetProperty("title").GetString()));
        var fullParts = fullStories[0].GetProperty("blocks").EnumerateArray().Select(p => p.GetProperty("caption").GetString() ?? p.GetProperty("text").GetString()).ToList();
        Assert.Equal(["Opening words", "Shown caption", "Draft caption", "Embargoed caption"], fullParts);
        Assert.DoesNotContain("Bin words", Encoding.UTF8.GetString(Read(fullZip, "archive.json")));
        Assert.DoesNotContain("Binned caption", Encoding.UTF8.GetString(Read(fullZip, "archive.json")));

        foreach (var document in new[] { publicJson, fullJson })
        {
            var exported = document.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()).ToHashSet();
            foreach (var story in document.GetProperty("stories").EnumerateArray())
                foreach (var part in story.GetProperty("blocks").EnumerateArray().Where(p => p.GetProperty("kind").GetString() == "Item"))
                    Assert.Contains(part.GetProperty("itemId").GetInt32(), exported);
        }
    }

    private static async Task<ZipArchive> ExportAsync(HttpClient admin, bool publishedOnly, bool includePersonalData)
    {
        var fields = new Dictionary<string, string>();
        if (publishedOnly)
            fields["PublishedOnly"] = "true";
        if (includePersonalData)
            fields["IncludePersonalData"] = "true";
        var response = await Browser.PostFormAsync(admin, "/Admin/Export", fields);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("commonage-export-", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        return new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
    }

    private static JsonElement FindItem(ZipArchive zip, int id) =>
        JsonDocument.Parse(Read(zip, "archive.json")).RootElement.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("id").GetInt32() == id);

    private static byte[] Read(ZipArchive zip, string path)
    {
        using var stream = zip.GetEntry(path)!.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
