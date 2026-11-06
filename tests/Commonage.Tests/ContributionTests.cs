using System.Net;
using System.Text.Json;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Media;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Commonage.Tests;

[Collection(nameof(AppCollection))]
public class ContributionTests(CommonageAppFactory app)
{
    private static readonly byte[] Photo = File.ReadAllBytes(Path.Combine("Fixtures", "photo-with-gps.jpg"));

    [Fact]
    public async Task A_contribution_stays_private_until_a_curator_publishes_it()
    {
        using var curator = app.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);
        var collectionId = await CreateCollectionAsync();
        var linkTitle = $"Share your photos {Guid.NewGuid():N}";
        await Browser.PostFormAsync(curator, "/Admin/Contributions/Edit", new()
        {
            ["Input.Title"] = linkTitle,
            ["Input.CollectionId"] = collectionId.ToString(),
            ["Input.IsActive"] = "true",
        });
        var token = await WithDbAsync(db => db.ContributionLinks.Where(l => l.Title == linkTitle).Select(l => l.Token).SingleAsync());
        using var visitor = app.CreateClient();

        var form = await visitor.GetStringAsync($"/contribute/{token}");
        var fields = ValidFields("Flood in Mill Street");
        fields["Input.Date"] = "um 1962";
        fields["Input.Email"] = "contributor@example.org";
        var sent = await Browser.SendFormWithFilesAsync(visitor, $"/contribute/{token}", fields, ("flood.jpg", Photo));

        Assert.Contains(linkTitle, form);
        Assert.Contains("Thank you", await sent.Content.ReadAsStringAsync());
        var item = await WithDbAsync(db => db.ArchiveItems
            .Include(i => i.Contribution)
            .Include(i => i.MediaFiles)
            .SingleAsync(i => i.Contribution!.ContributionLink!.Token == token));
        Assert.Equal(ItemStatus.Submitted, item.Status);
        Assert.Equal(collectionId, item.CollectionId);
        Assert.Equal("um 1962", item.DateText);
        Assert.Single(item.MediaFiles);
        Assert.Equal(("Ada Contributor", "contributor@example.org", RightsStatement.CreatedByContributor),
            (item.Contribution!.ContributorName, item.Contribution.ContributorEmail, item.Contribution.Rights));
        Assert.Contains("publish it on this website", item.Contribution.ConsentText);

        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/items/{item.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/media/{item.MediaFiles[0].Id}/preview")).StatusCode);
        Assert.DoesNotContain("Flood in Mill Street", await visitor.GetStringAsync($"/collections/{collectionId}"));

        var waiting = await curator.GetStringAsync("/Admin?status=Submitted");
        var review = await curator.GetStringAsync($"/Admin/Items/Edit/{item.Id}");
        Assert.Contains("Flood in Mill Street", waiting);
        Assert.Contains("Ada Contributor", review);
        Assert.Contains("contributor@example.org", review);

        await Browser.PostFormAsync(curator, $"/Admin/Items/Edit/{item.Id}", new()
        {
            ["Input.Title"] = "Flood in Mill Street",
            ["Input.CollectionId"] = collectionId.ToString(),
            ["Input.Date"] = "um 1962",
            ["command"] = "publish",
        });

        var published = await visitor.GetStringAsync($"/items/{item.Id}");
        Assert.Contains("Contributed by Ada Contributor", published);
        Assert.NotNull(await WithDbAsync(db => db.Contributions.Where(c => c.ArchiveItemId == item.Id).Select(c => c.ReviewedAt).SingleAsync()));
    }

    [Fact]
    public async Task Closed_and_unknown_links_show_not_found()
    {
        var closed = await CreateLinkAsync(isActive: false);
        using var visitor = app.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/contribute/{closed}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/contribute/{ContributionLink.NewToken()}")).StatusCode);
    }

    [Fact]
    public async Task Name_rights_and_consent_are_required()
    {
        var token = await CreateLinkAsync();
        using var visitor = app.CreateClient();

        var response = await Browser.SendFormWithFilesAsync(visitor, $"/contribute/{token}", new() { ["Input.Title"] = "Old school" });

        var page = await response.Content.ReadAsStringAsync();
        Assert.Contains("Please enter your name.", page);
        Assert.Contains("Please tell us who made it.", page);
        Assert.Contains("Please agree, so that we can include what you send in.", page);
        Assert.Equal(0, await CountContributionsAsync(token));
    }

    [Fact]
    public async Task A_rejected_contribution_stays_hidden_and_can_be_deleted_with_its_photos()
    {
        var token = await CreateLinkAsync();
        using var visitor = app.CreateClient();
        await Browser.SendFormWithFilesAsync(visitor, $"/contribute/{token}", ValidFields("Advertising"), ("spam.jpg", Photo));
        var item = await WithDbAsync(db => db.ArchiveItems.Include(i => i.MediaFiles).SingleAsync(i => i.Contribution!.ContributionLink!.Token == token));
        using var curator = app.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);

        await Browser.PostFormAsync(curator, $"/Admin/Items/Edit/{item.Id}", new()
        {
            ["Input.Title"] = "Advertising",
            ["Input.CollectionId"] = item.CollectionId.ToString(),
            ["command"] = "reject",
        });
        var statusAfterReject = await WithDbAsync(db => db.ArchiveItems.Where(i => i.Id == item.Id).Select(i => i.Status).SingleAsync());
        await Browser.PostFormAsync(curator, $"/Admin/Items/Edit/{item.Id}?handler=Delete", [], pageUrl: $"/Admin/Items/Edit/{item.Id}");
        await Browser.PostFormAsync(curator, $"/Admin/Bin?handler=DeleteItem&id={item.Id}", [], pageUrl: "/Admin/Bin");

        Assert.Equal(ItemStatus.Rejected, statusAfterReject);
        Assert.False(await WithDbAsync(db => db.ArchiveItems.AnyAsync(i => i.Id == item.Id)));
        Assert.Equal(0, await CountContributionsAsync(token));
        var storage = app.Services.GetRequiredService<IFileStorage>();
        Assert.Throws<FileNotFoundException>(() => storage.OpenRead(item.MediaFiles[0].OriginalKey));
    }

    [Fact]
    public async Task Automated_submissions_are_thanked_but_not_saved()
    {
        var token = await CreateLinkAsync();
        using var visitor = app.CreateClient();
        var honeypot = ValidFields("Cheap watches");
        honeypot["Website"] = "https://spam.example";

        var caughtByHoneypot = await Browser.SendFormWithFilesAsync(visitor, $"/contribute/{token}", honeypot);
        await using var patientServer = app.WithWebHostBuilder(b => b.UseSetting("Contributions:MinimumFillSeconds", "60"));
        using var fastVisitor = patientServer.CreateClient();
        var caughtByTimer = await Browser.SendFormWithFilesAsync(fastVisitor, $"/contribute/{token}", ValidFields("Cheap watches"));

        Assert.Contains("Thank you", await caughtByHoneypot.Content.ReadAsStringAsync());
        Assert.Contains("Thank you", await caughtByTimer.Content.ReadAsStringAsync());
        Assert.Equal(0, await CountContributionsAsync(token));
    }

    [Fact]
    public async Task Too_many_submissions_from_one_address_are_refused()
    {
        var token = await CreateLinkAsync();
        await using var strictServer = app.WithWebHostBuilder(b => b.UseSetting("Contributions:MaxSubmissionsPerHour", "2"));
        using var visitor = strictServer.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
            statuses.Add((await Browser.SendFormWithFilesAsync(visitor, $"/contribute/{token}", ValidFields($"Photo {i}"))).StatusCode);

        Assert.Equal([HttpStatusCode.Redirect, HttpStatusCode.Redirect, HttpStatusCode.TooManyRequests], statuses);
        Assert.Equal(2, await CountContributionsAsync(token));
    }

    [Fact]
    public async Task The_same_form_sent_again_arrives_once_and_gets_the_same_reference()
    {
        var token = await CreateLinkAsync();
        using var visitor = app.CreateClient();
        var fields = ValidFields("Sent over a bad connection");
        fields["SendingKey"] = Guid.NewGuid().ToString("N");

        var first = await Browser.SendFormWithFilesAsync(visitor, $"/contribute/{token}", fields, ("flood.jpg", Photo));
        var again = await Browser.SendFormWithFilesAsync(visitor, $"/contribute/{token}", fields, ("flood.jpg", Photo));

        Assert.Equal(1, await CountContributionsAsync(token));
        Assert.Equal(1, await WithDbAsync(db => db.MediaFiles.CountAsync(f => f.ArchiveItem.Contribution!.ContributionLink!.Token == token)));
        var reference = await WithDbAsync(db => db.Contributions.Where(c => c.ContributionLink!.Token == token).Select(c => c.ReferenceCode).SingleAsync());
        Assert.Contains(reference!, await first.Content.ReadAsStringAsync());
        Assert.Contains(reference!, await again.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Sent_twice_at_the_same_moment_it_still_arrives_once()
    {
        var token = await CreateLinkAsync();
        var key = Guid.NewGuid().ToString("N");
        var visitors = Enumerable.Range(0, 4).Select(_ => app.CreateClient()).ToList();

        var replies = await Task.WhenAll(visitors.Select(visitor =>
        {
            var fields = ValidFields("Tapped twice");
            fields["SendingKey"] = key;
            return Browser.SendFormWithFilesAsync(visitor, $"/contribute/{token}", fields);
        }));

        Assert.All(replies, reply => Assert.Contains("Thank you", reply.Content.ReadAsStringAsync().Result));
        Assert.Equal(1, await CountContributionsAsync(token));
        visitors.ForEach(visitor => visitor.Dispose());
    }

    [Fact]
    public async Task Going_back_and_changing_something_counts_as_a_new_contribution()
    {
        var token = await CreateLinkAsync();
        using var visitor = app.CreateClient();
        var key = Guid.NewGuid().ToString("N");
        var fields = ValidFields("The first one");
        fields["SendingKey"] = key;
        var otherPhoto = ValidFields("The first one");
        otherPhoto["SendingKey"] = key;
        var otherTitle = ValidFields("Another one");
        otherTitle["SendingKey"] = key;

        await Browser.SendFormWithFilesAsync(visitor, $"/contribute/{token}", fields, ("first.jpg", Photo));
        await Browser.SendFormWithFilesAsync(visitor, $"/contribute/{token}", otherPhoto, ("second.jpg", Photo));
        await Browser.SendFormWithFilesAsync(visitor, $"/contribute/{token}", otherTitle, ("first.jpg", Photo));

        Assert.Equal(3, await CountContributionsAsync(token));
    }

    [Fact]
    public async Task A_form_that_failed_while_storing_photos_really_sends_them_the_next_time()
    {
        var token = await CreateLinkAsync();
        var storage = new StorageThatFailsOnce(app.Services.GetRequiredService<IFileStorage>());
        await using var server = app.WithWebHostBuilder(b => b.ConfigureTestServices(services => services.AddSingleton<IFileStorage>(storage)));
        using var visitor = server.CreateClient();
        var fields = ValidFields("The disk had a bad moment");
        fields["SendingKey"] = Guid.NewGuid().ToString("N");

        var failed = await Browser.SendFormWithFilesAsync(visitor, $"/contribute/{token}", fields, ("flood.jpg", Photo));
        var again = await Browser.SendFormWithFilesAsync(visitor, $"/contribute/{token}", fields, ("flood.jpg", Photo));

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Contains("Thank you", await again.Content.ReadAsStringAsync());
        Assert.Equal(1, await WithDbAsync(db => db.MediaFiles.CountAsync(f => f.ArchiveItem.Contribution!.ContributionLink!.Token == token)));
    }

    private sealed class StorageThatFailsOnce(IFileStorage real) : IFileStorage
    {
        private int _calls;

        public Task SaveAsync(string key, Stream content, CancellationToken cancellationToken = default) =>
            Interlocked.Increment(ref _calls) == 1 ? throw new IOException("A bad moment.") : real.SaveAsync(key, content, cancellationToken);

        public Stream OpenRead(string key) => real.OpenRead(key);

        public void Delete(string key) => real.Delete(key);
    }

    [Fact]
    public async Task A_script_gets_its_answer_as_data_and_the_reference_still_reaches_the_thanks_page()
    {
        var token = await CreateLinkAsync();
        using var visitor = app.CreateClient();
        visitor.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        var sent = await Browser.SendFormWithFilesAsync(visitor, $"/contribute/{token}", ValidFields("Sent by the script"), ("flood.jpg", Photo));

        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        using var answer = JsonDocument.Parse(await sent.Content.ReadAsStringAsync());
        var thanks = await visitor.GetStringAsync(answer.RootElement.GetProperty("thanksUrl").GetString());
        var reference = await WithDbAsync(db => db.Contributions.Where(c => c.ContributionLink!.Token == token).Select(c => c.ReferenceCode).SingleAsync());
        Assert.Contains(reference!, thanks);
    }

    [Fact]
    public async Task A_script_is_told_what_is_wrong_per_field_and_not_to_choose_the_photos_again()
    {
        var token = await CreateLinkAsync();
        using var visitor = app.CreateClient();
        visitor.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        var fields = ValidFields("With a date nobody can read");
        fields["Input.Date"] = "the winter when I was small";

        var sent = await Browser.SendFormWithFilesAsync(visitor, $"/contribute/{token}", fields, ("flood.jpg", Photo));

        Assert.Equal(HttpStatusCode.BadRequest, sent.StatusCode);
        using var answer = JsonDocument.Parse(await sent.Content.ReadAsStringAsync());
        var errors = answer.RootElement.GetProperty("errors");
        Assert.Contains("could not be understood", errors.GetProperty("Input.Date")[0].GetString());
        Assert.False(errors.TryGetProperty("Files", out _));
        Assert.Equal(0, await CountContributionsAsync(token));
    }

    [Fact]
    public async Task A_heic_photo_is_refused_in_words_that_help()
    {
        var token = await CreateLinkAsync();
        using var visitor = app.CreateClient();
        byte[] heic = [0, 0, 0, 24, .. "ftypheic"u8, 0, 0, 0, 0];

        var sent = await Browser.SendFormWithFilesAsync(visitor, $"/contribute/{token}", ValidFields("From a phone"), ("IMG_0001.HEIC", heic));

        var page = await sent.Content.ReadAsStringAsync();
        Assert.Contains("IMG_0001.HEIC is in the HEIC format", page);
        Assert.DoesNotContain("IMG_0001.HEIC is not a supported photo format", page);
        Assert.Equal(0, await CountContributionsAsync(token));
    }

    [Fact]
    public async Task Only_the_contribution_form_may_show_pictures_from_the_device()
    {
        var token = await CreateLinkAsync();
        using var visitor = app.CreateClient();

        var form = await visitor.GetAsync($"/contribute/{token}");
        var home = await visitor.GetAsync("/");
        var login = await visitor.GetAsync("/Account/Login");

        Assert.Contains("img-src 'self' data: blob:;", string.Join(" ", form.Headers.GetValues("Content-Security-Policy")));
        Assert.DoesNotContain("blob:", string.Join(" ", home.Headers.GetValues("Content-Security-Policy")));
        Assert.DoesNotContain("blob:", string.Join(" ", login.Headers.GetValues("Content-Security-Policy")));
    }

    private static Dictionary<string, string> ValidFields(string title) => new()
    {
        ["Input.Title"] = title,
        ["Input.Name"] = "Ada Contributor",
        ["Input.ShowName"] = "true",
        ["Input.Rights"] = nameof(RightsStatement.CreatedByContributor),
        ["Input.Consent"] = "true",
    };

    private Task<int> CreateCollectionAsync() => WithDbAsync(async db =>
    {
        var collection = new Collection { Title = $"Contributions {Guid.NewGuid():N}", CreatedAt = DateTime.UtcNow };
        db.Add(collection);
        await db.SaveChangesAsync();
        return collection.Id;
    });

    private async Task<string> CreateLinkAsync(bool isActive = true)
    {
        var collectionId = await CreateCollectionAsync();
        return await WithDbAsync(async db =>
        {
            var link = new ContributionLink
            {
                Title = "Share your photos",
                CollectionId = collectionId,
                Token = ContributionLink.NewToken(),
                IsActive = isActive,
                CreatedAt = DateTime.UtcNow,
            };
            db.Add(link);
            await db.SaveChangesAsync();
            return link.Token;
        });
    }

    private Task<int> CountContributionsAsync(string token) =>
        WithDbAsync(db => db.Contributions.CountAsync(c => c.ContributionLink!.Token == token));

    private async Task<T> WithDbAsync<T>(Func<CommonageDbContext, Task<T>> query)
    {
        using var scope = app.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<CommonageDbContext>());
    }
}
