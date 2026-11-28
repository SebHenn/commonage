using System.IO.Compression;
using System.Net;
using System.Text;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Commonage.Tests;

/// <summary>
/// People in the bin: gone for visitors, the search and the export at once, restorable for 30 days, deleted for good by hand (a removal
/// request) or by the purge. The log keeps what was done and never the name. Each test has an archive of its own.
/// </summary>
[Collection(nameof(AppCollection))]
public class PersonBinTests(CommonageAppFactory app)
{
    private sealed record Seeded(int PersonId, int ItemId, PersonWords Words, string PublicKey);

    /// <summary>A person who has died, with other names, linked to a published item.</summary>
    private static async Task<Seeded> SeedAsync(WebApplicationFactory<Program> archive)
    {
        var words = PersonWords.New("b");
        var (personId, itemId) = await archive.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            var collection = new Collection { Title = $"Box {TestData.UniqueWord("c")}", CreatedAt = now };
            var item = PeopleSeed.NewItem(collection, $"Flood {TestData.UniqueWord("i")}", now, true);
            var person = PeopleSeed.NewPerson(words, PersonDisclosure.Deceased, now);
            db.AddRange(collection, item, person, PeopleSeed.Link(item, person, now));
            await db.SaveChangesAsync();
            return (person.Id, item.Id);
        });
        return new Seeded(personId, itemId, words, await PeopleSeed.KeyAsync(archive, personId));
    }

    private static async Task<HttpClient> CuratorAsync(WebApplicationFactory<Program> archive)
    {
        var curator = archive.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);
        return curator;
    }

    private static string EditPage(int id) => $"/Admin/People/Edit/{id}";

    private static Task<HttpResponseMessage> MoveToBinAsync(HttpClient curator, int id) =>
        Browser.SendFormAsync(curator, $"{EditPage(id)}?handler=MoveToBin", new(), EditPage(id));

    private static Task<HttpResponseMessage> BinPostAsync(HttpClient curator, string handler, int id) =>
        Browser.SendFormAsync(curator, $"/Admin/Bin?handler={handler}&id={id}", new(), "/Admin/Bin");

    private static Task<Person?> FindEvenInBinAsync(WebApplicationFactory<Program> archive, int id) =>
        archive.WithDbAsync(db => db.People.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Id == id));

    private static async Task<string> ExportJsonAsync(HttpClient curator, bool publishedOnly)
    {
        var fields = new Dictionary<string, string>();
        if (publishedOnly)
            fields["PublishedOnly"] = "true";
        var response = await Browser.PostFormAsync(curator, "/Admin/Export", fields);
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        using var reader = new StreamReader(zip.GetEntry("archive.json")!.Open(), Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static async Task<bool> SearchFindsAsync(HttpClient visitor, Seeded s, string words) =>
        (await PeopleSeed.TextAsync(visitor, $"/search?q={Uri.EscapeDataString(words)}")).Contains($"/items/{s.ItemId}\"");

    [Fact]
    public async Task A_person_in_the_bin_is_gone_for_visitors_search_and_export_and_found_again_after_a_restore()
    {
        await using var archive = await PeopleSeed.NewArchiveAsync(app);
        var s = await SeedAsync(archive);
        using var curator = await CuratorAsync(archive);
        using var visitor = archive.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync($"/people/{s.PublicKey}")).StatusCode);
        Assert.True(await SearchFindsAsync(visitor, s, s.Words.Name));
        Assert.Contains(s.Words.Name, await PeopleSeed.TextAsync(visitor, $"/items/{s.ItemId}"));
        Assert.Contains(s.Words.Name, await ExportJsonAsync(curator, publishedOnly: false));

        var moved = await MoveToBinAsync(curator, s.PersonId);

        Assert.EndsWith("/Admin/People", moved.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/people/{s.PublicKey}")).StatusCode);
        Assert.False(await SearchFindsAsync(visitor, s, s.Words.Name));
        Assert.False(await SearchFindsAsync(visitor, s, s.Words.Marked));
        Assert.DoesNotContain(s.Words.Name, await PeopleSeed.TextAsync(visitor, $"/items/{s.ItemId}"));
        Assert.DoesNotContain(s.Words.Name, await PeopleSeed.TextAsync(visitor, "/people"));
        Assert.DoesNotContain(s.Words.Name, await ExportJsonAsync(curator, publishedOnly: false));
        Assert.DoesNotContain(s.Words.Name, await ExportJsonAsync(curator, publishedOnly: true));
        Assert.DoesNotContain(s.Words.Secret, await ExportJsonAsync(curator, publishedOnly: false));
        Assert.DoesNotContain(s.Words.Name, await PeopleSeed.TextAsync(curator, "/Admin/People"));
        // The person's page does not open for curators either; the bin is where they are dealt with.
        Assert.Equal(HttpStatusCode.NotFound, (await curator.GetAsync(EditPage(s.PersonId))).StatusCode);
        Assert.Contains(s.Words.Name, await PeopleSeed.TextAsync(curator, "/Admin/Bin"));
        // Curators looking for a contributor still find them.
        Assert.Contains(s.Words.Name, await PeopleSeed.TextAsync(curator, $"/Admin/Contributors?q={Uri.EscapeDataString(s.Words.Name)}"));
        Assert.NotNull((await FindEvenInBinAsync(archive, s.PersonId))!.DeletedAt);

        await BinPostAsync(curator, "RestorePerson", s.PersonId);

        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync($"/people/{s.PublicKey}")).StatusCode);
        Assert.True(await SearchFindsAsync(visitor, s, s.Words.Name));
        Assert.Contains(s.Words.Name, await PeopleSeed.TextAsync(visitor, $"/items/{s.ItemId}"));
        Assert.Contains(s.Words.Name, await ExportJsonAsync(curator, publishedOnly: false));
        Assert.DoesNotContain(s.Words.Name, await PeopleSeed.TextAsync(curator, "/Admin/Bin"));
        Assert.Equal(
            [AuditAction.PersonMovedToBin, AuditAction.PersonRestored],
            await archive.WithDbAsync(db => db.AuditEntries.Where(e => e.PersonId == s.PersonId).OrderBy(e => e.Id).Select(e => e.Action).ToListAsync()));
    }

    [Fact]
    public async Task Deleting_a_person_from_the_bin_removes_names_and_links_but_not_the_items_and_the_log_keeps_no_name()
    {
        await using var archive = await PeopleSeed.NewArchiveAsync(app);
        var s = await SeedAsync(archive);
        using var curator = await CuratorAsync(archive);
        await MoveToBinAsync(curator, s.PersonId);

        await BinPostAsync(curator, "DeletePerson", s.PersonId);

        Assert.Null(await FindEvenInBinAsync(archive, s.PersonId));
        Assert.False(await archive.WithDbAsync(db => db.PersonNames.IgnoreQueryFilters().AnyAsync(n => n.Name == s.Words.Marked || n.Name == s.Words.Secret)));
        Assert.False(await archive.WithDbAsync(db => db.ItemPeople.IgnoreQueryFilters().AnyAsync(l => l.PersonId == s.PersonId)));
        Assert.True(await archive.WithDbAsync(db => db.ArchiveItems.AnyAsync(i => i.Id == s.ItemId && i.Status == ItemStatus.Published)));
        var entries = await archive.WithDbAsync(db => db.AuditEntries.Where(e => e.Action == AuditAction.PersonMovedToBin || e.Action == AuditAction.PersonDeleted).ToListAsync());
        Assert.Contains(entries, e => e.Action == AuditAction.PersonDeleted);
        Assert.All(entries, e => Assert.Null(e.PersonId));
        Assert.All(entries, e => Assert.DoesNotContain(s.Words.Name, e.Subject ?? ""));
        var activity = await PeopleSeed.TextAsync(curator, "/Admin/Activity");
        Assert.Contains("Deleted the person for good", activity);
        Assert.DoesNotContain(s.Words.Name, activity);
        Assert.Contains("Deleted the person for good", await PeopleSeed.TextAsync(curator, "/Admin/Activity?what=People"));
    }

    [Fact]
    public async Task Only_a_person_in_the_bin_can_be_restored_or_deleted_for_good()
    {
        await using var archive = await PeopleSeed.NewArchiveAsync(app);
        var s = await SeedAsync(archive);
        using var curator = await CuratorAsync(archive);

        Assert.Equal(HttpStatusCode.NotFound, (await BinPostAsync(curator, "DeletePerson", s.PersonId)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await BinPostAsync(curator, "RestorePerson", s.PersonId)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await BinPostAsync(curator, "DeletePerson", 987654)).StatusCode);

        Assert.Null((await FindEvenInBinAsync(archive, s.PersonId))!.DeletedAt);
    }

    [Fact]
    public async Task A_person_stays_in_the_bin_for_30_days_and_is_then_purged_with_a_log_entry_without_a_name()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        await using var archive = await app.NewArchiveAsync(b => b.ConfigureTestServices(x => x.AddSingleton<TimeProvider>(clock)));
        var first = await SeedAsync(archive);
        var second = await SeedAsync(archive);
        var kept = await SeedAsync(archive);
        using (var curator = await CuratorAsync(archive))
        {
            await MoveToBinAsync(curator, first.PersonId);
            clock.Now = clock.Now.AddDays(10);
            await MoveToBinAsync(curator, second.PersonId);
        }

        async Task PurgeAsync()
        {
            using var scope = archive.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ArchiveBin>().PurgeExpiredAsync();
        }

        clock.Now = clock.Now.AddDays(19);
        await PurgeAsync();
        Assert.NotNull(await FindEvenInBinAsync(archive, first.PersonId));

        clock.Now = clock.Now.AddDays(1).AddHours(1);
        await PurgeAsync();

        Assert.Null(await FindEvenInBinAsync(archive, first.PersonId));
        Assert.NotNull(await FindEvenInBinAsync(archive, second.PersonId));
        Assert.NotNull(await FindEvenInBinAsync(archive, kept.PersonId));
        Assert.True(await archive.WithDbAsync(db => db.ArchiveItems.AnyAsync(i => i.Id == first.ItemId)));
        var purged = await archive.WithDbAsync(db => db.AuditEntries.SingleAsync(e => e.Action == AuditAction.PersonPurged));
        Assert.Equal((null, null, null, null), (purged.Subject, purged.UserName, purged.UserId, purged.PersonId));

        clock.Now = clock.Now.AddDays(10);
        await PurgeAsync();
        Assert.Null(await FindEvenInBinAsync(archive, second.PersonId));
        Assert.NotNull(await FindEvenInBinAsync(archive, kept.PersonId));
    }

    [Fact]
    public async Task A_helper_cannot_bin_restore_delete_or_move()
    {
        var s = await SeedAsync(app);
        using var admin = await CuratorAsync(app);
        var (helper, _, _, _) = await app.CreateAccountAsync(admin, Roles.Helper);
        using (helper)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await Browser.SendFormAsync(helper, $"{EditPage(s.PersonId)}?handler=MoveToBin", new(), "/Admin")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Browser.SendFormAsync(helper, $"/Admin/Bin?handler=RestorePerson&id={s.PersonId}", new(), "/Admin")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Browser.SendFormAsync(helper, $"/Admin/Bin?handler=DeletePerson&id={s.PersonId}", new(), "/Admin")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await helper.GetAsync($"/Admin/People/Move/{s.PersonId}?items={s.ItemId}")).StatusCode);
        }

        Assert.Null((await FindEvenInBinAsync(app, s.PersonId))!.DeletedAt);
    }
}
