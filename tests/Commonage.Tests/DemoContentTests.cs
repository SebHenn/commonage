using Commonage.Web.Data;
using Commonage.Web.Demo;
using Commonage.Web.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetVips;
using Npgsql;

namespace Commonage.Tests;

[Collection(nameof(AppCollection))]
public class DemoContentTests(CommonageAppFactory app)
{
    [Theory]
    [InlineData("en", "Flood in Mill Street", "This is a demo archive")]
    [InlineData("de", "Hochwasser in der Mühlenstraße", "Das ist ein Demo-Archiv")]
    public async Task An_empty_archive_is_filled_with_demo_content_in_the_chosen_language(string language, string title, string notice)
    {
        var connectionString = await CreateEmptyDatabaseAsync();
        await using var demo = app.WithWebHostBuilder(b => b
            .UseSetting("ConnectionStrings:Default", connectionString)
            .UseSetting("Demo:Language", language));
        using var visitor = demo.CreateClient();
        using var german = new HttpRequestMessage(HttpMethod.Get, "/search");
        german.Headers.Add("Cookie", $".AspNetCore.Culture=c%3D{language}%7Cuic%3D{language}");

        var search = await (await visitor.SendAsync(german)).Content.ReadAsStringAsync();

        using var scope = demo.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommonageDbContext>();
        Assert.Contains(title, search);
        Assert.Contains(notice, search);
        Assert.Equal(3, await db.Collections.CountAsync());
        Assert.Equal(9, await db.ArchiveItems.CountAsync(i => i.Status == ItemStatus.Published));
        Assert.Equal(1, await db.ArchiveItems.CountAsync(i => i.Status == ItemStatus.Draft));
        Assert.Equal(1, await db.ArchiveItems.CountAsync(i => i.Status == ItemStatus.Submitted));
        Assert.True(await db.ContributionLinks.AnyAsync(l => l.IsActive));
        Assert.Equal(10, await db.MediaFiles.CountAsync());
        Assert.True(await db.ArchiveItems.Where(i => i.Status == ItemStatus.Published).AllAsync(i => i.DateText != null));
    }

    [Fact]
    public async Task An_archive_with_content_gets_no_demo_content()
    {
        var connectionString = await CreateEmptyDatabaseAsync();
        await using (var plain = app.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Default", connectionString)))
        {
            using var scope = plain.Services.CreateScope();
            var existing = scope.ServiceProvider.GetRequiredService<CommonageDbContext>();
            existing.Collections.Add(new Collection { Title = "Our own collection", CreatedAt = DateTime.UtcNow });
            await existing.SaveChangesAsync();
        }

        await using var demo = app.WithWebHostBuilder(b => b
            .UseSetting("ConnectionStrings:Default", connectionString)
            .UseSetting("Demo:Language", "en"));
        using var demoScope = demo.Services.CreateScope();
        var db = demoScope.ServiceProvider.GetRequiredService<CommonageDbContext>();
        Assert.Equal(["Our own collection"], await db.Collections.Select(c => c.Title).ToListAsync());
        Assert.False(await db.ArchiveItems.AnyAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_demo_letter_is_all_written_out_on_pages_large_enough_to_need_zooming(bool german)
    {
        foreach (var words in DemoLetter.Pages(german))
        {
            foreach (var paragraph in words)
                Assert.True(DemoLetter.CanWrite(paragraph), $"Something in \"{paragraph}\" would be missing from the page.");

            using var page = Image.NewFromBuffer(DemoLetter.Render(words, seed: 1));
            Assert.Equal((DemoLetter.Width, DemoLetter.Height), (page.Width, page.Height));
            Assert.True(Math.Max(page.Width, page.Height) > 1600, "It has to be larger than a preview, or there is no large size to try.");
            Assert.Equal("jpegload_buffer", page.Get("vips-loader"));
            Assert.DoesNotContain("exif-data", page.GetFields());
        }
    }

    [Fact]
    public void Every_demo_scene_renders_as_a_jpeg_without_metadata()
    {
        foreach (var scene in DemoImages.Scenes)
        {
            using var image = Image.NewFromBuffer(DemoImages.Render(scene));
            Assert.Equal((DemoImages.Width, DemoImages.Height), (image.Width, image.Height));
            Assert.Equal("jpegload_buffer", image.Get("vips-loader"));
            Assert.DoesNotContain("exif-data", image.GetFields());
        }
    }

    private async Task<string> CreateEmptyDatabaseAsync()
    {
        var name = "demo_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(app.ConnectionString))
        {
            await admin.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", admin);
            await command.ExecuteNonQueryAsync();
        }
        return new NpgsqlConnectionStringBuilder(app.ConnectionString) { Database = name }.ConnectionString;
    }
}
