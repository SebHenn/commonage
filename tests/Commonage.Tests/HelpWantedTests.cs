using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Commonage.Web.Domain;
using Commonage.Web.Operations;
using Commonage.Web.Search;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Commonage.Tests;

/// <summary>"Can you help?": curators flag items they hope visitors know more about, and visitors find them on one page.</summary>
[Collection(nameof(AppCollection))]
public class HelpWantedTests(CommonageAppFactory app)
{
    private record Ids(int Flagged, int Plain, int FlaggedDraft, int FlaggedBinned);

    private static async Task<(WebApplicationFactory<Program> Archive, Ids Ids)> ArchiveAsync(CommonageAppFactory app, bool asking)
    {
        var archive = await app.NewArchiveAsync();
        var ids = await archive.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            var collection = new Collection { Title = "Town", CreatedAt = now };
            ArchiveItem Item(string title, bool help) => new() { Title = title, Collection = collection, CreatedAt = now, UpdatedAt = now, Status = ItemStatus.Draft, HelpWanted = help };
            var flagged = Item("Unknown choir", true);
            flagged.Publish(now);
            var plain = Item("Known bridge", false);
            plain.Publish(now);
            var draft = Item("Unchecked wedding", true);
            var binned = Item("Binned parade", true);
            binned.Publish(now);
            binned.MoveToBin(now);
            db.ArchiveItems.AddRange(flagged, plain, draft, binned);
            var settings = await db.ArchiveSettings.FirstOrDefaultAsync();
            if (settings is null)
                db.ArchiveSettings.Add(settings = new ArchiveSettings());
            settings.AcceptVisitorNotes = asking;
            await db.SaveChangesAsync();
            return new Ids(flagged.Id, plain.Id, draft.Id, binned.Id);
        });
        archive.Services.GetRequiredService<ArchiveProfile>().Forget();
        return (archive, ids);
    }

    [Fact]
    public async Task The_page_lists_only_published_flagged_items_and_only_while_the_archive_asks()
    {
        var (archive, _) = await ArchiveAsync(app, asking: true);
        await using var _ = archive;
        using var visitor = archive.CreateClient();

        var page = await visitor.GetStringAsync("/help");

        Assert.Contains("Unknown choir", page);
        Assert.DoesNotContain("Known bridge", page);
        Assert.DoesNotContain("Unchecked wedding", page);
        Assert.DoesNotContain("Binned parade", page);
        Assert.Contains("href=\"/help\"", await visitor.GetStringAsync("/"));

        var (closed, _) = await ArchiveAsync(app, asking: false);
        await using var _2 = closed;
        using var other = closed.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync("/help")).StatusCode);
        Assert.DoesNotContain("href=\"/help\"", await other.GetStringAsync("/"));
    }

    [Fact]
    public async Task An_item_says_it_asks_for_help_but_only_when_flagged()
    {
        var (archive, ids) = await ArchiveAsync(app, asking: true);
        await using var _ = archive;
        using var visitor = archive.CreateClient();

        Assert.Contains("looking for help with this item", await visitor.GetStringAsync($"/items/{ids.Flagged}"));
        Assert.DoesNotContain("looking for help with this item", await visitor.GetStringAsync($"/items/{ids.Plain}"));
    }

    [Fact]
    public async Task A_curator_flags_an_item_and_the_dashboard_counts_it_also_when_not_yet_published()
    {
        var (archive, ids) = await ArchiveAsync(app, asking: true);
        await using var _ = archive;
        using var curator = archive.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);
        using var visitor = archive.CreateClient();

        Assert.DoesNotContain("looking for help", await visitor.GetStringAsync($"/items/{ids.Plain}"));
        await Browser.PostFormAsync(curator, $"/Admin/Items/Edit/{ids.Plain}", new()
        {
            ["Input.Title"] = "Known bridge",
            ["Input.CollectionId"] = (await archive.WithDbAsync(db => db.ArchiveItems.Where(i => i.Id == ids.Plain).Select(i => i.CollectionId).SingleAsync())).ToString(),
            ["Input.HelpWanted"] = "true",
        });

        Assert.Contains("Known bridge", await visitor.GetStringAsync("/help"));
        var dashboard = await curator.GetStringAsync("/Admin");
        Assert.Contains("3 items ask visitors for help", dashboard);
        Assert.Contains("1 of them is not published yet", dashboard);
        Assert.Contains("Known bridge", await curator.GetStringAsync($"/Admin?needs={ItemNeed.HelpWanted}"));
    }

    [Fact]
    public async Task The_dashboard_counts_open_notes_and_removal_requests()
    {
        var (archive, ids) = await ArchiveAsync(app, asking: true);
        await using var _ = archive;
        await archive.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            db.VisitorNotes.AddRange(
                new VisitorNote { ArchiveItemId = ids.Flagged, Kind = VisitorNoteKind.Removal, Message = "Please remove", ReceivedAt = now },
                new VisitorNote { ArchiveItemId = ids.Flagged, Kind = VisitorNoteKind.Information, Message = "My aunt", ReceivedAt = now },
                new VisitorNote { ArchiveItemId = ids.Flagged, Kind = VisitorNoteKind.Removal, Message = "Done", ReceivedAt = now, HandledAt = now });
            await db.SaveChangesAsync();
            return 0;
        });
        using var curator = archive.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);

        var dashboard = await curator.GetStringAsync("/Admin");

        Assert.Contains("2 notes from visitors to deal with", dashboard);
        Assert.Contains("1 asks for removal", dashboard);
    }

    [Fact]
    public async Task The_public_copy_of_the_export_says_which_items_ask_for_help()
    {
        var (archive, ids) = await ArchiveAsync(app, asking: true);
        await using var _ = archive;
        using var admin = archive.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        var response = await Browser.PostFormAsync(admin, "/Admin/Export", new() { ["PublishedOnly"] = "true" });
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var zip = new ZipArchive(stream);
        using var reader = new StreamReader(zip.GetEntry("archive.json")!.Open());

        var items = JsonDocument.Parse(await reader.ReadToEndAsync()).RootElement.GetProperty("items").EnumerateArray().ToList();

        Assert.True(items.Single(i => i.GetProperty("id").GetInt32() == ids.Flagged).GetProperty("helpWanted").GetBoolean());
        Assert.False(items.Single(i => i.GetProperty("id").GetInt32() == ids.Plain).GetProperty("helpWanted").GetBoolean());
    }
}
