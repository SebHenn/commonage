using System.Net;
using Commonage.Web.Forms;
using Commonage.Web.Operations;
using Commonage.Web.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Commonage.Tests;

/// <summary>Each test sets up an archive with a database of its own, because settings apply to everything in an archive.</summary>
[Collection(nameof(AppCollection))]
public class ArchiveSettingsTests(CommonageAppFactory app)
{
    private async Task<WebApplicationFactory<Program>> NewArchiveAsync(string? demo = null)
    {
        var name = "settings_" + Guid.NewGuid().ToString("N");
        await using (var connection = new NpgsqlConnection(app.ConnectionString))
        {
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
            await create.ExecuteNonQueryAsync();
        }
        var connectionString = new NpgsqlConnectionStringBuilder(app.ConnectionString) { Database = name }.ConnectionString;
        return app.WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Default", connectionString);
            if (demo is not null)
                b.UseSetting("Demo:Language", demo);
        });
    }

    private static async Task<HttpClient> AdminAsync(WebApplicationFactory<Program> archive)
    {
        var admin = archive.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        return admin;
    }

    /// <summary>Saves the settings form with its defaults, changed as given.</summary>
    private static Task<HttpResponseMessage> SaveAsync(HttpClient admin, Dictionary<string, string> changes, params (string FileName, byte[] Content)[] logo)
    {
        var fields = new Dictionary<string, string>
        {
            ["Input.DefaultLanguage"] = "en",
            ["Input.AllowSearchEngines"] = "true",
            ["Input.UseStandardColor"] = "true",
        };
        foreach (var (key, value) in changes)
            fields[key] = value;
        return SendSettingsAsync(admin, fields, logo);
    }

    private static async Task<HttpResponseMessage> SendSettingsAsync(HttpClient admin, Dictionary<string, string> fields, (string FileName, byte[] Content)[] logo)
    {
        var page = await admin.GetStringAsync("/Admin/Settings");
        var token = System.Text.RegularExpressions.Regex.Match(page, @"name=""__RequestVerificationToken"" type=""hidden"" value=""([^""]+)""").Groups[1].Value;
        var form = new MultipartFormDataContent { { new StringContent(token), "__RequestVerificationToken" } };
        foreach (var (key, value) in fields)
            form.Add(new StringContent(value), key);
        foreach (var (fileName, content) in logo)
            form.Add(new ByteArrayContent(content), "Logo", fileName);
        return await admin.PostAsync("/Admin/Settings", form);
    }

    [Fact]
    public async Task Without_settings_the_archive_works_under_the_softwares_name()
    {
        await using var archive = await NewArchiveAsync();
        using var visitor = archive.CreateClient();

        var home = await visitor.GetStringAsync("/");

        Assert.Contains("<title>Commonage</title>", home);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/about")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/imprint")).StatusCode);
        Assert.DoesNotContain("href=\"/about\"", home);
        Assert.Contains("href=\"/privacy\"", home);
        Assert.Contains("only sets the cookies it needs", await visitor.GetStringAsync("/privacy"));
    }

    [Fact]
    public async Task The_name_and_pages_an_admin_sets_appear_everywhere_and_a_page_left_empty_does_not()
    {
        await using var archive = await NewArchiveAsync();
        using var admin = await AdminAsync(archive);

        var saved = await SaveAsync(admin, new()
        {
            ["Input.Name"] = "Mill Street History Group",
            ["Input.Tagline"] = "Memories of one street",
            ["Input.Contact"] = "Write to archive@example.org, or see https://example.org/people.",
        });
        using var visitor = archive.CreateClient();
        var home = await visitor.GetStringAsync("/");
        var contact = await visitor.GetStringAsync("/contact");

        Assert.Contains("Your changes are saved", await saved.Content.ReadAsStringAsync());
        Assert.Contains("<title>Mill Street History Group</title>", home);
        Assert.Contains("- Mill Street History Group</title>", await visitor.GetStringAsync("/search"));
        Assert.Contains("Memories of one street", home);
        Assert.Contains("Made with Commonage", home);
        Assert.Contains("href=\"/contact\"", home);
        Assert.Contains("href=\"mailto:archive@example.org\"", contact);
        Assert.Contains("href=\"https://example.org/people\"", contact);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/about")).StatusCode);
    }

    [Fact]
    public async Task Only_admins_can_change_the_settings()
    {
        using var admin = app.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        var (curator, _, _, _) = await app.CreateAccountAsync(admin);
        using var visitor = app.CreateClient();

        var asVisitor = await visitor.GetAsync("/Admin/Settings");
        var asCurator = await curator.GetAsync("/Admin/Settings");

        Assert.Contains("/Account/Login", asVisitor.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Contains("/Account/AccessDenied", asCurator.RequestMessage!.RequestUri!.AbsolutePath);
    }

    [Theory]
    [InlineData("#ffee00", false)]
    [InlineData("#0d6efd", true)]
    [InlineData("#1a5f7a", true)]
    public async Task A_colour_too_light_to_read_is_refused_with_one_that_works(string color, bool accepted)
    {
        await using var archive = await NewArchiveAsync();
        using var admin = await AdminAsync(archive);

        var response = await SaveAsync(admin, new() { ["Input.UseStandardColor"] = "false", ["Input.AccentColor"] = color });
        var page = await response.Content.ReadAsStringAsync();

        Assert.Equal(!accepted, page.Contains("too light"));
        if (!accepted)
        {
            var suggestion = System.Text.RegularExpressions.Regex.Match(page, "A similar colour that works is (#[0-9a-f]{6})").Groups[1].Value;
            Assert.True(AccentColor.TryParse(suggestion, out var suggested));
            Assert.True(AccentColor.ContrastWithWhite(suggested) >= AccentColor.MinimumContrast);
        }
    }

    [Fact]
    public async Task A_chosen_colour_reaches_buttons_and_links_and_changes_the_stylesheets_address()
    {
        await using var archive = await NewArchiveAsync();
        using var admin = await AdminAsync(archive);
        using var visitor = archive.CreateClient();
        Assert.DoesNotContain("branding.css", await visitor.GetStringAsync("/"));

        await SaveAsync(admin, new() { ["Input.UseStandardColor"] = "false", ["Input.AccentColor"] = "#1a5f7a" });
        var first = System.Text.RegularExpressions.Regex.Match(await visitor.GetStringAsync("/"), @"/branding\.css\?v=\w+").Value;
        var css = await visitor.GetStringAsync(first);
        await SaveAsync(admin, new() { ["Input.UseStandardColor"] = "false", ["Input.AccentColor"] = "#7a1a5f" });
        var second = System.Text.RegularExpressions.Regex.Match(await visitor.GetStringAsync("/"), @"/branding\.css\?v=\w+").Value;

        Assert.Contains(".btn-primary", css);
        Assert.Contains("--bs-btn-bg: #1a5f7a", css);
        Assert.Contains("--bs-link-color-rgb: 26, 95, 122", css);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task A_logo_is_stored_without_its_metadata_and_a_new_one_replaces_it()
    {
        await using var archive = await NewArchiveAsync();
        using var admin = await AdminAsync(archive);
        using var visitor = archive.CreateClient();

        var saved = await SaveAsync(admin, new(), ("logo.jpg", TestData.Photo));
        Assert.Contains("Your changes are saved", await saved.Content.ReadAsStringAsync());
        var address = System.Text.RegularExpressions.Regex.Match(await visitor.GetStringAsync("/"), @"/branding/[0-9a-f-]+/logo").Value;
        var bytes = await visitor.GetByteArrayAsync(address);

        using (var image = NetVips.Image.NewFromBuffer(bytes))
        {
            Assert.True(image.Width <= 256 && image.Height <= 256);
            Assert.DoesNotContain("exif-data", image.GetFields());
        }
        Assert.Equal(0x89, bytes[0]);

        await SaveAsync(admin, new(), ("logo.jpg", TestData.Photo));
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync(address)).StatusCode);
    }

    [Fact]
    public async Task A_file_that_is_no_image_is_refused_as_a_logo()
    {
        await using var archive = await NewArchiveAsync();
        using var admin = await AdminAsync(archive);

        var response = await SaveAsync(admin, new(), ("logo.svg", "<svg onload='alert(1)'/>"u8.ToArray()));

        Assert.Contains("The logo must be", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Text_on_a_page_cannot_contain_markup()
    {
        await using var archive = await NewArchiveAsync();
        using var admin = await AdminAsync(archive);

        await SaveAsync(admin, new() { ["Input.About"] = "<script>alert(1)</script> https://example.org/\"onmouseover=\"alert(1) javascript:alert(1)" });
        using var visitor = archive.CreateClient();
        var page = await visitor.GetStringAsync("/about");

        Assert.DoesNotContain("<script>alert", page);
        Assert.DoesNotContain("onmouseover=\"alert", page);
        Assert.DoesNotContain("href=\"javascript", page);
    }

    [Theory]
    [InlineData("Write to a@example.org.", "mailto:a@example.org", "a@example.org.")]
    [InlineData("(see https://example.org/x).", "https://example.org/x", "https://example.org/x).")]
    public void Addresses_become_links_without_the_punctuation_after_them(string text, string href, string notLinked)
    {
        using var writer = new StringWriter();
        PlainText.WithLinks(text).WriteTo(writer, System.Text.Encodings.Web.HtmlEncoder.Default);

        Assert.Contains($"href=\"{href}\"", writer.ToString());
        Assert.DoesNotContain($">{notLinked}<", writer.ToString());
    }

    [Fact]
    public async Task The_archives_language_applies_when_the_browser_asks_for_none_it_offers()
    {
        await using var archive = await NewArchiveAsync();
        using var admin = await AdminAsync(archive);
        await SaveAsync(admin, new() { ["Input.DefaultLanguage"] = "de" });
        using var visitor = archive.CreateClient();

        using var french = new HttpRequestMessage(HttpMethod.Get, "/search");
        french.Headers.Add("Accept-Language", "fr");
        using var english = new HttpRequestMessage(HttpMethod.Get, "/search");
        english.Headers.Add("Accept-Language", "en");

        Assert.Contains("<html lang=\"de\"", await (await visitor.SendAsync(french)).Content.ReadAsStringAsync());
        Assert.Contains("<html lang=\"en\"", await (await visitor.SendAsync(english)).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Curator_pages_use_the_archives_time_zone()
    {
        await using var archive = await NewArchiveAsync();
        using var admin = await AdminAsync(archive);

        await SaveAsync(admin, new() { ["Input.TimeZone"] = "Asia/Tokyo" });
        var tokyo = await admin.GetStringAsync("/Admin/Activity");
        await SaveAsync(admin, new() { ["Input.TimeZone"] = "America/New_York" });
        var newYork = await admin.GetStringAsync("/Admin/Activity");

        string Hour(string page) => System.Text.RegularExpressions.Regex.Match(page, @"<time[^>]*>([^<]+)</time>").Groups[1].Value;
        Assert.NotEqual(Hour(tokyo), Hour(newYork));
    }

    [Fact]
    public async Task An_unknown_time_zone_is_refused()
    {
        await using var archive = await NewArchiveAsync();
        using var admin = await AdminAsync(archive);

        var response = await SaveAsync(admin, new() { ["Input.TimeZone"] = "Moon/Base" });

        Assert.Contains("Please choose one of the time zones", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Search_engines_are_kept_from_private_pages_always_and_from_the_archive_on_request()
    {
        await using var archive = await NewArchiveAsync();
        using var admin = await AdminAsync(archive);
        using var visitor = archive.CreateClient();
        const string noindex = "name=\"robots\" content=\"noindex\"";

        Assert.DoesNotContain(noindex, await visitor.GetStringAsync("/"));
        Assert.Contains(noindex, await visitor.GetStringAsync("/Account/Login"));
        var open = await visitor.GetStringAsync("/robots.txt");
        Assert.Contains("Disallow: /Admin", open);
        Assert.DoesNotContain("Disallow: /\n", open);

        await SaveAsync(admin, new() { ["Input.AllowSearchEngines"] = "false" });

        Assert.Contains(noindex, await visitor.GetStringAsync("/"));
        Assert.Contains("Disallow: /\n", await visitor.GetStringAsync("/robots.txt"));
    }

    [Fact]
    public async Task The_getting_started_list_shows_what_is_left_and_disappears_when_all_is_done()
    {
        var root = Path.Combine(Path.GetTempPath(), "commonage-settings-tests", Guid.NewGuid().ToString("N"));
        await using var plain = await NewArchiveAsync();
        await using var archive = plain.WithWebHostBuilder(b => b.UseSetting("Storage:RootPath", Path.Combine(root, "media")));
        try
        {
            using var admin = await AdminAsync(archive);
            var start = await admin.GetStringAsync("/Admin");
            Assert.Contains("Getting started", start);
            Assert.Contains("Give the archive its name", start);

            await SaveAsync(admin, new()
            {
                ["Input.Name"] = "A group",
                ["Input.Contact"] = "a@example.org",
                ["Input.PrivacyReviewed"] = "true",
                ["Input.ImprintNotNeeded"] = "true",
            });
            await Browser.PostFormAsync(admin, "/Admin/Collections/Edit", new() { ["Input.Title"] = "First" });
            var collectionPage = await admin.GetStringAsync("/Admin/Contributions/Edit");
            var collectionId = System.Text.RegularExpressions.Regex.Match(collectionPage, @"<option value=""(\d+)""").Groups[1].Value;
            await Browser.PostFormAsync(admin, "/Admin/Contributions/Edit", new()
            {
                ["Input.Title"] = "A link",
                ["Input.CollectionId"] = collectionId,
                ["Input.IsActive"] = "true",
            });
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "last-backup"), DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
            Assert.Contains("We have tested a restore", await admin.GetStringAsync("/Admin"));

            await Browser.PostFormAsync(admin, "/Admin/Settings?handler=RestoreTested", new(), "/Admin");

            Assert.DoesNotContain("Getting started", await admin.GetStringAsync("/Admin"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_contributor_gets_a_reference_a_curator_can_find_and_the_page_says_no_email_follows()
    {
        var token = await app.WithDbAsync(async db =>
        {
            var link = new ContributionLink
            {
                Title = TestData.Unique("Share"),
                Token = Guid.NewGuid().ToString("N"),
                CreatedAt = DateTime.UtcNow,
                Collection = new Collection { Title = TestData.Unique("Contributions"), CreatedAt = DateTime.UtcNow },
            };
            db.ContributionLinks.Add(link);
            await db.SaveChangesAsync();
            return link.Token;
        });
        using var visitor = app.CreateClient();
        var title = TestData.Unique("With a reference");

        var thanks = await Browser.SendFormWithFilesAsync(visitor, $"/contribute/{token}", new()
        {
            ["Input.Title"] = title,
            ["Input.Name"] = "Ada Contributor",
            ["Input.Rights"] = nameof(RightsStatement.CreatedByContributor),
            ["Input.Consent"] = "true",
        });
        var page = await thanks.Content.ReadAsStringAsync();
        var reference = await app.WithDbAsync(db => db.Contributions.Where(c => c.ArchiveItem.Title == title).Select(c => c.ReferenceCode).SingleAsync());
        using var curator = app.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);

        Assert.Matches("^[2-9A-Z]{4}-[2-9A-Z]{4}$", reference);
        Assert.Contains(reference!, page);
        Assert.Contains("No email will follow", page);
        Assert.Contains(title, await curator.GetStringAsync($"/Admin/Contributors?q={reference}"));

        // A reloaded page shows nobody's reference.
        Assert.DoesNotContain(reference!, await visitor.GetStringAsync($"/contribute/{token}/thanks"));
    }

    [Fact]
    public async Task Spam_gets_a_reference_too_so_it_learns_nothing()
    {
        await using var archive = await NewArchiveAsync();
        await using var patient = archive.WithWebHostBuilder(b => b.UseSetting("Contributions:MinimumFillSeconds", "3600"));
        var token = "";
        using (var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateScope(patient.Services))
        {
            var db = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Commonage.Web.Data.CommonageDbContext>(scope.ServiceProvider);
            var link = new ContributionLink
            {
                Title = "Share",
                Token = Guid.NewGuid().ToString("N"),
                CreatedAt = DateTime.UtcNow,
                Collection = new Collection { Title = "Contributions", CreatedAt = DateTime.UtcNow },
            };
            db.ContributionLinks.Add(link);
            await db.SaveChangesAsync();
            token = link.Token;
        }
        using var visitor = patient.CreateClient();

        var thanks = await Browser.SendFormWithFilesAsync(visitor, $"/contribute/{token}", new()
        {
            ["Input.Title"] = "Spam",
            ["Input.Name"] = "A program",
            ["Input.Rights"] = nameof(RightsStatement.CreatedByContributor),
            ["Input.Consent"] = "true",
        });

        Assert.Contains("Your reference:", await thanks.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("en", "Lindenau History Group")]
    [InlineData("de", "Geschichtsverein Lindenau")]
    public async Task The_demo_archive_is_set_up_like_a_real_group(string language, string name)
    {
        await using var demo = await NewArchiveAsync(language);
        using var visitor = demo.CreateClient();

        Assert.Contains(name, await visitor.GetStringAsync("/"));
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync("/contact")).StatusCode);
    }

    private static async Task<string> DateCheckAsync(HttpClient client, string text) =>
        System.Text.Json.JsonDocument.Parse(await client.GetStringAsync($"/date-check?text={Uri.EscapeDataString(text)}"))
            .RootElement.GetProperty("message").GetString()!;

    private static Task ReadInAsync(HttpClient client, string language) =>
        Browser.SendFormAsync(client, "/SetLanguage", new() { ["culture"] = language, ["returnUrl"] = "/" }, pageUrl: "/");

    [Fact]
    public async Task Item_text_says_which_language_it_is_in_also_to_a_reader_of_another()
    {
        await using var demo = await NewArchiveAsync("de");
        using var visitor = demo.CreateClient();
        visitor.DefaultRequestHeaders.Add("Accept-Language", "en");
        var home = await visitor.GetStringAsync("/");
        var itemUrl = System.Text.RegularExpressions.Regex.Match(home, @"href=""(/items/\d+)""").Groups[1].Value;

        var page = await visitor.GetStringAsync(itemUrl);

        Assert.Contains("<html lang=\"en\"", page);
        Assert.Matches(@"<h1 lang=""de"">", page);
        Assert.Contains("class=\"text-preserve-lines\" lang=\"de\"", page);
        Assert.Contains("class=\"item-card-title\" lang=\"de\"", home);
        Assert.Contains("<h2 id=\"reuse-heading\" class=\"h6 mb-1\">May I reuse this?</h2>", page);
        Assert.Contains("xml:lang=\"de\"", await visitor.GetStringAsync("/feed.xml"));

        // In several languages without a main one, no language is claimed for them.
        using var admin = await AdminAsync(demo);
        await SaveAsync(admin, new() { ["Input.DefaultLanguage"] = "de", ["Input.ContentLanguage"] = DateLanguages.Several });
        var mixed = await visitor.GetStringAsync(itemUrl);
        Assert.Contains("<h1>", mixed);
        Assert.DoesNotContain("class=\"text-preserve-lines\" lang=", mixed);
    }

    [Fact]
    public async Task Without_a_choice_the_material_is_taken_to_be_in_the_archive_language()
    {
        await using var archive = await NewArchiveAsync();
        using var admin = await AdminAsync(archive);
        await SaveAsync(admin, new() { ["Input.DefaultLanguage"] = "de" });

        var settings = await admin.GetStringAsync("/Admin/Settings");

        Assert.Matches(@"<option value=""de"" lang=""de"" selected=""selected"">Deutsch</option>", settings);
        Assert.Matches(@"id=""date-language-en""[^>]*checked=""checked""", settings);
        Assert.DoesNotMatch(@"id=""date-language-de""[^>]*checked=""checked""", settings);
    }

    [Fact]
    public async Task Saving_the_settings_without_a_change_reads_dates_as_before()
    {
        await using var archive = await NewArchiveAsync();
        using var admin = await AdminAsync(archive);
        Assert.Equal("Understood as: 1945–1955", await DateCheckAsync(admin, "um 1950"));

        // What the page offers when nothing was chosen: English, and German as well.
        await SaveAsync(admin, new() { ["Input.ContentLanguage"] = "en", ["Input.OtherContentLanguages"] = "de" });

        Assert.Equal("Understood as: 1945–1955", await DateCheckAsync(admin, "um 1950"));
        Assert.Equal("Understood as: 1950–1959", await DateCheckAsync(admin, "1950er Jahre"));
        var saved = await archive.Services.CreateScope().ServiceProvider.GetRequiredService<Commonage.Web.Data.CommonageDbContext>()
            .ArchiveSettings.SingleAsync();
        Assert.Equal(("en", "de"), (saved.ContentLanguage, saved.OtherContentLanguages));
    }

    [Fact]
    public async Task Dates_are_read_in_the_languages_of_the_material_and_of_whoever_types_them()
    {
        await using var archive = await NewArchiveAsync();
        using var admin = await AdminAsync(archive);
        Assert.Equal("Understood as: 1953", await DateCheckAsync(admin, "12 marca 1953"));

        var saved = await SaveAsync(admin, new() { ["Input.ContentLanguage"] = "pl" });

        Assert.Contains("Your changes are saved", await saved.Content.ReadAsStringAsync());
        Assert.Equal("Understood as: 1953-03-12", await DateCheckAsync(admin, "12 marca 1953"));
        // German is no longer the material's, and this admin reads English.
        Assert.Equal("Understood as: 1950", await DateCheckAsync(admin, "um 1950"));
        Assert.Equal("Understood as: 1950–1959", await DateCheckAsync(admin, "1950s"));
        await ReadInAsync(admin, "de");
        Assert.Equal("Verstanden als: 1945–1955", await DateCheckAsync(admin, "um 1950"));
    }

    [Fact]
    public async Task Only_languages_from_the_list_can_be_chosen()
    {
        await using var archive = await NewArchiveAsync();
        using var admin = await AdminAsync(archive);

        var wrong = await (await SaveAsync(admin, new() { ["Input.ContentLanguage"] = "<script>" })).Content.ReadAsStringAsync();
        var wrongOther = await (await SaveAsync(admin, new() { ["Input.ContentLanguage"] = "en", ["Input.OtherContentLanguages"] = "pl" })).Content.ReadAsStringAsync();

        Assert.Contains("Please choose one of the languages.", wrong);
        Assert.Contains("Please choose one of the languages.", wrongOther);
        Assert.DoesNotContain("Your changes are saved", wrong + wrongOther);
    }

    [Fact]
    public async Task A_language_without_translations_reads_English_with_English_counts()
    {
        await using var polish = app.WithWebHostBuilder(b => b.UseSetting("Languages:ForTesting", "pl"));
        using var curator = polish.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);
        await ReadInAsync(curator, "pl");

        var bin = await curator.GetStringAsync("/Admin/Bin");
        await ReadInAsync(curator, "de");
        var german = await curator.GetStringAsync("/Admin/Bin");

        Assert.Contains("<html lang=\"pl\"", bin);
        Assert.Contains("Deleted items and photos stay here for 30 days,", bin);
        Assert.Contains("bleiben 30 Tage hier", german);
    }
}
