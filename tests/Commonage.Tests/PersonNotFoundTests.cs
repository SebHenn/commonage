using System.Net;
using Commonage.Web.Domain;

namespace Commonage.Tests;

/// <summary>
/// Whether a person exists must not be learnt from the answer: an unknown key, a hidden person, one in the bin, one whose items
/// are all hidden and a key that cannot be one are the same page, to the byte but for the form token and the script nonce.
/// </summary>
[Collection(nameof(AppCollection))]
public class PersonNotFoundTests(CommonageAppFactory app)
{
    [Fact]
    public async Task Every_key_nobody_may_use_gets_the_same_answer()
    {
        await using var archive = await PeopleSeed.NewArchiveAsync(app);
        var keys = await archive.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            var collection = new Collection { Title = "Box", CreatedAt = now };
            var visible = PeopleSeed.NewItem(collection, "Visible", now, true);
            var draft = PeopleSeed.NewItem(collection, "Draft", now, false);
            var hidden = PeopleSeed.NewPerson(PersonWords.New("h"), PersonDisclosure.Hidden, now);
            var binned = PeopleSeed.NewPerson(PersonWords.New("b"), PersonDisclosure.Deceased, now);
            binned.MoveToBin(now);
            var allHidden = PeopleSeed.NewPerson(PersonWords.New("a"), PersonDisclosure.Deceased, now);
            db.AddRange(collection, visible, draft, hidden, binned, allHidden,
                PeopleSeed.Link(visible, hidden, now), PeopleSeed.Link(visible, binned, now), PeopleSeed.Link(draft, allHidden, now));
            await db.SaveChangesAsync();
            return new[] { hidden.PublicId, binned.PublicId, allHidden.PublicId, "0123456789AB", "short", "has space!!!", new string('Z', 400), "I" + new string('0', 11) };
        });

        string? first = null;
        foreach (var key in keys)
        {
            // A new client for each key: the first answer sets the form cookie, and a client that has it is not given it again.
            using var visitor = archive.CreateClient();
            var response = await visitor.GetAsync($"/people/{Uri.EscapeDataString(key)}");
            var body = PeopleSeed.Normalised(await response.Content.ReadAsStringAsync());
            var answer = $"{(int)response.StatusCode}\n{PeopleSeed.Headers(response)}\nset-cookie:{response.Headers.Contains("Set-Cookie")}\n{response.Content.Headers.ContentLength}\n{body}";
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            first ??= answer;
            Assert.Equal(first, answer);
            Assert.DoesNotContain(key, body);
        }
    }
}
