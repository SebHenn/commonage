using System.Net;
using Commonage.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Tests;

/// <summary>Inviting visitors from a collection's page is the curators' choice, and the poster is theirs to print.</summary>
[Collection(nameof(AppCollection))]
public class LinkInvitationTests(CommonageAppFactory app)
{
    private async Task<(int CollectionId, int LinkId, string Token)> CreateLinkAsync(bool active, bool invite)
    {
        var suffix = Guid.NewGuid().ToString("N");
        return await app.WithDbAsync(async db =>
        {
            var collection = new Collection { Title = $"Invite {suffix}", CreatedAt = DateTime.UtcNow };
            var link = new ContributionLink
            {
                Title = $"Send us your Mill Street photos {suffix}",
                Introduction = "We are looking for the flood of 1962.",
                Collection = collection,
                Token = ContributionLink.NewToken(),
                IsActive = active,
                InviteOnCollectionPage = invite,
                CreatedAt = DateTime.UtcNow,
            };
            db.Add(link);
            await db.SaveChangesAsync();
            return (collection.Id, link.Id, link.Token);
        });
    }

    [Fact]
    public async Task The_collection_page_invites_only_for_an_open_link_a_curator_chose()
    {
        var chosen = await CreateLinkAsync(active: true, invite: true);
        var notChosen = await CreateLinkAsync(active: true, invite: false);
        var closed = await CreateLinkAsync(active: false, invite: true);
        using var visitor = app.CreateClient();

        var invited = await visitor.GetStringAsync($"/collections/{chosen.CollectionId}");
        Assert.Contains("Send something in", invited);
        Assert.Contains($"/contribute/{chosen.Token}", invited);
        foreach (var other in new[] { notChosen, closed })
        {
            var page = await visitor.GetStringAsync($"/collections/{other.CollectionId}");
            Assert.DoesNotContain("Send something in", page);
            Assert.DoesNotContain(other.Token, page);
        }
    }

    [Fact]
    public async Task A_curator_turns_the_invitation_on_and_it_is_logged_like_any_change_to_the_link()
    {
        var link = await CreateLinkAsync(active: true, invite: false);
        using var curator = app.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);

        await Browser.PostFormAsync(curator, $"/Admin/Contributions/Edit/{link.LinkId}", new()
        {
            ["Input.Title"] = "Mill Street 1962",
            ["Input.CollectionId"] = link.CollectionId.ToString(),
            ["Input.IsActive"] = "true",
            ["Input.InviteOnCollectionPage"] = "true",
        });

        Assert.True(await app.WithDbAsync(db => db.ContributionLinks.Where(l => l.Id == link.LinkId).Select(l => l.InviteOnCollectionPage).SingleAsync()));
        Assert.Contains("Changed the contribution link", await curator.GetStringAsync("/Admin/Activity"));
    }

    [Fact]
    public async Task The_poster_shows_the_form_the_link_opens_and_is_for_curators_only()
    {
        var link = await CreateLinkAsync(active: true, invite: false);
        using var curator = app.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);

        var poster = await curator.GetStringAsync($"/Admin/Contributions/Poster/{link.LinkId}");
        Assert.Contains("Send us your Mill Street photos", poster);
        Assert.Contains("We are looking for the flood of 1962.", poster);
        Assert.Contains($"/contribute/{link.Token}", poster);
        Assert.Contains("<svg", poster);
        Assert.DoesNotContain(" style=", poster);
        Assert.DoesNotContain("This link is closed", poster);
        Assert.Equal(HttpStatusCode.NotFound, (await curator.GetAsync("/Admin/Contributions/Poster/999999")).StatusCode);

        using var visitor = app.CreateClient(new() { AllowAutoRedirect = false });
        var anonymous = await visitor.GetAsync($"/Admin/Contributions/Poster/{link.LinkId}");
        Assert.NotEqual(HttpStatusCode.OK, anonymous.StatusCode);
        Assert.DoesNotContain(link.Token, await anonymous.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_poster_of_a_closed_link_warns_the_curator()
    {
        var link = await CreateLinkAsync(active: false, invite: false);
        using var curator = app.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);

        Assert.Contains("This link is closed", await curator.GetStringAsync($"/Admin/Contributions/Poster/{link.LinkId}"));
    }
}
