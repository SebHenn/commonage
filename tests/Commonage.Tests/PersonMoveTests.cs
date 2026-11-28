using System.Net;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Tests;

/// <summary>The split (M30, part 4): ticked items go to another or a new person, and nothing else changes.</summary>
[Collection(nameof(AppCollection))]
public class PersonMoveTests(CommonageAppFactory app)
{
    private async Task<int> NewPersonAsync(string name, PersonDisclosure disclosure = PersonDisclosure.Hidden, params (string Name, bool Shown)[] otherNames)
    {
        return await app.WithDbAsync(async db =>
        {
            var person = db.AddHiddenPerson(name, DateTime.UtcNow);
            if (disclosure != PersonDisclosure.Hidden)
                person.SetDisclosure(disclosure, DateTime.UtcNow);
            foreach (var (other, shown) in otherNames)
                person.Names.Add(new PersonName { Name = other, Kind = PersonNameKind.Nickname, ShownToVisitors = shown });
            await db.SaveChangesAsync();
            return person.Id;
        });
    }

    private async Task LinkAsync(int personId, params int[] itemIds)
    {
        await app.WithDbAsync(async db =>
        {
            foreach (var itemId in itemIds)
                db.ItemPeople.Add(new ItemPerson { ArchiveItemId = itemId, PersonId = personId, CreatedAt = DateTime.UtcNow });
            return await db.SaveChangesAsync();
        });
    }

    private async Task<HttpClient> CuratorAsync()
    {
        var client = app.CreateClient();
        await Browser.LogInAsCuratorAsync(client);
        return client;
    }

    private static string Items(params int[] ids) => string.Join('&', ids.Select(i => $"items={i}"));

    private Task<List<int>> LinkedAsync(int personId) =>
        app.WithDbAsync(db => db.ItemPeople.IgnoreQueryFilters().Where(l => l.PersonId == personId).Select(l => l.ArchiveItemId).OrderBy(i => i).ToListAsync());

    [Fact]
    public async Task Moving_ticked_items_links_them_once_to_the_other_person_takes_names_along_hidden_and_saves_no_item()
    {
        var moved = await app.CreateContributionAsync(ItemStatus.Published);
        var shared = await app.CreateContributionAsync(ItemStatus.Published);
        var stays = await app.CreateContributionAsync(ItemStatus.Published);
        var carried = TestData.UniqueWord("Carried");
        var left = TestData.UniqueWord("Left");
        var source = await NewPersonAsync(TestData.UniqueWord("Source"), PersonDisclosure.Deceased, (carried, true), (left, true));
        var target = await NewPersonAsync(TestData.UniqueWord("Target"), PersonDisclosure.Deceased);
        await LinkAsync(source, moved.Id, shared.Id, stays.Id);
        await LinkAsync(target, shared.Id);
        var versions = await app.WithDbAsync(db => db.ArchiveItems.Where(i => i.Id == moved.Id || i.Id == shared.Id || i.Id == stays.Id).Select(i => new { i.Id, i.Version, i.UpdatedAt }).ToListAsync());
        var carriedId = await app.WithDbAsync(db => db.PersonNames.Where(n => n.Name == carried).Select(n => n.Id).SingleAsync());
        using var curator = await CuratorAsync();

        // The items to move are ticked on the person's page.
        var edit = await curator.GetStringAsync($"/Admin/People/Edit/{source}");
        Assert.Contains($"name=\"items\" value=\"{moved.Id}\"", edit);
        Assert.Contains("Move to bin", edit);

        var page = $"/Admin/People/Move/{source}?{Items(moved.Id, shared.Id)}&target={target}";
        var ask = await curator.GetStringAsync(page);
        Assert.Contains("1 item is moved.", ask);
        Assert.Contains("1 item is linked to the other person already", ask);
        Assert.DoesNotContain("Visitors will see more", ask);
        Assert.DoesNotContain("Visitors will see less", ask);
        Assert.Contains(carried, ask);

        var done = await Browser.PostFormAsync(curator, $"{page}&names={carriedId}", new(), page);

        Assert.EndsWith($"/Admin/People/Edit/{target}", done.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Equal([stays.Id], await LinkedAsync(source));
        Assert.Equal([moved.Id, shared.Id], await LinkedAsync(target));
        var name = await app.WithDbAsync(db => db.PersonNames.SingleAsync(n => n.PersonId == target));
        Assert.Equal((carried, false), (name.Name, name.ShownToVisitors));
        Assert.False(await app.WithDbAsync(db => db.PersonNames.AnyAsync(n => n.PersonId == target && n.Name == left)));
        // No item is saved: neither version nor change time moves.
        var after = await app.WithDbAsync(db => db.ArchiveItems.Where(i => i.Id == moved.Id || i.Id == shared.Id || i.Id == stays.Id).Select(i => new { i.Id, i.Version, i.UpdatedAt }).ToListAsync());
        Assert.Equal(versions.OrderBy(v => v.Id), after.OrderBy(v => v.Id));
        var entry = await app.WithDbAsync(db => db.AuditEntries.Where(e => e.Action == AuditAction.PersonItemsMoved && e.PersonId == source).SingleAsync());
        Assert.Null(entry.ArchiveItemId);
    }

    [Fact]
    public async Task Moving_to_a_new_person_makes_one_who_only_curators_see_and_the_page_warns_when_visitors_would_lose_the_person()
    {
        var item = await app.CreateContributionAsync(ItemStatus.Published);
        var source = await NewPersonAsync(TestData.UniqueWord("Shown"), PersonDisclosure.Deceased);
        await LinkAsync(source, item.Id);
        var newName = TestData.UniqueWord("Fresh");
        using var curator = await CuratorAsync();

        var page = $"/Admin/People/Move/{source}?{Items(item.Id)}&newName={newName}";
        var ask = await curator.GetStringAsync(page);
        Assert.Contains("Visitors will see less of the person on these items", ask);
        Assert.Contains("seen by curators only", ask);
        Assert.False(await app.WithDbAsync(db => db.People.AnyAsync(p => p.Name == newName)));

        await Browser.PostFormAsync(curator, page, new(), page);

        var created = await app.WithDbAsync(db => db.People.SingleAsync(p => p.Name == newName));
        Assert.Equal(PersonDisclosure.Hidden, created.Disclosure);
        Assert.False(created.NeedsReview);
        Assert.Equal([item.Id], await LinkedAsync(created.Id));
        Assert.Empty(await LinkedAsync(source));
        Assert.True(await app.WithDbAsync(db => db.AuditEntries.AnyAsync(e => e.Action == AuditAction.PersonCreated && e.PersonId == created.Id)));
    }

    [Fact]
    public async Task The_page_warns_when_the_other_person_is_shown_more_and_only_a_person_who_is_there_and_not_binned_can_be_the_target()
    {
        var item = await app.CreateContributionAsync(ItemStatus.Published);
        var hidden = await NewPersonAsync(TestData.UniqueWord("Hidden"));
        var shown = await NewPersonAsync(TestData.UniqueWord("Shown"), PersonDisclosure.Deceased);
        var binned = await NewPersonAsync(TestData.UniqueWord("Binned"));
        await app.WithDbAsync(async db =>
        {
            (await db.People.SingleAsync(p => p.Id == binned)).MoveToBin(DateTime.UtcNow);
            return await db.SaveChangesAsync();
        });
        await LinkAsync(hidden, item.Id);
        using var curator = await CuratorAsync();

        var ask = await curator.GetStringAsync($"/Admin/People/Move/{hidden}?{Items(item.Id)}&target={shown}");
        Assert.Contains("Visitors will see more of the person on these items", ask);

        Assert.Equal(HttpStatusCode.NotFound, (await curator.GetAsync($"/Admin/People/Move/{hidden}?{Items(item.Id)}&target={binned}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await curator.GetAsync($"/Admin/People/Move/{hidden}?{Items(item.Id)}&target={hidden}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await curator.GetAsync($"/Admin/People/Move/{hidden}?{Items(item.Id)}&target=987654")).StatusCode);
    }

    [Fact]
    public async Task Nothing_ticked_or_an_item_the_person_is_not_linked_to_moves_nothing_and_goes_back_to_the_person()
    {
        var other = await app.CreateContributionAsync(ItemStatus.Published);
        var source = await NewPersonAsync(TestData.UniqueWord("Source"));
        var target = await NewPersonAsync(TestData.UniqueWord("Target"));
        using var curator = await CuratorAsync();

        var none = await curator.GetAsync($"/Admin/People/Move/{source}?target={target}");
        var foreign = await curator.GetAsync($"/Admin/People/Move/{source}?{Items(other.Id)}&target={target}");

        Assert.EndsWith($"/Admin/People/Edit/{source}", none.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.EndsWith($"/Admin/People/Edit/{source}", foreign.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Empty(await LinkedAsync(target));
    }

    [Fact]
    public async Task Finding_the_target_by_name_or_number_leads_to_the_confirmation()
    {
        var item = await app.CreateContributionAsync(ItemStatus.Published);
        var source = await NewPersonAsync(TestData.UniqueWord("Source"));
        var targetName = TestData.UniqueWord("Findme");
        var target = await NewPersonAsync(targetName);
        await LinkAsync(source, item.Id);
        using var curator = await CuratorAsync();

        var byName = await curator.GetAsync($"/Admin/People/Move/{source}?{Items(item.Id)}&with={targetName}");
        var byNumber = await curator.GetAsync($"/Admin/People/Move/{source}?{Items(item.Id)}&with={target}");

        foreach (var found in new[] { byName, byNumber })
        {
            Assert.Contains($"target={target}", found.RequestMessage!.RequestUri!.Query);
            Assert.Contains($"value=\"{item.Id}\"", await found.Content.ReadAsStringAsync());
        }
    }
}
