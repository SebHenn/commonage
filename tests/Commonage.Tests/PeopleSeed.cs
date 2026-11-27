using System.Text.RegularExpressions;
using Commonage.Web.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Commonage.Tests;

/// <summary>A clock a test can move, for embargoes that end.</summary>
internal sealed class MutableClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Every word a person has that must not show where it must not: each is found by searching for it.</summary>
internal sealed record PersonWords(string Name, string Marked, string Secret, string Biography, string Note, string Born)
{
    public static PersonWords New(string kind) => new(
        $"Karl {TestData.UniqueWord("main" + kind)}", $"Onkel {TestData.UniqueWord("marked" + kind)}", $"Geb {TestData.UniqueWord("secret" + kind)}",
        $"Bio {TestData.UniqueWord("bio" + kind)}", $"Note {TestData.UniqueWord("note" + kind)}", "1901");

    /// <summary>Everything that is not the main name: what a hidden person must not leak, and what visitors never get of anybody.</summary>
    public IEnumerable<string> Texts => [Name, Marked, Secret, Biography, Note, Born];
}

/// <summary>Seeds archives for the tests of people on public pages, and reads their pages. Each test makes an archive of its own.</summary>
internal static partial class PeopleSeed
{
    public static ArchiveItem NewItem(Collection collection, string title, DateTime now, bool published, bool withFile = false)
    {
        var item = new ArchiveItem { Title = title, Description = $"Words of {title}", Collection = collection, CreatedAt = now, UpdatedAt = now };
        if (withFile)
            item.MediaFiles.Add(new MediaFile
            {
                ArchiveItem = item, OriginalFileName = "a.jpg", ContentType = "image/jpeg", Sha256 = new string('a', 64),
                StorageId = Guid.NewGuid(), Status = ProcessingStatus.Ready, AltText = "Alt text",
            });
        if (published)
            item.Publish(now);
        return item;
    }

    public static Person NewPerson(PersonWords words, PersonDisclosure disclosure, DateTime now, bool withDates = true)
    {
        var person = new Person { Name = words.Name, CreatedAt = now, UpdatedAt = now, Biography = words.Biography, CuratorNote = words.Note };
        person.SetDisclosure(disclosure, now);
        if (withDates)
        {
            Assert.True(person.TrySetBorn(words.Born));
            Assert.True(person.TrySetDied("1980"));
        }
        person.Names.Add(new PersonName { Name = words.Marked, Kind = PersonNameKind.Nickname, ShownToVisitors = true });
        person.Names.Add(new PersonName { Name = words.Secret, Kind = PersonNameKind.BirthName, ShownToVisitors = false });
        return person;
    }

    public static ItemPerson Link(ArchiveItem item, Person person, DateTime now) => new() { ArchiveItem = item, Person = person, CreatedAt = now };

    public static async Task<WebApplicationFactory<Program>> NewArchiveAsync(CommonageAppFactory app, MutableClock? clock = null)
    {
        if (clock is null)
            return await app.NewArchiveAsync();
        return await app.NewArchiveAsync(b => b.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(clock)));
    }

    public static async Task<string> TextAsync(HttpClient client, string address)
    {
        var response = await client.GetAsync(address);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync() : "";
    }

    /// <summary>Every page carries a form token and a script nonce that are new with each response; nothing else is changed.</summary>
    public static string Normalised(string page) => Nonce().Replace(Token().Replace(page, "$1"), "$1\"\"");

    [GeneratedRegex(@"(__RequestVerificationToken[^>]*?value="")[^""]*")]
    private static partial Regex Token();

    [GeneratedRegex(@"(nonce=)""[^""]*""")]
    private static partial Regex Nonce();

    public static string MetaTags(string page) => string.Concat(Regex.Matches(page, @"<meta[^>]*(og:|name=""description""|name=""robots"")[^>]*>").Select(m => m.Value + "\n"));

    public static string Headers(HttpResponseMessage response) => string.Join('\n',
        response.Headers.Concat(response.Content.Headers).Where(h => h.Key is not ("Date" or "Set-Cookie")).OrderBy(h => h.Key, StringComparer.Ordinal)
            .Select(h => $"{h.Key}: {string.Join(',', h.Value)}"));

    public static async Task<Guid> GiveLogoAsync(WebApplicationFactory<Program> archive)
    {
        var logo = Guid.NewGuid();
        await archive.WithDbAsync(async db =>
        {
            var settings = await db.ArchiveSettings.FirstOrDefaultAsync();
            if (settings is null)
                db.ArchiveSettings.Add(settings = new ArchiveSettings());
            settings.LogoId = logo;
            return await db.SaveChangesAsync();
        });
        archive.Services.GetRequiredService<Commonage.Web.Operations.ArchiveProfile>().Forget();
        return logo;
    }

    /// <summary>The public key of a person.</summary>
    public static Task<string> KeyAsync(WebApplicationFactory<Program> archive, int personId) =>
        archive.WithDbAsync(db => db.People.IgnoreQueryFilters().Where(p => p.Id == personId).Select(p => p.PublicId).SingleAsync());
}
