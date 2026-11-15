using System.Net;
using System.Text;
using Commonage.Web.Domain;
using Commonage.Web.Media;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Tests;

/// <summary>A test that needs poppler, the program that reads PDFs. It is skipped on a machine without it; CI has it.</summary>
public sealed class PdfFactAttribute : FactAttribute
{
    public PdfFactAttribute()
    {
        if (!PopplerIsInstalled)
            Skip = "poppler (pdfinfo, pdftoppm) is not installed on this machine.";
    }

    private static bool PopplerIsInstalled { get; } = Check();

    private static bool Check()
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("pdfinfo", "-v")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            })!;
            process.WaitForExit(10_000);
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}

[Collection(nameof(AppCollection))]
public class PdfTests(CommonageAppFactory app)
{
    /// <summary>A small valid PDF with one line of text on each page.</summary>
    internal static byte[] MakePdf(int pages, string word = "")
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(' ', Enumerable.Range(0, pages).Select(i => $"{4 + i * 2} 0 R"))}] /Count {pages} >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        for (var page = 1; page <= pages; page++)
        {
            var content = $"BT /F1 24 Tf 50 700 Td (Page {page} {word}) Tj ET";
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents {5 + (page - 1) * 2} 0 R >>");
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }

        var text = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(text.Length);
            text.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = text.Length;
        text.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            text.Append($"{offset:D10} 00000 n \n");
        text.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }

    [PdfFact]
    public async Task A_curator_adds_a_PDF_and_visitors_page_through_it_as_pictures_without_the_file()
    {
        using var curator = app.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);
        var item = await app.CreateContributionAsync(ItemStatus.Draft);
        var pdf = MakePdf(3);

        await Browser.UploadAsync(curator, $"/Admin/Items/Edit/{item.Id}", "Upload", "programme.pdf", pdf);
        var file = await WaitForProcessingAsync(item.Id);

        Assert.Equal(ProcessingStatus.Ready, file.Status);
        Assert.Equal("application/pdf", file.ContentType);
        Assert.Equal(3, file.PageCount);
        Assert.Equal(pdf, await curator.GetByteArrayAsync($"/admin/media/{file.Id}/original"));

        using var visitor = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/media/{file.Id}/page/1")).StatusCode);

        await app.WithDbAsync(async db =>
        {
            var stored = await db.ArchiveItems.SingleAsync(i => i.Id == item.Id);
            stored.Publish(DateTime.UtcNow);
            return await db.SaveChangesAsync();
        });

        var second = await visitor.GetAsync($"/media/{file.Id}/page/2");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("image/jpeg", second.Content.Headers.ContentType?.MediaType);
        using var picture = NetVips.Image.NewFromBuffer(await second.Content.ReadAsByteArrayAsync());
        Assert.True(picture.Width > 100);
        Assert.DoesNotContain(picture.GetFields(), field => field.StartsWith("exif", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/media/{file.Id}/page/4")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/media/{file.Id}/page/0")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync($"/media/{file.Id}/preview")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await visitor.GetAsync($"/admin/media/{file.Id}/original")).StatusCode);
    }

    [PdfFact]
    public async Task A_file_that_only_starts_like_a_PDF_fails_without_pages_and_without_stopping_the_archive()
    {
        using var curator = app.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);
        var item = await app.CreateContributionAsync(ItemStatus.Draft);

        await Browser.UploadAsync(curator, $"/Admin/Items/Edit/{item.Id}", "Upload", "broken.pdf", "%PDF-1.4\nthis is not a document"u8.ToArray());
        var file = await WaitForProcessingAsync(item.Id);

        Assert.Equal(ProcessingStatus.Failed, file.Status);
        Assert.Null(file.PageCount);
        Assert.NotNull(file.ProcessingError);
    }

    [PdfFact]
    public async Task A_long_PDF_gets_previews_for_the_first_pages_only()
    {
        using var curator = app.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);
        var item = await app.CreateContributionAsync(ItemStatus.Draft);

        await Browser.UploadAsync(curator, $"/Admin/Items/Edit/{item.Id}", "Upload", "register.pdf", MakePdf(MediaFile.MaxPdfPages + 2));
        var file = await WaitForProcessingAsync(item.Id, seconds: 240);

        Assert.Equal(ProcessingStatus.Ready, file.Status);
        Assert.Equal(MediaFile.MaxPdfPages, file.PageCount);
    }

    [Fact]
    public async Task A_PDF_is_refused_with_a_clear_message_when_the_archive_has_no_program_to_read_it()
    {
        await using var without = app.WithWebHostBuilder(b => b.UseSetting("Uploads:PdfToolsDirectory", Path.Combine(Path.GetTempPath(), "no-such-folder-" + Guid.NewGuid().ToString("N"))));
        using var curator = without.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);
        var item = await app.CreateContributionAsync(ItemStatus.Draft);

        var response = await Browser.UploadAsync(curator, $"/Admin/Items/Edit/{item.Id}", "Upload", "programme.pdf", MakePdf(1));

        Assert.Contains("this archive was set up without the program that reads PDFs", await response.Content.ReadAsStringAsync());
        Assert.Empty(await app.WithDbAsync(db => db.MediaFiles.Where(f => f.ArchiveItemId == item.Id).ToListAsync()));
    }

    [Fact]
    public async Task Text_read_from_documents_is_found_for_published_items_only()
    {
        var word = TestData.UniqueWord("Pdfword");
        var published = await app.CreateContributionAsync(ItemStatus.Published);
        var draft = await app.CreateContributionAsync(ItemStatus.Draft);
        var binned = await app.CreateContributionAsync(ItemStatus.Published);
        await app.WithDbAsync(async db =>
        {
            foreach (var item in new[] { published, draft, binned })
                db.ItemFileTexts.Add(new ItemFileText { ArchiveItemId = item.Id, Text = "minutes of the meeting " + word });
            await db.SaveChangesAsync();
            (await db.ArchiveItems.SingleAsync(i => i.Id == binned.Id)).MoveToBin(DateTime.UtcNow);
            return await db.SaveChangesAsync();
        });

        using var visitor = app.CreateClient();
        var page = await visitor.GetStringAsync($"/search?q={word}");

        Assert.Contains("Items found: 1", page);
        Assert.Contains($"/items/{published.Id}", page);
        Assert.DoesNotContain($"/items/{draft.Id}\"", page);
        Assert.DoesNotContain($"/items/{binned.Id}\"", page);
    }

    [PdfFact]
    public async Task A_PDF_is_found_by_its_text_until_it_goes_to_the_bin_and_again_after_it_comes_back()
    {
        var word = TestData.UniqueWord("Scanword");
        using var curator = app.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);
        var item = await app.CreateContributionAsync(ItemStatus.Published);

        await Browser.UploadAsync(curator, $"/Admin/Items/Edit/{item.Id}", "Upload", "minutes.pdf", MakePdf(3, word));
        var file = await WaitForProcessingAsync(item.Id);
        Assert.Equal(ProcessingStatus.Ready, file.Status);
        using var visitor = app.CreateClient();
        // The text is indexed just after the file is marked ready, so search until it is found.
        var found = "";
        for (var attempt = 0; attempt < 100 && !found.Contains("Items found: 1"); attempt++)
        {
            found = await visitor.GetStringAsync($"/search?q={word}");
            if (!found.Contains("Items found: 1"))
                await Task.Delay(100);
        }
        if (!found.Contains("Items found: 1"))
        {
            // Shown only on a failure: what the database holds, so that a failure on a machine that is not this one can be understood.
            var state = await app.WithDbAsync(async db =>
            {
                var stored = await db.MediaFiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(f => f.Id == file.Id);
                var row = await db.ItemFileTexts.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(t => t.ArchiveItemId == item.Id);
                return $"status {stored.Status}, error '{stored.ProcessingError}', extracted '{stored.ExtractedText}', index row '{row?.Text}', word '{word}'";
            });
            Assert.Fail("The text was not found. " + state);
        }
        var itemPage = await visitor.GetStringAsync($"/items/{item.Id}");
        Assert.Contains($"/media/{file.Id}/page/3", itemPage);
        Assert.Contains("Page 2 of 3", itemPage);

        await app.WithDbAsync(async db =>
        {
            (await db.MediaFiles.SingleAsync(f => f.Id == file.Id)).DeletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await FileTextIndex.RebuildAsync(db, item.Id, default);
            return await db.SaveChangesAsync();
        });
        Assert.Contains("Items found: 0", await visitor.GetStringAsync($"/search?q={word}"));

        await app.WithDbAsync(async db =>
        {
            (await db.MediaFiles.IgnoreQueryFilters().SingleAsync(f => f.Id == file.Id)).DeletedAt = null;
            await db.SaveChangesAsync();
            await FileTextIndex.RebuildAsync(db, item.Id, default);
            return await db.SaveChangesAsync();
        });
        Assert.Contains("Items found: 1", await visitor.GetStringAsync($"/search?q={word}"));
    }

    private async Task<MediaFile> WaitForProcessingAsync(int itemId, int seconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            var file = await app.WithDbAsync(db => db.MediaFiles.AsNoTracking().SingleAsync(f => f.ArchiveItemId == itemId));
            if (file.Status != ProcessingStatus.Pending || DateTime.UtcNow > deadline)
                return file;
            await Task.Delay(100);
        }
    }
}
