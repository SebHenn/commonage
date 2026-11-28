using System.Net;
using Commonage.Web.Data;
using Commonage.Web.Audit;
using Commonage.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Tests;

/// <summary>"This is the same person as ...", and the review list of people carried over from tags (M30, part 3).</summary>
[Collection(nameof(AppCollection))]
public class PersonMergeTests(CommonageAppFactory app)
{
    private async Task<int> NewPersonAsync(string? name = null, PersonDisclosure disclosure = PersonDisclosure.Hidden, bool unreviewed = false)
    {
        return await app.WithDbAsync(async db =>
        {
            var person = db.AddHiddenPerson(name ?? TestData.Unique("Person"), DateTime.UtcNow);
            if (disclosure != PersonDisclosure.Hidden)
                person.SetDisclosure(disclosure, DateTime.UtcNow);
            await db.SaveChangesAsync();
            if (unreviewed)
                await db.People.Where(p => p.Id == person.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.DisclosureChangedAt, (DateTime?)null));
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

    private Task<Person> ReadAsync(int id) => app.WithDbAsync(db => db.People.IgnoreQueryFilters().Include(p => p.Names).SingleAsync(p => p.Id == id));

    [Fact]
    public async Task Merging_links_every_item_once_the_bin_included_and_carries_the_names_without_showing_them()
    {
        var shared = await app.CreateContributionAsync(ItemStatus.Published);
        var onlySource = await app.CreateContributionAsync(ItemStatus.Draft);
        var binned = await app.CreateContributionAsync(ItemStatus.Published);
        await app.WithDbAsync(async db =>
        {
            (await db.ArchiveItems.SingleAsync(i => i.Id == binned.Id)).MoveToBin(DateTime.UtcNow);
            return await db.SaveChangesAsync();
        });
        var sourceName = TestData.UniqueWord("Sourcename");
        var source = await NewPersonAsync(sourceName, PersonDisclosure.Deceased);
        var target = await NewPersonAsync(TestData.UniqueWord("Targetname"), PersonDisclosure.Deceased);
        await LinkAsync(source, shared.Id, onlySource.Id, binned.Id);
        await LinkAsync(target, shared.Id);
        await app.WithDbAsync(async db =>
        {
            (await db.People.Include(p => p.Names).SingleAsync(p => p.Id == source)).Names.Add(new PersonName { Name = TestData.UniqueWord("Nick"), Kind = PersonNameKind.Nickname, ShownToVisitors = true });
            return await db.SaveChangesAsync();
        });
        using var curator = await CuratorAsync();

        var page = $"/Admin/People/Merge/{source}?target={target}";
        var ask = await curator.GetStringAsync(page);
        Assert.Contains("2 items are linked to the other person.", ask);
        Assert.Contains("2 names are added as other names of the other person.", ask);
        Assert.Equal(PersonDisclosure.Deceased, (await ReadAsync(source)).Disclosure);

        await Browser.PostFormAsync(curator, page, new(), page);

        var merged = await ReadAsync(target);
        Assert.Equal(PersonDisclosure.Deceased, merged.Disclosure);
        Assert.Equal([shared.Id, onlySource.Id, binned.Id], await app.WithDbAsync(db => db.ItemPeople.IgnoreQueryFilters().Where(l => l.PersonId == target).Select(l => l.ArchiveItemId).OrderBy(i => i).ToListAsync()));
        Assert.Contains(sourceName, merged.Names.Select(n => n.Name));
        Assert.Equal(2, merged.Names.Count);
        Assert.All(merged.Names, n => Assert.False(n.ShownToVisitors));

        var gone = await ReadAsync(source);
        Assert.NotNull(gone.DeletedAt);
        Assert.Equal(PersonDisclosure.Hidden, gone.Disclosure);
        // The links stay on the binned person, hidden by the bin's filter, and come back with a restore.
        Assert.Equal(3, await app.WithDbAsync(db => db.ItemPeople.IgnoreQueryFilters().CountAsync(l => l.PersonId == source)));
        Assert.Empty(await app.WithDbAsync(db => db.ItemPeople.Where(l => l.PersonId == source).ToListAsync()));
        await app.WithDbAsync(async db =>
        {
            (await db.People.IgnoreQueryFilters().SingleAsync(p => p.Id == source)).RestoreFromBin(DateTime.UtcNow);
            return await db.SaveChangesAsync();
        });
        // The item in the bin keeps hiding its own link.
        Assert.Equal(2, await app.WithDbAsync(db => db.ItemPeople.CountAsync(l => l.PersonId == source)));
        var entries = await app.WithDbAsync(db => db.AuditEntries.Where(e => e.Action == AuditAction.PeopleMerged && e.PersonId == target).ToListAsync());
        Assert.Single(entries);
    }

    /// <summary>
    /// Merging never takes an item past the limit of people: the target takes the place of the source, whose links the bin's filter then hides,
    /// and an item that has both keeps one link. So the check that adding a person makes needs no second copy in the merge.
    /// </summary>
    [Fact]
    public async Task Merging_leaves_an_item_with_a_full_list_of_people_at_the_limit_and_never_over_it()
    {
        var full = await app.CreateContributionAsync(ItemStatus.Published);
        var both = await app.CreateContributionAsync(ItemStatus.Published);
        var source = await NewPersonAsync();
        var target = await NewPersonAsync();
        var others = await app.WithDbAsync(async db =>
        {
            var made = Enumerable.Range(0, Person.MaxPerItem - 1).Select(_ => db.AddHiddenPerson(TestData.Unique("Crowd"), DateTime.UtcNow)).ToList();
            await db.SaveChangesAsync();
            return made.Select(p => p.Id).ToList();
        });
        await LinkAsync(source, full.Id, both.Id);
        await LinkAsync(target, both.Id);
        foreach (var person in others)
            await LinkAsync(person, full.Id);
        await app.WithDbAsync(async db =>
        {
            // The other item has the source, the target and 198 more.
            foreach (var person in others.Take(Person.MaxPerItem - 2))
                db.ItemPeople.Add(new ItemPerson { ArchiveItemId = both.Id, PersonId = person, CreatedAt = DateTime.UtcNow });
            return await db.SaveChangesAsync();
        });
        Assert.Equal(Person.MaxPerItem, await app.WithDbAsync(db => db.ItemPeople.CountAsync(l => l.ArchiveItemId == full.Id)));
        Assert.Equal(Person.MaxPerItem, await app.WithDbAsync(db => db.ItemPeople.CountAsync(l => l.ArchiveItemId == both.Id)));
        using var curator = await CuratorAsync();

        var page = $"/Admin/People/Merge/{source}?target={target}";
        await Browser.PostFormAsync(curator, page, new(), page);

        Assert.Equal(Person.MaxPerItem, await app.WithDbAsync(db => db.ItemPeople.CountAsync(l => l.ArchiveItemId == full.Id)));
        Assert.Equal(Person.MaxPerItem - 1, await app.WithDbAsync(db => db.ItemPeople.CountAsync(l => l.ArchiveItemId == both.Id)));
        Assert.Equal([full.Id, both.Id], await app.WithDbAsync(db => db.ItemPeople.Where(l => l.PersonId == target).Select(l => l.ArchiveItemId).OrderBy(i => i).ToListAsync()));
        Assert.Empty(await app.WithDbAsync(db => db.ItemPeople.Where(l => l.PersonId == source).ToListAsync()));
    }

    [Fact]
    public async Task After_a_merge_the_visible_item_names_the_target_once_and_the_binned_person_nowhere()
    {
        var item = await app.CreateContributionAsync(ItemStatus.Published);
        var sourceName = TestData.UniqueWord("Mergedaway");
        var targetName = TestData.UniqueWord("Mergedinto");
        var source = await NewPersonAsync(sourceName, PersonDisclosure.Deceased);
        var target = await NewPersonAsync(targetName, PersonDisclosure.Deceased);
        await LinkAsync(source, item.Id);
        using var curator = await CuratorAsync();
        var page = $"/Admin/People/Merge/{source}?target={target}";
        await Browser.PostFormAsync(curator, page, new(), page);

        var visitor = app.CreateClient();
        var html = await visitor.GetStringAsync($"/items/{item.Id}");
        Assert.Contains(targetName, html);
        Assert.DoesNotContain(sourceName, html);
        Assert.DoesNotContain(sourceName, await visitor.GetStringAsync("/people"));
        Assert.DoesNotContain($"/items/{item.Id}\"", await visitor.GetStringAsync($"/search?q={sourceName}"));
        Assert.Equal(1, await app.WithDbAsync(db => db.VisiblePeople.CountAsync(p => p.Id == source || p.Id == target)));
    }

    [Fact]
    public async Task The_stricter_choice_wins_and_the_page_says_so_before_anything_happens()
    {
        var source = await NewPersonAsync(disclosure: PersonDisclosure.Hidden);
        var target = await NewPersonAsync(disclosure: PersonDisclosure.Deceased);
        using var curator = await CuratorAsync();
        var page = $"/Admin/People/Merge/{source}?target={target}";

        var ask = await curator.GetStringAsync(page);
        Assert.Contains("Visitors will no longer see the other person", ask);
        Assert.Equal(PersonDisclosure.Deceased, (await ReadAsync(target)).Disclosure);

        await Browser.PostFormAsync(curator, page, new(), page);
        Assert.Equal(PersonDisclosure.Hidden, (await ReadAsync(target)).Disclosure);

        // The other way round the target stays as it is, and nothing is said about a stricter choice.
        var second = await NewPersonAsync(disclosure: PersonDisclosure.Deceased);
        var third = await NewPersonAsync(disclosure: PersonDisclosure.Deceased);
        var calm = await curator.GetStringAsync($"/Admin/People/Merge/{second}?target={third}");
        Assert.DoesNotContain("Visitors will no longer see", calm);
        Assert.DoesNotContain("becomes stricter", calm);
    }

    [Fact]
    public async Task A_hidden_target_and_a_target_in_the_bin_are_pointed_out_and_the_result_stays_in_the_bin()
    {
        var source = await NewPersonAsync(disclosure: PersonDisclosure.Deceased);
        var hiddenTarget = await NewPersonAsync();
        var binTarget = await NewPersonAsync(disclosure: PersonDisclosure.Deceased);
        await app.WithDbAsync(async db =>
        {
            (await db.People.SingleAsync(p => p.Id == binTarget)).MoveToBin(DateTime.UtcNow);
            return await db.SaveChangesAsync();
        });
        using var curator = await CuratorAsync();

        Assert.Contains("The other person is only seen by curators.", await curator.GetStringAsync($"/Admin/People/Merge/{source}?target={hiddenTarget}"));
        var page = $"/Admin/People/Merge/{source}?target={binTarget}";
        Assert.Contains("The other person is in the bin.", await curator.GetStringAsync(page));

        await Browser.PostFormAsync(curator, page, new(), page);
        Assert.NotNull((await ReadAsync(binTarget)).DeletedAt);
    }

    [Fact]
    public async Task The_other_person_is_found_by_number_or_by_name_and_several_matches_are_listed()
    {
        var source = await NewPersonAsync();
        var name = TestData.UniqueWord("Samename");
        var one = await NewPersonAsync(name);
        var two = await NewPersonAsync(name);
        var lone = await NewPersonAsync(TestData.UniqueWord("Lonename"));
        using var curator = await CuratorAsync();
        var page = $"/Admin/People/Merge/{source}";

        var byNumber = await curator.GetAsync($"{page}?with={lone}");
        Assert.EndsWith($"target={lone}", byNumber.RequestMessage!.RequestUri!.Query);

        var list = await curator.GetStringAsync($"{page}?with={name}");
        Assert.Contains($"target={one}", list);
        Assert.Contains($"target={two}", list);

        Assert.Contains("No other person matches.", await curator.GetStringAsync($"{page}?with={TestData.UniqueWord("Nobody")}"));
        // This person itself is not offered.
        Assert.Contains("No other person matches.", await curator.GetStringAsync($"{page}?with={source}"));
    }

    [Fact]
    public async Task A_person_cannot_be_merged_into_itself_and_a_missing_person_is_not_found()
    {
        var source = await NewPersonAsync();
        using var curator = await CuratorAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await curator.GetAsync($"/Admin/People/Merge/{source}?target={source}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await curator.GetAsync($"/Admin/People/Merge/{source}?target=999999999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await curator.GetAsync("/Admin/People/Merge/999999999")).StatusCode);
        var post = await Browser.SendFormAsync(curator, $"/Admin/People/Merge/{source}?target={source}", new(), $"/Admin/People/Merge/{source}");
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        Assert.Null((await ReadAsync(source)).DeletedAt);
    }

    [Fact]
    public async Task Visitors_cannot_reach_the_merge_page()
    {
        var source = await NewPersonAsync();
        var visitor = app.CreateClient(new() { AllowAutoRedirect = false });
        var response = await visitor.GetAsync($"/Admin/People/Merge/{source}");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Review_list_shows_only_unreviewed_people_and_the_dashboard_counts_them_by_what_visitors_get()
    {
        var shown = await NewPersonAsync(TestData.UniqueWord("Reviewshown"), PersonDisclosure.MayBeShown, unreviewed: true);
        var kept = await NewPersonAsync(TestData.UniqueWord("Reviewkept"), unreviewed: true);
        var chosen = await NewPersonAsync(TestData.UniqueWord("Reviewchosen"));
        var (shownName, keptName, chosenName) = await app.WithDbAsync(async db => (
            (await db.People.SingleAsync(p => p.Id == shown)).Name, (await db.People.SingleAsync(p => p.Id == kept)).Name, (await db.People.SingleAsync(p => p.Id == chosen)).Name));
        using var curator = await CuratorAsync();

        var list = await curator.GetStringAsync("/Admin/People?review=1");
        Assert.Contains(shownName, list);
        Assert.Contains(keptName, list);
        Assert.DoesNotContain(chosenName, list);

        var dashboard = await curator.GetStringAsync("/Admin");
        Assert.Contains("People from former tags", dashboard);
        Assert.Contains("href=\"/Admin/People?review=1\"", dashboard);
        Assert.Matches(@"\d+ shown without review", dashboard);
        Assert.Matches(@"\d+ kept hidden", dashboard);
    }

    [Fact]
    public async Task Bulk_review_hidden_is_at_once_and_deceased_asks_first_and_only_unreviewed_people_change()
    {
        var a = await NewPersonAsync(unreviewed: true);
        var b = await NewPersonAsync(unreviewed: true);
        var alreadyChosen = await NewPersonAsync(disclosure: PersonDisclosure.Deceased);
        using var curator = await CuratorAsync();
        var page = "/Admin/People?review=1";
        string[] both = [a.ToString(), b.ToString(), alreadyChosen.ToString()];

        // The ask: nothing changes, and the answer lists the people.
        var form = new FormUrlEncodedContent(both.Select(id => KeyValuePair.Create("selected", id)).Append(KeyValuePair.Create("choice", "Deceased")).Append(KeyValuePair.Create("__RequestVerificationToken", await Browser.GetAntiforgeryTokenAsync(curator, page))));
        var ask = await curator.PostAsync("/Admin/People?review=1&handler=BulkReview", form);
        ask.EnsureSuccessStatusCode();
        Assert.Contains("Yes, show them", await ask.Content.ReadAsStringAsync());
        Assert.Equal(2, await app.WithDbAsync(db => db.People.CountAsync(p => (p.Id == a || p.Id == b) && p.DisclosureChangedAt == null)));
        Assert.Equal(0, await app.WithDbAsync(db => db.AuditEntries.CountAsync(e => e.PersonId == a || e.PersonId == b)));

        var confirmed = new FormUrlEncodedContent(both.Select(id => KeyValuePair.Create("selected", id)).Concat([KeyValuePair.Create("choice", "Deceased"), KeyValuePair.Create("confirmed", "true"), KeyValuePair.Create("__RequestVerificationToken", await Browser.GetAntiforgeryTokenAsync(curator, page))]));
        (await curator.PostAsync("/Admin/People?review=1&handler=BulkReview", confirmed)).EnsureSuccessStatusCode();

        Assert.Equal(PersonDisclosure.Deceased, (await ReadAsync(a)).Disclosure);
        Assert.Equal(PersonDisclosure.Deceased, (await ReadAsync(b)).Disclosure);
        Assert.NotNull((await ReadAsync(a)).DisclosureChangedAt);
        Assert.Equal(1, await app.WithDbAsync(db => db.AuditEntries.CountAsync(e => e.Action == AuditAction.PersonDisclosureChanged && e.PersonId == a)));
        Assert.Equal(1, await app.WithDbAsync(db => db.AuditEntries.CountAsync(e => e.Action == AuditAction.PersonDisclosureChanged && e.PersonId == b)));
        Assert.Equal(0, await app.WithDbAsync(db => db.AuditEntries.CountAsync(e => e.PersonId == alreadyChosen && e.Action == AuditAction.PersonDisclosureChanged)));

        // "Only curators" needs no question, and counts as the person's own choice.
        var c = await NewPersonAsync(unreviewed: true);
        var hidden = new FormUrlEncodedContent([KeyValuePair.Create("selected", c.ToString()), KeyValuePair.Create("choice", "Hidden"), KeyValuePair.Create("__RequestVerificationToken", await Browser.GetAntiforgeryTokenAsync(curator, page))]);
        (await curator.PostAsync("/Admin/People?review=1&handler=BulkReview", hidden)).EnsureSuccessStatusCode();
        var reviewed = await ReadAsync(c);
        Assert.Equal(PersonDisclosure.Hidden, reviewed.Disclosure);
        Assert.NotNull(reviewed.DisclosureChangedAt);
    }
}
