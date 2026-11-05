using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Commonage.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Tests;

[Collection(nameof(AppCollection))]
public class CatalogueTests(CommonageAppFactory app)
{
    private async Task<HttpClient> CuratorAsync()
    {
        var curator = app.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);
        return curator;
    }

    private static Dictionary<string, string> Fields(ArchiveItem item, params (string Key, string Value)[] more)
    {
        var fields = new Dictionary<string, string>
        {
            ["Input.Title"] = item.Title,
            ["Input.CollectionId"] = item.CollectionId.ToString(),
        };
        foreach (var (key, value) in more)
            fields[key] = value;
        return fields;
    }

    private Task<ArchiveItem> ReloadAsync(int id) =>
        app.WithDbAsync(db => db.ArchiveItems.AsNoTracking().Include(i => i.Contribution).SingleAsync(i => i.Id == id));

    [Fact]
    public async Task Catalogue_details_are_saved_and_stay_away_from_visitors_and_the_public_search()
    {
        var item = await app.CreateContributionAsync(ItemStatus.Published);
        var secret = "Secretword" + Guid.NewGuid().ToString("N");
        using var curator = await CuratorAsync();

        await Browser.PostFormAsync(curator, $"/Admin/Items/Edit/{item.Id}", Fields(item,
            ("Input.RightsUri", RightsOptions.InCopyright),
            ("Input.RightsHolder", secret),
            ("Input.ReferenceNumber", TestData.Unique("R")[..30]),
            ("Input.OriginalLocation", secret),
            ("Input.ReceivedFrom", secret),
            ("Input.ReceivedOn", "summer 2019"),
            ("Input.InternalNotes", secret)));

        var saved = await ReloadAsync(item.Id);
        Assert.Equal(RightsOptions.InCopyright, saved.RightsUri);
        Assert.Equal("summer 2019", saved.ReceivedOn);

        using var visitor = app.CreateClient();
        Assert.DoesNotContain(secret, await visitor.GetStringAsync($"/items/{item.Id}"));
        Assert.Contains("Items found: 0", await visitor.GetStringAsync($"/search?q={secret}"));
        Assert.Contains(secret, await curator.GetStringAsync($"/Admin/Items/Edit/{item.Id}"));
    }

    [Fact]
    public async Task A_reference_number_cannot_be_used_twice_and_finds_its_item_in_the_list()
    {
        var first = await app.CreateContributionAsync(ItemStatus.Draft);
        var second = await app.CreateContributionAsync(ItemStatus.Draft);
        var number = "REF-" + Guid.NewGuid().ToString("N")[..12];
        using var curator = await CuratorAsync();

        await Browser.PostFormAsync(curator, $"/Admin/Items/Edit/{first.Id}", Fields(first, ("Input.ReferenceNumber", number)));
        var refused = await Browser.SendFormAsync(curator, $"/Admin/Items/Edit/{second.Id}", Fields(second, ("Input.ReferenceNumber", number)));
        var list = await curator.GetStringAsync($"/Admin?q={number}");

        Assert.Contains("already used by", await refused.Content.ReadAsStringAsync());
        Assert.Null((await ReloadAsync(second.Id)).ReferenceNumber);
        Assert.Contains(first.Title, list);
        Assert.DoesNotContain(second.Title, list);
    }

    [Fact]
    public async Task An_unknown_rights_statement_is_refused()
    {
        var item = await app.CreateContributionAsync(ItemStatus.Draft);
        using var curator = await CuratorAsync();

        var response = await Browser.SendFormAsync(curator, $"/Admin/Items/Edit/{item.Id}", Fields(item, ("Input.RightsUri", "https://example.org/whatever")));

        Assert.Contains("Please choose one of the statements", await response.Content.ReadAsStringAsync());
        Assert.Null((await ReloadAsync(item.Id)).RightsUri);
    }

    [Theory]
    [InlineData(RightsStatement.CreatedByContributor, RightsOptions.InCopyright)]
    [InlineData(RightsStatement.PermissionFromRightsHolder, RightsOptions.InCopyright)]
    [InlineData(RightsStatement.Unknown, RightsOptions.NotEvaluated)]
    public void A_contributors_answer_never_suggests_a_free_licence(RightsStatement answer, string expected)
    {
        Assert.Equal(expected, RightsOptions.ProposalFor(answer));
    }

    [Fact]
    public async Task A_contribution_is_offered_a_suggestion_but_nothing_is_public_or_saved_until_a_curator_saves()
    {
        var item = await app.CreateContributionAsync(ItemStatus.Submitted);
        using var curator = await CuratorAsync();

        var page = await curator.GetStringAsync($"/Admin/Items/Edit/{item.Id}");

        var uri = System.Text.RegularExpressions.Regex.Escape(RightsOptions.InCopyright);
        Assert.Matches($"<option[^>]*selected[^>]*{uri}|<option[^>]*{uri}[^>]*selected", page);
        Assert.Contains("Suggested from what the contributor answered", page);
        Assert.Null((await ReloadAsync(item.Id)).RightsUri);
        // The credit is not filled in from the contributor's photographer: a name given for one purpose is not public by itself.
        Assert.Null((await ReloadAsync(item.Id)).Credit);
        Assert.Contains("The contributor named as photographer or author: Heinz Muster", page);
    }

    [Fact]
    public async Task A_photos_description_is_saved_with_the_item_and_describes_the_picture_for_visitors()
    {
        var item = await app.CreateContributionAsync(ItemStatus.Published);
        using var curator = await CuratorAsync();
        await Browser.UploadAsync(curator, $"/Admin/Items/Edit/{item.Id}", "Upload", "flood.jpg", TestData.Photo);
        var photo = await app.WithDbAsync(db => db.MediaFiles.Where(f => f.ArchiveItemId == item.Id).Select(f => f.Id).SingleAsync());
        for (var i = 0; i < 100 && await app.WithDbAsync(db => db.MediaFiles.AnyAsync(f => f.Id == photo && f.Status != ProcessingStatus.Ready)); i++)
            await Task.Delay(100);

        await Browser.PostFormAsync(curator, $"/Admin/Items/Edit/{item.Id}", Fields(item, ($"Input.AltTexts[{photo}]", "Two boats in a flooded street")));

        using var visitor = app.CreateClient();
        Assert.Contains("alt=\"Two boats in a flooded street\"", await visitor.GetStringAsync($"/items/{item.Id}"));
        Assert.Contains("Two boats in a flooded street", await curator.GetStringAsync($"/Admin/Items/Edit/{item.Id}"));
    }

    [Fact]
    public async Task A_save_without_photo_descriptions_leaves_them_alone()
    {
        var item = await app.CreateContributionAsync(ItemStatus.Draft);
        using var curator = await CuratorAsync();
        await Browser.UploadAsync(curator, $"/Admin/Items/Edit/{item.Id}", "Upload", "flood.jpg", TestData.Photo);
        var photo = await app.WithDbAsync(db => db.MediaFiles.Where(f => f.ArchiveItemId == item.Id).Select(f => f.Id).SingleAsync());
        await Browser.PostFormAsync(curator, $"/Admin/Items/Edit/{item.Id}", Fields(item, ($"Input.AltTexts[{photo}]", "Kept")));

        await Browser.PostFormAsync(curator, $"/Admin/Items/Edit/{item.Id}", Fields(item));

        Assert.Equal("Kept", await app.WithDbAsync(db => db.MediaFiles.Where(f => f.Id == photo).Select(f => f.AltText).SingleAsync()));
    }

    [Fact]
    public async Task The_list_finds_what_still_needs_a_rights_statement_or_a_photo_description()
    {
        var undecided = await app.CreateContributionAsync(ItemStatus.Draft);
        var decided = await app.CreateContributionAsync(ItemStatus.Draft);
        using var curator = await CuratorAsync();
        await Browser.PostFormAsync(curator, $"/Admin/Items/Edit/{decided.Id}", Fields(decided, ("Input.RightsUri", RightsOptions.NotEvaluated)));
        await Browser.UploadAsync(curator, $"/Admin/Items/Edit/{undecided.Id}", "Upload", "flood.jpg", TestData.Photo);

        var needsRights = await curator.GetStringAsync($"/Admin?collection={undecided.CollectionId}&needs=Rights");
        var decidedPage = await curator.GetStringAsync($"/Admin?collection={decided.CollectionId}&needs=Rights");
        var needsAlt = await curator.GetStringAsync($"/Admin?collection={undecided.CollectionId}&needs=AltText");

        Assert.Contains(undecided.Title, needsRights);
        Assert.DoesNotContain(decided.Title, decidedPage);
        Assert.Contains(undecided.Title, needsAlt);
    }

    [Fact]
    public async Task A_curator_can_correct_a_contributors_details_and_the_log_says_only_that_it_happened()
    {
        var item = await app.CreateContributionAsync(ItemStatus.Draft, name: "Erika Mustr");
        var page = $"/Admin/Items/Edit/{item.Id}";
        using var curator = await CuratorAsync();

        await Browser.PostFormAsync(curator, $"{page}?handler=ContributorDetails", new()
        {
            ["contributorName"] = "Erika Muster",
            ["contributorEmail"] = "erika.new@example.org",
            ["contributorRightsHolder"] = "Heinz Muster",
        }, page);

        var contribution = (await ReloadAsync(item.Id)).Contribution!;
        Assert.Equal(("Erika Muster", "erika.new@example.org", false), (contribution.ContributorName, contribution.ContributorEmail, contribution.ShowName));
        var entry = await app.WithDbAsync(db => db.AuditEntries.SingleAsync(e => e.ArchiveItemId == item.Id && e.Action == AuditAction.ContributorDetailsChanged));
        Assert.Equal(item.Title, entry.Subject);
    }

    [Theory]
    [InlineData("", "a@example.org", "Please enter a name")]
    [InlineData("Erika", "not an address", "Please enter a valid email address")]
    public async Task Wrong_contributor_details_are_refused_and_change_nothing(string name, string email, string message)
    {
        var item = await app.CreateContributionAsync(ItemStatus.Draft, name: "Erika Original");
        var page = $"/Admin/Items/Edit/{item.Id}";
        using var curator = await CuratorAsync();

        var response = await Browser.PostFormAsync(curator, $"{page}?handler=ContributorDetails", new()
        {
            ["contributorName"] = name,
            ["contributorEmail"] = email,
        }, page);

        Assert.Contains(message, await response.Content.ReadAsStringAsync());
        Assert.Equal("Erika Original", (await ReloadAsync(item.Id)).Contribution!.ContributorName);
    }

    [Fact]
    public async Task A_personal_data_search_finds_people_named_in_what_curators_wrote()
    {
        var item = await app.CreateContributionAsync(ItemStatus.Draft);
        var name = "Person" + Guid.NewGuid().ToString("N");
        using var curator = await CuratorAsync();
        await Browser.PostFormAsync(curator, $"/Admin/Items/Edit/{item.Id}", Fields(item, ("Input.ReceivedFrom", name)));

        var found = await curator.GetStringAsync($"/Admin/Contributors?q={name}");

        Assert.Contains("Named in what curators wrote", found);
        Assert.Contains(item.Title, found);
    }

    [Fact]
    public async Task An_export_keeps_catalogue_details_but_a_public_one_and_one_without_personal_data_leave_things_out()
    {
        var item = await app.CreateContributionAsync(ItemStatus.Published);
        var number = "EXP-" + Guid.NewGuid().ToString("N")[..12];
        using var admin = await CuratorAsync();
        await Browser.PostFormAsync(admin, $"/Admin/Items/Edit/{item.Id}", Fields(item,
            ("Input.ReferenceNumber", number),
            ("Input.RightsUri", RightsOptions.CcBy),
            ("Input.RightsHolder", "Holder Name"),
            ("Input.ReceivedFrom", "Giver Name"),
            ("Input.InternalNotes", "Note about Giver Name")));

        async Task<JsonElement> ExportedAsync(Dictionary<string, string> options)
        {
            var response = await Browser.SendFormAsync(admin, "/Admin/Export", options);
            using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync());
            await using var json = await zip.GetEntry("archive.json")!.OpenAsync();
            using var document = await JsonDocument.ParseAsync(json);
            return document.RootElement.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetInt32() == item.Id).Clone();
        }

        var complete = await ExportedAsync(new() { ["IncludePersonalData"] = "true" });
        var withoutNames = await ExportedAsync(new());
        var published = await ExportedAsync(new() { ["PublishedOnly"] = "true", ["IncludePersonalData"] = "true" });

        Assert.Equal(number, complete.GetProperty("referenceNumber").GetString());
        Assert.Equal("Giver Name", complete.GetProperty("receivedFrom").GetString());
        Assert.Equal(RightsOptions.CcBy, withoutNames.GetProperty("rightsUri").GetString());
        Assert.Equal(number, withoutNames.GetProperty("referenceNumber").GetString());
        Assert.Equal(JsonValueKind.Null, withoutNames.GetProperty("rightsHolder").ValueKind);
        Assert.Equal(JsonValueKind.Null, withoutNames.GetProperty("receivedFrom").ValueKind);
        Assert.Equal(JsonValueKind.Null, withoutNames.GetProperty("internalNotes").ValueKind);
        Assert.Equal(JsonValueKind.Null, published.GetProperty("referenceNumber").ValueKind);
        Assert.Equal(JsonValueKind.Null, published.GetProperty("internalNotes").ValueKind);
        Assert.Equal(RightsOptions.CcBy, published.GetProperty("rightsUri").GetString());
    }
}
