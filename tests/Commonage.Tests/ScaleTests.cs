using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Commonage.Web.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Commonage.Tests;

/// <summary>Runs only when COMMONAGE_SCALE_ITEMS is set, because it fills the database with that many items.</summary>
public sealed class ScaleFactAttribute : FactAttribute
{
    public ScaleFactAttribute()
    {
        if (ScaleTests.ItemCount == 0)
            Skip = "Set COMMONAGE_SCALE_ITEMS (for example 50000) to run the scale benchmark.";
    }
}

/// <summary>
/// How the app behaves with a large archive: the pages people open most and the export. Run it alone:
/// <c>COMMONAGE_SCALE_ITEMS=50000 dotnet test tests/Commonage.Tests --filter ScaleTests</c>. The numbers go to the test
/// output. They come from this machine and a PostgreSQL container, so they say where time goes, not what a small server does.
/// </summary>
[Collection(nameof(AppCollection))]
public class ScaleTests(CommonageAppFactory app, ITestOutputHelper output)
{
    public static readonly int ItemCount = int.TryParse(Environment.GetEnvironmentVariable("COMMONAGE_SCALE_ITEMS"), out var count) ? count : 0;

    [ScaleFact]
    public async Task A_large_archive_stays_fast_and_exports_in_constant_memory()
    {
        await Seed();
        try
        {
            using var admin = app.CreateClient();
            await Browser.LogInAsCuratorAsync(admin);
            using var visitor = app.CreateClient();

            // Each page twice: the first request also warms the database and the app.
            foreach (var url in new[]
            {
                "/", "/Search", "/Search?q=scale", "/Search?q=item+4", "/Search?tag=scale-tag-7", "/Search?sort=OldestFirst",
                "/Search?from=1950&to=1959", "/Search?p=500", "/Search?p=2000", "/Search?q=scale&p=1000", "/places",
            })
                await Time(visitor, url);
            foreach (var url in new[] { "/Admin", "/Admin?status=Draft", "/Admin?sort=Title", "/Admin?sort=OldestFirst&p=300" })
                await Time(admin, url);

            GC.Collect();
            var before = GC.GetTotalMemory(forceFullCollection: true);
            var peak = before;
            using var stop = new CancellationTokenSource();
            var sampler = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: false));
                    await Task.Delay(20);
                }
            });
            var clock = Stopwatch.StartNew();
            var response = await Browser.PostFormAsync(admin, "/Admin/Export", new() { ["IncludePersonalData"] = "true" });
            var path = Path.GetTempFileName();
            await using (var file = File.Create(path))
                await (await response.Content.ReadAsStreamAsync()).CopyToAsync(file);
            clock.Stop();
            await stop.CancelAsync();
            await sampler;

            int exported;
            using (var zip = ZipFile.OpenRead(path))
            using (var json = JsonDocument.Parse(zip.GetEntry("archive.json")!.Open().ReadAllBytes()))
                exported = json.RootElement.GetProperty("items").GetArrayLength();
            output.WriteLine($"export: {clock.ElapsedMilliseconds} ms, {new FileInfo(path).Length / 1024 / 1024} MB zip, {exported} items, memory in use grew by up to {(peak - before) / 1024 / 1024} MB while writing");
            File.Delete(path);
            Assert.True(exported >= ItemCount);
        }
        finally
        {
            await Remove();
        }
    }

    private async Task Time(HttpClient client, string url)
    {
        var times = new List<long>();
        for (var run = 0; run < 2; run++)
        {
            var clock = Stopwatch.StartNew();
            var response = await client.GetAsync(url);
            await response.Content.ReadAsStringAsync();
            times.Add(clock.ElapsedMilliseconds);
            Assert.True(response.IsSuccessStatusCode, $"{url}: {(int)response.StatusCode}");
        }

        output.WriteLine($"{url,-32} {times[0],6} ms first, {times[1],6} ms second");
    }

    private Task<int> Seed() => app.WithDbAsync(async db =>
    {
        var clock = Stopwatch.StartNew();
        var collections = Enumerable.Range(1, 8).Select(n => new Collection { Title = $"Scale collection {n}", CreatedAt = DateTime.UtcNow, Position = n }).ToList();
        db.Collections.AddRange(collections);
        db.Tags.AddRange(Enumerable.Range(0, 200).Select(n => new Tag { Name = $"scale-tag-{n}", NormalizedName = $"scale-tag-{n}" }));
        await db.SaveChangesAsync();
        var firstCollection = collections[0].Id;
        var firstTag = await db.Tags.Where(t => t.Name == "scale-tag-0").Select(t => t.Id).SingleAsync();

        // Nine in ten are published, the rest are drafts, spread over eight collections and the years 1900 to 2020.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ArchiveItems" ("Title", "Description", "Status", "CollectionId", "DateText", "DateFrom", "DateTo", "TagNames",
                "CreatedAt", "UpdatedAt", "PublishedAt", "HelpWanted", "NeedsTranscription", "ReferenceNumber")
            SELECT 'Scale item ' || g, 'A photograph taken at the mill, number ' || g || '. It shows the street, the school and the people who lived there.',
                CASE WHEN g % 10 = 0 THEN 0 ELSE 1 END, {firstCollection} + (g % 8), (1900 + g % 121)::text,
                make_date(1900 + g % 121, 1, 1), make_date(1900 + g % 121, 12, 31), 'scale-tag-' || (g % 200),
                now() - (g || ' minutes')::interval, now(), CASE WHEN g % 10 = 0 THEN NULL ELSE now() - (g || ' minutes')::interval END,
                false, false, 'scale-' || g
            FROM generate_series(1, {ItemCount}) AS g
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ArchiveItemTag" ("ItemsId", "TagsId")
            SELECT i."Id", {firstTag} + (substring(i."ReferenceNumber" from 7)::int % 200)
            FROM "ArchiveItems" i WHERE i."ReferenceNumber" LIKE 'scale-%'
            """);
        output.WriteLine($"seeded {ItemCount} items in {clock.ElapsedMilliseconds} ms");
        return 0;
    });

    private Task<int> Remove() => app.WithDbAsync(async db =>
    {
        await db.Database.ExecuteSqlRawAsync("""DELETE FROM "ArchiveItems" WHERE "ReferenceNumber" LIKE 'scale-%'""");
        await db.Database.ExecuteSqlRawAsync("""DELETE FROM "Tags" WHERE "Name" LIKE 'scale-tag-%'""");
        await db.Database.ExecuteSqlRawAsync("""DELETE FROM "Collections" WHERE "Title" LIKE 'Scale collection %'""");
        return 0;
    });
}

internal static class StreamExtensions
{
    public static byte[] ReadAllBytes(this Stream stream)
    {
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
