using System.Security.Cryptography;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Media;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Demo;

/// <summary>
/// A small made-up archive for trying Commonage out, showing it to a group or testing it with them. Everything in it
/// is invented: the town of Lindenau, its people and the drawings. Switched on with <c>Demo:Language</c> ("en" or
/// "de"; <c>COMMONAGE_DEMO</c> in Docker), and only filled into an archive that has no collections yet.
/// </summary>
public static class DemoContent
{
    public const string Section = "Demo";

    public static readonly IReadOnlyList<string> Languages = ["en", "de"];

    /// <summary>The demo language, or null when the archive is not a demo.</summary>
    public static string? LanguageOf(IConfiguration configuration) =>
        configuration[$"{Section}:Language"]?.Trim().ToLowerInvariant() is { } language && Languages.Contains(language) ? language : null;

    public static async Task SeedAsync(IServiceProvider services, IConfiguration configuration, ILogger logger)
    {
        if (LanguageOf(configuration) is not { } language)
            return;

        var db = services.GetRequiredService<CommonageDbContext>();
        if (await db.Collections.AnyAsync())
            return;

        var storage = services.GetRequiredService<IFileStorage>();
        var de = language == "de";
        string T(string english, string german) => de ? german : english;
        var now = DateTime.UtcNow;
        var tags = new Dictionary<string, Tag>();
        var scenes = new List<(ArchiveItem Item, string? Scene)>();
        ArchiveItem letter;
        var letterPages = DemoLetter.Pages(de);

        var millStreet = Collection(T("Mill Street", "Mühlenstraße"),
            T("Houses, shops and people of Mill Street in Lindenau, from the 1930s to today.",
              "Häuser, Läden und Menschen der Mühlenstraße in Lindenau, von den 1930er Jahren bis heute."));
        var club = Collection(T("Lindenau Football Club", "FC Lindenau"),
            T("Team photos, letters and memories from the club, founded in 1921.",
              "Mannschaftsfotos, Briefe und Erinnerungen aus dem Verein, gegründet 1921."));
        var school = Collection(T("School memories", "Schulerinnerungen"),
            T("The old school on the hill and the people who learned and taught there.",
              "Die alte Schule am Berg und die Menschen, die dort gelernt und gelehrt haben."));
        db.Collections.AddRange(millStreet, club, school);

        var items = new List<ArchiveItem>
        {
            Published(millStreet, T("The mill in winter", "Die Mühle im Winter"), T("circa 1952", "um 1952"),
                T("The water mill that gave the street its name, in the hard winter of the early 1950s. It ground grain until 1961.",
                  "Die Wassermühle, nach der die Straße benannt ist, im strengen Winter Anfang der 1950er Jahre. Sie mahlte Getreide bis 1961."),
                [T("mill", "Mühle"), T("winter", "Winter")], "mill-winter"),
            Published(millStreet, T("Flood in Mill Street", "Hochwasser in der Mühlenstraße"), T("March 1962", "März 1962"),
                T("After days of rain the brook rose over its banks. Neighbours rowed bread to the houses at the lower end of the street.",
                  "Nach tagelangem Regen trat der Bach über die Ufer. Nachbarn brachten Brot mit dem Boot zu den Häusern am unteren Ende der Straße."),
                [T("flood", "Hochwasser"), T("Mill Street", "Mühlenstraße")], "flood",
                contributor: T("Erika M.", "Erika M.")),
            Published(millStreet, T("Krause's bakery", "Bäckerei Krause"), T("1950s", "1950er Jahre"),
                T("The bakery at number 12, run by the Krause family for three generations. The awning was red and white.",
                  "Die Bäckerei in Nummer 12, drei Generationen lang von Familie Krause geführt. Die Markise war rot-weiß."),
                [T("shops", "Läden"), T("Mill Street", "Mühlenstraße")], "bakery"),
            Published(millStreet, T("Street party for the new bridge", "Straßenfest zur neuen Brücke"), "12.06.1971",
                T("The whole street celebrated when the new bridge over the brook opened. There was music, cake and a race for the children.",
                  "Die ganze Straße feierte die Eröffnung der neuen Brücke über den Bach. Es gab Musik, Kuchen und ein Wettrennen für die Kinder."),
                [T("celebrations", "Feste"), T("bridge", "Brücke")], "bridge-party"),
            Published(club, T("Team photo 1958", "Mannschaftsfoto 1958"), "1958",
                T("The first team in the season they won the district cup. Names of the players are welcome.",
                  "Die erste Mannschaft in der Saison, in der sie den Kreispokal gewann. Namen der Spieler sind willkommen."),
                [T("football", "Fußball"), T("team photos", "Mannschaftsfotos")], "team"),
            (letter = Published(club, T("Letter about the new pitch", "Brief wegen des neuen Sportplatzes"), T("before 1965", "vor 1965"),
                // The words of the letter, as a curator would write them out, because nobody can search for handwriting.
                string.Join("\n\n", letterPages.SelectMany(page => page)),
                [T("football", "Fußball"), T("letters", "Briefe")], scene: null)),
            Published(school, T("First day at school", "Erster Schultag"), T("September 1968", "September 1968"),
                T("The new first-years in front of the school on the hill, each with a school cone.",
                  "Die neuen Erstklässler vor der Schule am Berg, jedes Kind mit einer Schultüte."),
                [T("school", "Schule"), T("children", "Kinder")], "school"),
            Published(millStreet, T("Memories of the tram", "Erinnerungen an die Straßenbahn"), "1930–1955",
                T("Until 1955 the tram ran down Mill Street to the station. Older residents remember the bell and the conductor who knew everyone by name.",
                  "Bis 1955 fuhr die Straßenbahn durch die Mühlenstraße zum Bahnhof. Ältere Bewohner erinnern sich an die Klingel und den Schaffner, der alle beim Namen kannte."),
                [T("tram", "Straßenbahn"), T("transport", "Verkehr")], "tram"),
        };

        // Work in progress, to show curators what is not public yet.
        var draft = new ArchiveItem
        {
            Title = T("Unsorted photos from the club house", "Unsortierte Fotos aus dem Vereinsheim"),
            Description = T("Found in a box in the attic. Dates and names still to be found out.",
                "In einer Kiste auf dem Dachboden gefunden. Daten und Namen müssen noch herausgefunden werden."),
            Collection = club,
            Status = ItemStatus.Draft,
            CreatedAt = now,
            UpdatedAt = now,
        };
        items.Add(draft);

        var link = new ContributionLink
        {
            Title = T("Share your photos of Mill Street", "Teilen Sie Ihre Fotos der Mühlenstraße"),
            Introduction = T("Do you have photos or stories from Mill Street? Send them in.",
                "Haben Sie Fotos oder Geschichten aus der Mühlenstraße? Senden Sie sie ein."),
            Collection = millStreet,
            Token = ContributionLink.NewToken(),
            IsActive = true,
            CreatedAt = now,
        };
        db.ContributionLinks.Add(link);

        // A contribution waiting for review, so curators can try the review straight away.
        var waiting = new ArchiveItem
        {
            Title = T("My grandmother's shop", "Der Laden meiner Großmutter"),
            Description = T("My grandmother ran the grocery at number 3 until 1975. This is the shop front.",
                "Meine Großmutter führte bis 1975 den Lebensmittelladen in Nummer 3. Das ist die Ladenfront."),
            Collection = millStreet,
            Status = ItemStatus.Submitted,
            CreatedAt = now,
            UpdatedAt = now,
            Contribution = new Contribution
            {
                ContributionLink = link,
                ContributorName = T("Sam Example", "Sam Beispiel"),
                ContributorEmail = "sam@example.org",
                ShowName = true,
                Rights = RightsStatement.PermissionFromRightsHolder,
                RightsHolder = T("Grandfather Example", "Großvater Beispiel"),
                ConsentText = T("I agree that the archive may keep what I send in, describe it and publish it on this website, also where a link to it is shared and in its list of what is new. If I chose so above, my name is shown with it. I can ask the archive to take it down at any time, but it cannot reach copies others have made.",
                    "Ich bin einverstanden, dass das Archiv meinen Beitrag aufbewahrt, beschreibt und auf dieser Website veröffentlicht, auch dort, wo ein Link darauf geteilt wird, und in seiner Liste der Neuigkeiten. Wenn ich es oben gewählt habe, wird mein Name dabei angezeigt. Ich kann das Archiv jederzeit bitten, ihn wieder zu entfernen, aber es kann Kopien nicht erreichen, die andere gemacht haben."),
                ConsentCoversSharing = true,
                SubmittedAt = now,
            },
        };
        waiting.TrySetDate(T("1970s", "1970er Jahre"));
        items.Add(waiting);
        // Last, so that the items before it keep their numbers. A story without a picture, as many are.
        items.Add(Published(club, T("What the old goalkeeper told us", "Was der alte Torwart uns erzählte"), T("1950s", "1950er Jahre"),
                T("Karl kept goal for eleven seasons. He says the pitch sloped so much that you could only see the far goal from the top of the hill. No photo of him has been found yet.",
                  "Karl stand elf Spielzeiten im Tor. Er sagt, der Platz sei so schief gewesen, dass man das andere Tor nur von oben sehen konnte. Ein Foto von ihm ist noch nicht gefunden."),
                [T("football", "Fußball"), T("memories", "Erinnerungen")], scene: null));
        await AddPhotoAsync(waiting, "grandmother-shop", DemoImages.Render("grandmother-shop"));

        foreach (var (item, scene) in scenes)
        {
            if (scene is not null)
                await AddPhotoAsync(item, scene, DemoImages.Render(scene));
        }

        await AddPhotoAsync(letter, "letter-page-1", DemoLetter.Render(letterPages[0], seed: 1),
            T("First page", "Erste Seite"), T("A handwritten letter in blue ink, first page, folded once across the middle.", "Ein handgeschriebener Brief in blauer Tinte, erste Seite, einmal in der Mitte gefaltet."));
        await AddPhotoAsync(letter, "letter-page-2", DemoLetter.Render(letterPages[1], seed: 2),
            T("Second page, with the chairman's signature", "Zweite Seite, mit der Unterschrift des Vorsitzenden"), T("The second page of the handwritten letter, ending with the chairman's name.", "Die zweite Seite des handgeschriebenen Briefs, am Ende der Name des Vorsitzenden."));

        // A few catalogue details, as a group would keep them. The photos are drawn for this demo, so anyone may use them.
        var number = 0;
        foreach (var item in items.Where(i => i.Status == ItemStatus.Published))
        {
            item.ReferenceNumber = $"LHG-{++number:000}";
            item.OriginalLocation = T($"Box {1 + (number / 4)}", $"Karton {1 + (number / 4)}");
            item.RightsUri = RightsOptions.CcBy;
            item.Credit = T("Drawing: demo archive", "Zeichnung: Demo-Archiv");
        }

        db.ArchiveItems.AddRange(items);
        // A made-up group, so the demo shows an archive as a group would set it up.
        db.ArchiveSettings.Add(new ArchiveSettings { DefaultLanguage = language, PrivacyReviewed = true, ImprintNotNeeded = true, DefaultRightsUri = RightsOptions.NotEvaluated });
        void Text(ArchiveTextKind kind, string text) => db.ArchiveTexts.Add(new ArchiveText { Language = language, Kind = kind, Text = text });
        Text(ArchiveTextKind.Name, T("Lindenau History Group", "Geschichtsverein Lindenau"));
        Text(ArchiveTextKind.Tagline, T("Photos and memories from a town that does not exist.", "Fotos und Erinnerungen aus einem Ort, den es nicht gibt."));
        Text(ArchiveTextKind.About, T(
            "The Lindenau History Group is made up, like everything in this demo. A real group would say here who they are and how the archive came about.",
            "Den Geschichtsverein Lindenau gibt es nicht, wie alles in dieser Demo. Eine echte Gruppe würde hier sagen, wer sie ist und wie das Archiv entstanden ist."));
        Text(ArchiveTextKind.Contact, T(
            "A real group would say here how to reach them, for example: archive@example.org",
            "Eine echte Gruppe würde hier sagen, wie sie zu erreichen ist, zum Beispiel: archiv@example.org"));

        await db.SaveChangesAsync();
        logger.LogInformation("Filled the empty archive with demo content ({Language}).", language);

        Collection Collection(string title, string description) =>
            new() { Title = title, Description = description, CreatedAt = now };

        ArchiveItem Published(Collection collection, string title, string date, string description, string[] tagNames, string? scene, string? contributor = null)
        {
            var item = new ArchiveItem { Title = title, Description = description, Collection = collection, CreatedAt = now, UpdatedAt = now };
            if (!item.TrySetDate(date))
                throw new InvalidOperationException($"The demo date '{date}' cannot be read.");
            item.SetTags(tagNames.Select(TagFor));
            if (contributor is not null)
            {
                item.Contribution = new Contribution
                {
                    ContributionLink = null,
                    ContributorName = contributor,
                    ShowName = true,
                    Rights = RightsStatement.CreatedByContributor,
                    ConsentText = T("Agreed in conversation; recorded by a curator.", "Im Gespräch zugestimmt; von der Kuratorin erfasst."),
                    ConsentCoversSharing = true,
                    SubmittedAt = now,
                };
            }
            item.Publish(now);
            scenes.Add((item, scene));
            return item;
        }

        async Task AddPhotoAsync(ArchiveItem item, string name, byte[] jpeg, string? caption = null, string? altText = null)
        {
            var file = new MediaFile
            {
                ArchiveItem = item,
                OriginalFileName = name + ".jpg",
                Position = item.MediaFiles.Count,
                Caption = caption,
                AltText = altText,
                ContentType = "image/jpeg",
                SizeBytes = jpeg.Length,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(jpeg)),
                StorageId = Guid.NewGuid(),
                // The background service creates previews for pending files when the app starts.
                Status = ProcessingStatus.Pending,
                UploadedAt = now,
            };
            await storage.SaveAsync(file.OriginalKey, new MemoryStream(jpeg));
            item.MediaFiles.Add(file);
        }

        // Items share tags with the same name, as they would if a curator typed them.
        Tag TagFor(string name)
        {
            var normalized = Tag.Normalize(name);
            if (!tags.TryGetValue(normalized, out var tag))
                tags[normalized] = tag = new Tag { Name = name, NormalizedName = normalized };
            return tag;
        }
    }
}
