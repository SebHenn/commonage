using System.Net;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticAssets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Commonage.Tests;

[Collection(nameof(AppCollection))]
public class HelperTests(CommonageAppFactory app)
{
    /// <summary>The only addresses behind a login that a helper may reach. Everything else is for curators or admins.</summary>
    private static readonly string[] OpenToHelpers =
    [
        "/Admin", "/Admin/Index", "/Admin/Items/Edit/{id:int?}", "/Admin/Items/AddMany", "/Admin/Items/Preview/{id:int}",
    ];

    [Fact]
    public async Task A_helper_reaches_only_the_pages_for_describing_items()
    {
        using var client = app.CreateClient();
        var provider = app.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        var reachable = new List<string>();
        foreach (var endpoint in app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
                     .Where(e => e.Metadata.GetMetadata<StaticAssetDescriptor>() is null))
        {
            var pattern = "/" + endpoint.RoutePattern.RawText!.TrimStart('/');
            var authorize = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
            if (!pattern.StartsWith("/Admin", StringComparison.OrdinalIgnoreCase) || authorize.Count == 0)
                continue;
            var policy = await AuthorizationPolicy.CombineAsync(provider, authorize);
            var roleRequirements = policy!.Requirements.OfType<RolesAuthorizationRequirement>().ToList();
            if (roleRequirements.All(r => r.AllowedRoles.Contains(Roles.Helper)))
                reachable.Add(pattern);
        }

        // The pictures a helper sees while describing an item are under /admin/media, which is checked separately below.
        var pages = reachable.Distinct().Where(p => !p.StartsWith("/admin/media", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(OpenToHelpers.Order(StringComparer.Ordinal), pages.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_helper_describes_but_cannot_publish_or_delete()
    {
        using var admin = app.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        var (helper, _, _, _) = await app.CreateAccountAsync(admin, Roles.Helper);
        var (collectionId, draftId, publishedId) = await SeedAsync();
        var draftPage = $"/Admin/Items/Edit/{draftId}";
        var publishedPage = $"/Admin/Items/Edit/{publishedId}";

        var edit = await helper.GetStringAsync(draftPage);
        var publishAttempt = await Browser.SendFormAsync(helper, draftPage, new()
        {
            ["Input.Title"] = "Helper title", ["Input.CollectionId"] = collectionId.ToString(), ["command"] = "publish",
        });
        var openPublished = await helper.GetAsync(publishedPage);
        var changePublished = await Browser.SendFormAsync(helper, publishedPage, new()
        {
            ["Input.Title"] = "Changed by a helper", ["Input.CollectionId"] = collectionId.ToString(),
        }, draftPage);
        var previewPublished = await helper.GetAsync($"/Admin/Items/Preview/{publishedId}");
        var listed = await helper.GetStringAsync("/Admin?Text=Published+letter");
        var deleteAttempt = await Browser.SendFormAsync(helper, $"{draftPage}?handler=Delete", new(), draftPage);
        var save = await Browser.SendFormAsync(helper, draftPage, new()
        {
            ["Input.Title"] = "Helper title", ["Input.CollectionId"] = collectionId.ToString(),
        });

        Assert.DoesNotContain("value=\"publish\"", edit);
        Assert.DoesNotContain("Move to bin", edit);
        Assert.Equal(HttpStatusCode.Forbidden, publishAttempt.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, openPublished.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, changePublished.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, previewPublished.StatusCode);
        Assert.DoesNotContain("Published letter", listed);
        Assert.Equal(HttpStatusCode.Forbidden, deleteAttempt.StatusCode);
        Assert.True(save.IsSuccessStatusCode);
        var draft = await app.WithDbAsync(db => db.ArchiveItems.SingleAsync(i => i.Id == draftId));
        var published = await app.WithDbAsync(db => db.ArchiveItems.SingleAsync(i => i.Id == publishedId));
        Assert.Equal(("Helper title", ItemStatus.Draft, null), (draft.Title, draft.Status, draft.DeletedAt));
        Assert.Equal(("Published letter", ItemStatus.Published), (published.Title, published.Status));
    }

    [Fact]
    public async Task A_helper_sees_nothing_of_contributions_and_contributors()
    {
        using var admin = app.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        var (helper, _, _, _) = await app.CreateAccountAsync(admin, Roles.Helper);
        var marker = Guid.NewGuid().ToString("N");
        var (submittedId, acceptedId) = await SeedContributionsAsync(marker);

        var submitted = await helper.GetAsync($"/Admin/Items/Edit/{submittedId}");
        var preview = await helper.GetAsync($"/Admin/Items/Preview/{submittedId}");
        var accepted = await helper.GetStringAsync($"/Admin/Items/Edit/{acceptedId}");
        var list = await helper.GetStringAsync($"/Admin?Text={marker}");
        var contributors = await helper.GetAsync("/Admin/Contributors");
        var bin = await helper.GetAsync("/Admin/Bin");

        Assert.Equal(HttpStatusCode.NotFound, submitted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, preview.StatusCode);
        Assert.DoesNotContain($"name-{marker}", accepted);
        Assert.DoesNotContain($"mail-{marker}", accepted);
        Assert.DoesNotContain($"Submitted {marker}", list);
        Assert.Contains($"Accepted {marker}", list);
        Assert.Equal(HttpStatusCode.Forbidden, contributors.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, bin.StatusCode);
    }

    [Fact]
    public async Task A_helper_cannot_fetch_an_original_or_manage_accounts()
    {
        using var admin = app.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        var (helper, id, _, _) = await app.CreateAccountAsync(admin, Roles.Helper);

        var original = await helper.GetAsync("/admin/media/1/original");
        var users = await helper.GetAsync("/Admin/Users");
        var roles = await app.WithDbAsync(db => db.UserRoles.Where(r => r.UserId == id).Join(db.Roles, r => r.RoleId, r => r.Id, (_, r) => r.Name).ToListAsync());

        Assert.Equal(HttpStatusCode.Forbidden, original.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, users.StatusCode);
        Assert.Equal([Roles.Helper], roles);
    }

    [Fact]
    public async Task A_helper_cannot_act_on_a_contribution_in_any_way()
    {
        using var admin = app.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        var (helper, _, _, _) = await app.CreateAccountAsync(admin, Roles.Helper);
        var marker = Guid.NewGuid().ToString("N");
        var (submittedId, acceptedId) = await SeedContributionsAsync(marker);
        var fileId = await app.WithDbAsync(async db =>
        {
            var file = new MediaFile
            {
                ArchiveItemId = submittedId, OriginalFileName = "a.jpg", ContentType = "image/jpeg", Sha256 = marker.PadRight(64, '0'),
                Status = ProcessingStatus.Ready, UploadedAt = DateTime.UtcNow, StorageId = Guid.NewGuid(),
            };
            db.MediaFiles.Add(file);
            await db.SaveChangesAsync();
            return file.Id;
        });
        var submittedPage = $"/Admin/Items/Edit/{submittedId}";
        var acceptedPage = $"/Admin/Items/Edit/{acceptedId}";

        // On an accepted draft, the contributor's details and the curators' decisions stay closed to a helper.
        var forbidden = new List<HttpResponseMessage>();
        foreach (var handler in new[] { "Withdraw", "ContributorDetails", "RemoveContributorDetails", "Delete" })
            forbidden.Add(await Browser.SendFormAsync(helper, $"{acceptedPage}?handler={handler}", new(), acceptedPage));
        forbidden.Add(await Browser.SendFormAsync(helper, $"{acceptedPage}?handler=DeleteMedia&mediaFileId={fileId}", new(), acceptedPage));
        foreach (var command in new[] { "accept", "reject" })
            forbidden.Add(await Browser.SendFormAsync(helper, acceptedPage, new() { ["Input.Title"] = "x", ["command"] = command }));

        forbidden.Add(await Browser.SendFormAsync(helper, submittedPage, new() { ["Input.Title"] = "x", ["command"] = "accept" }, acceptedPage));

        // On an item that is being reviewed, a helper is told nothing.
        var hidden = new List<HttpResponseMessage>
        {
            await helper.GetAsync($"/admin/media/{fileId}/thumbnail"),
            await helper.GetAsync($"{submittedPage}?handler=PhotoCard&mediaFileId={fileId}"),
            await Browser.SendFormAsync(helper, $"{submittedPage}?handler=TurnPhoto&mediaFileId={fileId}", new(), acceptedPage),
            await Browser.SendFormAsync(helper, $"{submittedPage}?handler=MovePhoto&mediaFileId={fileId}", new(), acceptedPage),
        };

        Assert.All(forbidden, response => Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode));
        Assert.All(hidden, response => Assert.Equal(HttpStatusCode.NotFound, response.StatusCode));
        var items = await app.WithDbAsync(db => db.ArchiveItems.Where(i => i.Id == submittedId || i.Id == acceptedId).ToListAsync());
        Assert.Equal(ItemStatus.Submitted, items.Single(i => i.Id == submittedId).Status);
        Assert.Equal(ItemStatus.Draft, items.Single(i => i.Id == acceptedId).Status);
    }

    [Fact]
    public async Task A_helper_is_not_told_what_a_contribution_holds_through_the_duplicate_check()
    {
        using var admin = app.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        var (helper, _, _, _) = await app.CreateAccountAsync(admin, Roles.Helper);
        var marker = Guid.NewGuid().ToString("N");
        var (submittedId, acceptedId) = await SeedContributionsAsync(marker);
        await app.WithDbAsync(async db =>
        {
            (await db.ArchiveItems.SingleAsync(i => i.Id == submittedId)).ReferenceNumber = $"ref-{marker}";
            await db.SaveChangesAsync();
            return 0;
        });

        var asHelper = await Browser.PostFormAsync(helper, $"/Admin/Items/Edit/{acceptedId}", new() { ["Input.Title"] = "Accepted", ["Input.ReferenceNumber"] = $"ref-{marker}" }, $"/Admin/Items/Edit/{acceptedId}");
        var asHelperText = await asHelper.Content.ReadAsStringAsync();

        Assert.Contains("already used by another item", asHelperText);
        Assert.DoesNotContain($"Submitted {marker}", asHelperText);
    }

    private async Task<(int CollectionId, int DraftId, int PublishedId)> SeedAsync()
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommonageDbContext>();
        var now = DateTime.UtcNow;
        var collection = new Collection { Title = "Helper letters", CreatedAt = now };
        var draft = new ArchiveItem { Title = "Helper draft", Collection = collection, CreatedAt = now, UpdatedAt = now };
        var published = new ArchiveItem { Title = "Published letter", Collection = collection, CreatedAt = now, UpdatedAt = now };
        published.Publish(now);
        db.AddRange(collection, draft, published);
        await db.SaveChangesAsync();
        return (collection.Id, draft.Id, published.Id);
    }

    private async Task<(int SubmittedId, int AcceptedId)> SeedContributionsAsync(string marker)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommonageDbContext>();
        var now = DateTime.UtcNow;
        var collection = new Collection { Title = "Contributed", CreatedAt = now };
        ArchiveItem Make(string title, ItemStatus status) => new()
        {
            Title = title, Collection = collection, CreatedAt = now, UpdatedAt = now, Status = status,
            Contribution = new Contribution { ContributorName = $"name-{marker}", ContributorEmail = $"mail-{marker}@example.org", ReferenceCode = Contribution.NewReferenceCode(), ConsentText = "Consent" },
        };
        var submitted = Make($"Submitted {marker}", ItemStatus.Submitted);
        var accepted = Make($"Accepted {marker}", ItemStatus.Draft);
        db.AddRange(collection, submitted, accepted);
        await db.SaveChangesAsync();
        return (submitted.Id, accepted.Id);
    }
}
