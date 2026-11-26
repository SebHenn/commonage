using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Identity;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Tests;

/// <summary>People as curators handle them: who may see whom, other names, links to items, helpers kept out, and what is public.</summary>
[Collection(nameof(AppCollection))]
public partial class PersonTests(CommonageAppFactory app)
{
    private async Task<int> NewPersonAsync(string? name = null, Action<Person>? configure = null)
    {
        return await app.WithDbAsync(async db =>
        {
            var person = db.AddHiddenPerson(name ?? TestData.Unique("Person"), DateTime.UtcNow);
            configure?.Invoke(person);
            await db.SaveChangesAsync();
            return person.Id;
        });
    }

    private async Task<HttpClient> CuratorAsync()
    {
        var client = app.CreateClient();
        await Browser.LogInAsCuratorAsync(client);
        return client;
    }

    [Fact]
    public async Task A_new_person_is_seen_by_curators_only_and_gets_a_public_id_that_does_not_change()
    {
        using var curator = await CuratorAsync();
        var name = TestData.Unique("Created");

        await Browser.PostFormAsync(curator, "/Admin/People?handler=Create", new() { ["NewName"] = name }, "/Admin/People");

        var person = await app.WithDbAsync(db => db.People.SingleAsync(p => p.Name == name));
        Assert.Equal(PersonDisclosure.Hidden, person.Disclosure);
        Assert.Matches("^[0-9a-hjkmnp-tv-z]{12}$", person.PublicId);
        Assert.Contains(name, await curator.GetStringAsync("/Admin/People"));

        var page = $"/Admin/People/Edit/{person.Id}";
        await Browser.PostFormAsync(curator, page, new() { ["Input.Name"] = name + " II", ["Input.Version"] = person.Version.ToString() }, page);
        var renamed = await app.WithDbAsync(db => db.People.SingleAsync(p => p.Id == person.Id));
        Assert.Equal((name + " II", person.PublicId), (renamed.Name, renamed.PublicId));
    }

    [Fact]
    public async Task Showing_a_person_to_visitors_needs_a_second_step_that_says_what_visitors_will_see()
    {
        var id = await NewPersonAsync();
        using var curator = await CuratorAsync();
        var page = $"/Admin/People/Edit/{id}";

        var ask = await Browser.PostFormAsync(curator, $"{page}?handler=Disclosure", new() { ["choice"] = "Deceased" }, page);
        var stillHidden = await app.WithDbAsync(db => db.People.SingleAsync(p => p.Id == id));
        Assert.Contains("Yes, show this person", await ask.Content.ReadAsStringAsync());
        Assert.Equal(PersonDisclosure.Hidden, stillHidden.Disclosure);

        await Browser.PostFormAsync(curator, $"{page}?handler=Disclosure", new() { ["choice"] = "Deceased", ["confirmed"] = "true" }, page);
        Assert.Equal(PersonDisclosure.Deceased, (await app.WithDbAsync(db => db.People.SingleAsync(p => p.Id == id))).Disclosure);

        // Hidden again is one step, and needs no question.
        await Browser.PostFormAsync(curator, $"{page}?handler=Disclosure", new() { ["choice"] = "Hidden" }, page);
        Assert.Equal(PersonDisclosure.Hidden, (await app.WithDbAsync(db => db.People.SingleAsync(p => p.Id == id))).Disclosure);
    }

    [Fact]
    public async Task Another_curator_saving_first_is_not_overwritten()
    {
        var id = await NewPersonAsync(TestData.Unique("Shared"));
        using var curator = await CuratorAsync();
        var page = $"/Admin/People/Edit/{id}";
        var opened = await app.WithDbAsync(db => db.People.SingleAsync(p => p.Id == id));
        await app.WithDbAsync(async db =>
        {
            var person = await db.People.SingleAsync(p => p.Id == id);
            person.Biography = "Written by someone else";
            return await db.SaveChangesAsync();
        });

        var response = await Browser.PostFormAsync(curator, page, new() { ["Input.Name"] = opened.Name, ["Input.Biography"] = "Mine", ["Input.Version"] = opened.Version.ToString() }, page);

        Assert.Contains("Someone else changed this person", await response.Content.ReadAsStringAsync());
        Assert.Equal("Written by someone else", (await app.WithDbAsync(db => db.People.SingleAsync(p => p.Id == id))).Biography);
    }

    [Fact]
    public async Task Other_names_are_added_marked_for_visitors_and_removed_one_at_a_time()
    {
        var id = await NewPersonAsync();
        using var curator = await CuratorAsync();
        var page = $"/Admin/People/Edit/{id}";
        var other = TestData.Unique("Nick");

        await Browser.PostFormAsync(curator, $"{page}?handler=AddName", new() { ["name"] = other, ["kind"] = "Nickname", ["language"] = "de" }, page);
        var added = await app.WithDbAsync(db => db.PersonNames.SingleAsync(n => n.PersonId == id));
        Assert.Equal((other, PersonNameKind.Nickname, "de", false), (added.Name, added.Kind, added.Language, added.ShownToVisitors));

        await Browser.PostFormAsync(curator, $"{page}?handler=ShowName", new() { ["nameId"] = added.Id.ToString(), ["shown"] = "true" }, page);
        Assert.True((await app.WithDbAsync(db => db.PersonNames.SingleAsync(n => n.Id == added.Id))).ShownToVisitors);

        var invalid = await Browser.SendFormAsync(curator, $"{page}?handler=AddName", new() { ["name"] = "x", ["kind"] = "Other", ["language"] = "not a language code!" }, page);
        Assert.Equal(1, await app.WithDbAsync(db => db.PersonNames.CountAsync(n => n.PersonId == id)));
        Assert.True(invalid.IsSuccessStatusCode || invalid.StatusCode == HttpStatusCode.BadRequest);

        await Browser.PostFormAsync(curator, $"{page}?handler=RemoveName", new() { ["nameId"] = added.Id.ToString() }, page);
        Assert.Equal(0, await app.WithDbAsync(db => db.PersonNames.CountAsync(n => n.PersonId == id)));
    }

    [Fact]
    public async Task Adding_a_person_to_an_item_links_one_match_asks_about_several_and_makes_a_hidden_person_for_none()
    {
        var item = await app.CreateContributionAsync(ItemStatus.Published);
        var unique = TestData.Unique("Linkable");
        var single = await NewPersonAsync(unique);
        var twinName = TestData.Unique("Twin");
        await NewPersonAsync(twinName);
        await NewPersonAsync(twinName);
        using var curator = await CuratorAsync();
        var itemPage = $"/Admin/Items/Edit/{item.Id}";
        var addPage = $"/Admin/People/AddToItem/{item.Id}";
        var versionBefore = (await app.WithDbAsync(db => db.ArchiveItems.SingleAsync(i => i.Id == item.Id))).Version;

        // One match: linked at once, without a question.
        await Browser.PostFormAsync(curator, addPage, new() { ["Name"] = unique.ToUpperInvariant() }, addPage);
        Assert.Equal([single], await app.WithDbAsync(db => db.ItemPeople.Where(l => l.ArchiveItemId == item.Id).Select(l => l.PersonId).ToListAsync()));

        // Several: asks which one, and links nothing.
        var several = await Browser.PostFormAsync(curator, addPage, new() { ["Name"] = twinName }, addPage);
        Assert.Contains("More than one person has this name", await several.Content.ReadAsStringAsync());
        Assert.Equal(1, await app.WithDbAsync(db => db.ItemPeople.CountAsync(l => l.ArchiveItemId == item.Id)));

        // None: asks first, and only then makes a new person who only curators see.
        var fresh = TestData.Unique("Nobody");
        var ask = await Browser.PostFormAsync(curator, addPage, new() { ["Name"] = fresh }, addPage);
        Assert.Contains("Yes, add the person", await ask.Content.ReadAsStringAsync());
        Assert.False(await app.WithDbAsync(db => db.People.AnyAsync(p => p.Name == fresh)));
        await Browser.PostFormAsync(curator, $"{addPage}?handler=Create", new() { ["Name"] = fresh }, addPage);
        var made = await app.WithDbAsync(db => db.People.SingleAsync(p => p.Name == fresh));
        Assert.Equal(PersonDisclosure.Hidden, made.Disclosure);
        Assert.Contains(fresh, await curator.GetStringAsync(itemPage));

        // Taking one off removes the link only. Linking and unlinking never saved the item.
        await Browser.PostFormAsync(curator, $"{addPage}?handler=Remove", new() { ["personId"] = single.ToString() }, itemPage);
        Assert.Equal([made.Id], await app.WithDbAsync(db => db.ItemPeople.Where(l => l.ArchiveItemId == item.Id).Select(l => l.PersonId).ToListAsync()));
        Assert.True(await app.WithDbAsync(db => db.People.AnyAsync(p => p.Id == single)));
        Assert.Equal(versionBefore, (await app.WithDbAsync(db => db.ArchiveItems.SingleAsync(i => i.Id == item.Id))).Version);
    }

    [Fact]
    public async Task A_public_tag_with_the_same_name_is_pointed_out_but_never_turned_into_a_person()
    {
        var item = await app.CreateContributionAsync(ItemStatus.Published);
        var name = TestData.Unique("Taggy Person");
        await app.WithDbAsync(async db =>
        {
            var tagged = await db.ArchiveItems.Include(i => i.Tags).SingleAsync(i => i.Id == item.Id);
            tagged.SetTags([new Tag { Name = name, NormalizedName = Tag.Normalize(name), Kind = TagKind.Person }]);
            return await db.SaveChangesAsync();
        });
        using var curator = await CuratorAsync();
        var addPage = $"/Admin/People/AddToItem/{item.Id}";

        var ask = await Browser.PostFormAsync(curator, addPage, new() { ["Name"] = name }, addPage);

        Assert.Contains("A public tag with this name still exists", await ask.Content.ReadAsStringAsync());
        Assert.False(await app.WithDbAsync(db => db.People.AnyAsync(p => p.Name == name)));
    }

    [Fact]
    public async Task Helpers_get_nothing_of_people()
    {
        // An item of the archive's own: helpers do not see contributions at all.
        var item = await app.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            var own = new ArchiveItem { Title = TestData.Unique("Own item"), Collection = new Collection { Title = TestData.Unique("Own"), CreatedAt = now }, CreatedAt = now, UpdatedAt = now };
            db.ArchiveItems.Add(own);
            await db.SaveChangesAsync();
            return own;
        });
        var name = TestData.Unique("Linked");
        var id = await NewPersonAsync(name);
        await app.WithDbAsync(async db =>
        {
            db.ItemPeople.Add(new ItemPerson { ArchiveItemId = item.Id, PersonId = id, CreatedAt = DateTime.UtcNow });
            return await db.SaveChangesAsync();
        });
        using var admin = await CuratorAsync();
        var (helper, _, _, _) = await app.CreateAccountAsync(admin, Roles.Helper);

        var asCurator = await admin.GetStringAsync($"/Admin/Items/Edit/{item.Id}");
        var asHelper = await helper.GetStringAsync($"/Admin/Items/Edit/{item.Id}");

        Assert.Contains(name, asCurator);
        Assert.DoesNotContain(name, asHelper);
        Assert.DoesNotContain("Add a person", asHelper);
        foreach (var url in new[] { "/Admin/People", $"/Admin/People/Edit/{id}", $"/Admin/People/Preview/{id}", $"/Admin/People/AddToItem/{item.Id}" })
            Assert.Equal(HttpStatusCode.Forbidden, (await helper.GetAsync(url)).StatusCode);
        var post = await Browser.SendFormAsync(helper, $"/Admin/People/AddToItem/{item.Id}", new() { ["Name"] = "Made by a helper" }, $"/Admin/Items/Edit/{item.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        Assert.False(await app.WithDbAsync(db => db.People.AnyAsync(p => p.Name == "Made by a helper")));
        Assert.Equal(HttpStatusCode.Forbidden, (await helper.GetAsync("/Admin/People?handler=Suggest&q=a")).StatusCode);
    }

    [Fact]
    public async Task Visitors_cannot_reach_the_pages_for_people()
    {
        var id = await NewPersonAsync();
        using var visitor = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        foreach (var url in new[] { "/Admin/People", $"/Admin/People/Edit/{id}", $"/Admin/People/Preview/{id}", "/Admin/People?handler=Suggest&q=a" })
        {
            var response = await visitor.GetAsync(url);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("/Account/Login", response.Headers.Location!.ToString());
        }
    }

    [Fact]
    public async Task The_preview_says_why_visitors_cannot_see_a_person()
    {
        var hidden = await NewPersonAsync();
        var deceasedWithoutItem = await NewPersonAsync(configure: p => p.SetDisclosure(PersonDisclosure.Deceased, DateTime.UtcNow));
        using var curator = await CuratorAsync();

        Assert.Contains("Only curators see this person", await curator.GetStringAsync($"/Admin/People/Preview/{hidden}"));
        Assert.Contains("None of this person", await curator.GetStringAsync($"/Admin/People/Preview/{deceasedWithoutItem}"));
    }

    [Fact]
    public async Task The_projection_for_visitors_holds_a_person_only_while_a_published_item_names_them()
    {
        var published = await app.CreateContributionAsync(ItemStatus.Published);
        var draft = await app.CreateContributionAsync(ItemStatus.Draft);
        var shownName = TestData.Unique("Shown");
        var secretNote = TestData.Unique("note");
        var secretOther = TestData.Unique("secretname");
        var (visible, hiddenOne, alone, draftOnly) = (await NewPersonAsync(shownName, p =>
        {
            p.SetDisclosure(PersonDisclosure.Deceased, DateTime.UtcNow);
            p.CuratorNote = secretNote;
            p.Names.Add(new PersonName { Name = "Seen other", Kind = PersonNameKind.Nickname, ShownToVisitors = true });
            p.Names.Add(new PersonName { Name = secretOther, Kind = PersonNameKind.BirthName, ShownToVisitors = false });
        }), await NewPersonAsync(), await NewPersonAsync(configure: p => p.SetDisclosure(PersonDisclosure.Deceased, DateTime.UtcNow)),
            await NewPersonAsync(configure: p => p.SetDisclosure(PersonDisclosure.Deceased, DateTime.UtcNow)));
        await app.WithDbAsync(async db =>
        {
            db.ItemPeople.AddRange(
                new ItemPerson { ArchiveItemId = published.Id, PersonId = visible, CreatedAt = DateTime.UtcNow },
                new ItemPerson { ArchiveItemId = published.Id, PersonId = hiddenOne, CreatedAt = DateTime.UtcNow },
                new ItemPerson { ArchiveItemId = draft.Id, PersonId = draftOnly, CreatedAt = DateTime.UtcNow });
            return await db.SaveChangesAsync();
        });

        var ids = await app.WithDbAsync(async db =>
        {
            var today = db.Today;
            return await Visibility.Person(db, today).Select(p => p.Id).ToListAsync();
        });
        var view = await app.WithDbAsync(async db =>
        {
            var publicId = (await db.People.SingleAsync(p => p.Id == visible)).PublicId;
            return await VisiblePeople.ViewAsync(db, Visibility.Person(db, db.Today), publicId, CancellationToken.None);
        });

        Assert.Contains(visible, ids);
        Assert.DoesNotContain(hiddenOne, ids);
        Assert.DoesNotContain(alone, ids);
        Assert.DoesNotContain(draftOnly, ids);
        Assert.NotNull(view);
        Assert.Equal(shownName, view!.Name);
        Assert.Equal(["Seen other"], view.OtherNames.Select(n => n.Name));
        var json = JsonSerializer.Serialize(view);
        Assert.DoesNotContain(secretNote, json);
        Assert.DoesNotContain(secretOther, json);
    }

    [Fact]
    public async Task Curators_find_people_in_the_personal_data_search_by_name_other_name_or_note_even_in_the_bin()
    {
        var token = Guid.NewGuid().ToString("N")[..10];
        await NewPersonAsync($"Byname{token}");
        await NewPersonAsync(TestData.Unique("Other"), p => p.Names.Add(new PersonName { Name = $"Known as {token}x", Kind = PersonNameKind.Other }));
        await NewPersonAsync(TestData.Unique("Noted"), p => p.CuratorNote = $"Asked {token}y to stay hidden");
        await NewPersonAsync($"Binned{token}", p => p.MoveToBin(DateTime.UtcNow));
        using var curator = await CuratorAsync();

        var html = await curator.GetStringAsync($"/Admin/Contributors?q={token}");

        Assert.Contains($"Byname{token}", html);
        Assert.Contains($"Binned{token}", html);
        Assert.Equal(4, Regex.Matches(html, @"/Admin/People/Edit/\d+").Select(m => m.Value).Distinct().Count());
    }

    [Fact]
    public async Task Every_change_to_a_person_is_in_the_activity_log_without_the_name()
    {
        var name = TestData.Unique("Logged");
        using var curator = await CuratorAsync();
        await Browser.PostFormAsync(curator, "/Admin/People?handler=Create", new() { ["NewName"] = name }, "/Admin/People");
        var id = (await app.WithDbAsync(db => db.People.SingleAsync(p => p.Name == name))).Id;

        var entries = await app.WithDbAsync(db => db.AuditEntries.Where(e => e.PersonId == id).ToListAsync());

        Assert.Equal([AuditAction.PersonCreated], entries.Select(e => e.Action));
        Assert.DoesNotContain(name, string.Join(' ', entries.Select(e => e.Subject)));
    }
}
