using System.Net;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Commonage.Tests;

/// <summary>How long the activity log and rejected or withdrawn contributions are kept. Each test has an archive of its own, because the settings apply to all of it.</summary>
[Collection(nameof(AppCollection))]
public class RetentionTests(CommonageAppFactory app)
{
    private static async Task<HttpClient> AdminAsync(WebApplicationFactory<Program> archive)
    {
        var admin = archive.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        return admin;
    }

    private static Task<HttpResponseMessage> SaveSettingsAsync(HttpClient admin, Dictionary<string, string> fields)
    {
        var all = new Dictionary<string, string>
        {
            ["Input.DefaultLanguage"] = "en",
            ["Input.AllowSearchEngines"] = "true",
            ["Input.UseStandardColor"] = "true",
        };
        foreach (var (key, value) in fields)
            all[key] = value;
        return Browser.PostFormAsync(admin, "/Admin/Settings", all);
    }

    private static Task<ArchiveSettings> SettingsAsync(WebApplicationFactory<Program> archive) =>
        archive.WithDbAsync(db => db.ArchiveSettings.AsNoTracking().SingleAsync());

    private static async Task ApplyAsync(WebApplicationFactory<Program> archive)
    {
        using var scope = archive.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ArchiveRetention>().ApplyAsync();
    }

    private static Task SetAsideAtAsync(WebApplicationFactory<Program> archive, int itemId, DateTime at) =>
        archive.WithDbAsync(db => db.ArchiveItems.Where(i => i.Id == itemId).ExecuteUpdateAsync(s => s.SetProperty(i => i.RejectedOrWithdrawnAt, at)));

    private static Task<DateTime?> BinnedAtAsync(WebApplicationFactory<Program> archive, int itemId) =>
        archive.WithDbAsync(db => db.ArchiveItems.IgnoreQueryFilters().Where(i => i.Id == itemId).Select(i => i.DeletedAt).SingleAsync());

    [Fact]
    public async Task Rejected_and_withdrawn_contributions_go_to_the_bin_after_the_time_the_archive_keeps_them()
    {
        await using var archive = await app.NewArchiveAsync();
        using var admin = await AdminAsync(archive);
        await SaveSettingsAsync(admin, new() { ["Input.RejectedKeptMonths"] = "6" });
        var now = DateTime.UtcNow;
        var oldRejected = await archive.CreateContributionAsync(ItemStatus.Rejected);
        var recentRejected = await archive.CreateContributionAsync(ItemStatus.Rejected);
        var oldWithdrawn = await archive.CreateContributionAsync(ItemStatus.Withdrawn);
        var published = await archive.CreateContributionAsync(ItemStatus.Published);
        var waiting = await archive.CreateContributionAsync(ItemStatus.Submitted);
        await SetAsideAtAsync(archive, oldRejected.Id, now.AddMonths(-6).AddDays(-1));
        await SetAsideAtAsync(archive, recentRejected.Id, now.AddMonths(-6).AddDays(1));
        await SetAsideAtAsync(archive, oldWithdrawn.Id, now.AddYears(-2));

        await ApplyAsync(archive);

        Assert.NotNull(await BinnedAtAsync(archive, oldRejected.Id));
        Assert.NotNull(await BinnedAtAsync(archive, oldWithdrawn.Id));
        Assert.Null(await BinnedAtAsync(archive, recentRejected.Id));
        Assert.Null(await BinnedAtAsync(archive, published.Id));
        Assert.Null(await BinnedAtAsync(archive, waiting.Id));
        var logged = await archive.WithDbAsync(db => db.AuditEntries.Where(e => e.Action == AuditAction.ItemMovedToBinAfterKeptTime).ToListAsync());
        Assert.Equal(new[] { oldRejected.Title, oldWithdrawn.Title }.Order(), logged.Select(e => e.Subject).Order());
        Assert.All(logged, e => Assert.Null(e.UserName));
        var bin = await admin.GetStringAsync("/Admin/Bin");
        Assert.Contains(oldRejected.Title, bin);
        Assert.Contains("Moved the rejected or withdrawn contribution to the bin", await admin.GetStringAsync("/Admin/Activity"));
    }

    [Fact]
    public async Task Curators_see_when_a_rejected_contribution_goes_to_the_bin()
    {
        await using var archive = await app.NewArchiveAsync();
        using var admin = await AdminAsync(archive);
        await SaveSettingsAsync(admin, new() { ["Input.RejectedKeptMonths"] = "6" });
        var item = await archive.CreateContributionAsync(ItemStatus.Submitted);
        var page = $"/Admin/Items/Edit/{item.Id}";

        var rejected = await Browser.PostFormAsync(admin, page, new()
        {
            ["Input.Title"] = item.Title,
            ["Input.CollectionId"] = item.CollectionId.ToString(),
            ["command"] = "reject",
        });

        Assert.Contains("It stays hidden, and goes to the bin in 6 months.", await rejected.Content.ReadAsStringAsync());
        Assert.Contains("This one goes to the bin on", await admin.GetStringAsync(page));
    }

    /// <summary>An archive that moves elsewhere keeps its times, and each contribution keeps when its time started.</summary>
    [Fact]
    public async Task The_kept_times_and_when_they_started_are_exported()
    {
        await using var archive = await app.NewArchiveAsync();
        using var admin = await AdminAsync(archive);
        await SaveSettingsAsync(admin, new() { ["Input.ActivityLogKeptYears"] = "5", ["Input.RejectedKeptMonths"] = "24" });
        var rejected = await archive.CreateContributionAsync(ItemStatus.Rejected);
        var at = new DateTime(2025, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        await SetAsideAtAsync(archive, rejected.Id, at);

        var response = await Browser.PostFormAsync(admin, "/Admin/Export", new());
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        using var json = System.Text.Json.JsonDocument.Parse(zip.GetEntry("archive.json")!.Open());

        var settings = json.RootElement.GetProperty("settings");
        Assert.Equal(5, settings.GetProperty("activityLogKeptYears").GetInt32());
        Assert.Equal(24, settings.GetProperty("rejectedKeptMonths").GetInt32());
        var item = json.RootElement.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetInt32() == rejected.Id);
        Assert.Equal(at, item.GetProperty("rejectedOrWithdrawnAt").GetDateTime().ToUniversalTime());
    }

    [Fact]
    public async Task Without_a_time_set_nothing_is_removed()
    {
        await using var archive = await app.NewArchiveAsync();
        using var admin = await AdminAsync(archive);
        await SaveSettingsAsync(admin, new());
        var rejected = await archive.CreateContributionAsync(ItemStatus.Rejected);
        await SetAsideAtAsync(archive, rejected.Id, DateTime.UtcNow.AddYears(-20));
        await archive.WithDbAsync(async db =>
        {
            db.AuditEntries.Add(new AuditEntry { At = DateTime.UtcNow.AddYears(-20), Action = AuditAction.ItemChanged, Subject = "Long ago" });
            return await db.SaveChangesAsync();
        });

        await ApplyAsync(archive);

        Assert.Null(await BinnedAtAsync(archive, rejected.Id));
        Assert.True(await archive.WithDbAsync(db => db.AuditEntries.AnyAsync(e => e.Subject == "Long ago")));
        Assert.Null((await SettingsAsync(archive)).RejectedKeptMonths);
        Assert.Null((await SettingsAsync(archive)).ActivityLogKeptYears);
    }

    [Fact]
    public async Task A_contribution_taken_back_out_of_the_bin_is_kept_for_the_whole_time_again()
    {
        await using var archive = await app.NewArchiveAsync();
        using var admin = await AdminAsync(archive);
        await SaveSettingsAsync(admin, new() { ["Input.RejectedKeptMonths"] = "3" });
        var rejected = await archive.CreateContributionAsync(ItemStatus.Rejected);
        await SetAsideAtAsync(archive, rejected.Id, DateTime.UtcNow.AddMonths(-4));
        await ApplyAsync(archive);

        await Browser.PostFormAsync(admin, $"/Admin/Bin?handler=RestoreItem&id={rejected.Id}", new(), "/Admin/Bin");
        await ApplyAsync(archive);

        Assert.Null(await BinnedAtAsync(archive, rejected.Id));
        var restarted = await archive.WithDbAsync(db => db.ArchiveItems.Where(i => i.Id == rejected.Id).Select(i => i.RejectedOrWithdrawnAt).SingleAsync());
        Assert.True(restarted > DateTime.UtcNow.AddMinutes(-5), "The time starts again when it is restored.");
    }

    [Fact]
    public void Only_rejected_and_withdrawn_contributions_carry_the_date()
    {
        var now = DateTime.UtcNow;
        var item = new ArchiveItem { Title = "Mill", Status = ItemStatus.Submitted, Contribution = new Contribution { ConsentText = "I agree." } };

        item.Reject(now);
        var rejected = item.RejectedOrWithdrawnAt;
        item.Accept(now.AddDays(1));
        var accepted = item.RejectedOrWithdrawnAt;
        item.Publish(now.AddDays(2));
        item.Withdraw(now.AddDays(3));

        Assert.Equal(now, rejected);
        Assert.Null(accepted);
        Assert.Equal(now.AddDays(3), item.RejectedOrWithdrawnAt);
    }

    [Fact]
    public async Task The_activity_log_loses_entries_older_than_kept_and_says_so_once()
    {
        await using var archive = await app.NewArchiveAsync();
        using var admin = await AdminAsync(archive);
        await SaveSettingsAsync(admin, new() { ["Input.ActivityLogKeptYears"] = "1" });
        await archive.WithDbAsync(async db =>
        {
            db.AuditEntries.Add(new AuditEntry { At = DateTime.UtcNow.AddYears(-1).AddDays(-1), Action = AuditAction.ItemChanged, Subject = "Over a year ago" });
            db.AuditEntries.Add(new AuditEntry { At = DateTime.UtcNow.AddMonths(-11), Action = AuditAction.ItemChanged, Subject = "Eleven months ago" });
            return await db.SaveChangesAsync();
        });

        await ApplyAsync(archive);
        await ApplyAsync(archive);

        var subjects = await archive.WithDbAsync(db => db.AuditEntries.Select(e => e.Subject).ToListAsync());
        Assert.DoesNotContain("Over a year ago", subjects);
        Assert.Contains("Eleven months ago", subjects);
        Assert.Equal(1, await archive.WithDbAsync(db => db.AuditEntries.CountAsync(e => e.Action == AuditAction.ActivityLogShortened)));
        Assert.Contains("Deleted activity log entries older than the archive keeps them", await admin.GetStringAsync("/Admin/Activity"));
    }

    [Fact]
    public async Task The_settings_take_only_the_offered_times_and_an_old_form_changes_nothing()
    {
        await using var archive = await app.NewArchiveAsync();
        using var admin = await AdminAsync(archive);

        await SaveSettingsAsync(admin, new() { ["Input.ActivityLogKeptYears"] = "2", ["Input.RejectedKeptMonths"] = "12" });
        var saved = await SettingsAsync(archive);
        var page = await admin.GetStringAsync("/Admin/Settings");
        var refused = await SaveSettingsAsync(admin, new() { ["Input.ActivityLogKeptYears"] = "0", ["Input.RejectedKeptMonths"] = "1" });
        var afterRefused = await SettingsAsync(archive);
        await SaveSettingsAsync(admin, new());
        var afterOldForm = await SettingsAsync(archive);
        await SaveSettingsAsync(admin, new() { ["Input.ActivityLogKeptYears"] = "0", ["Input.RejectedKeptMonths"] = "0" });
        var afterAlways = await SettingsAsync(archive);

        Assert.Equal((2, 12), (saved.ActivityLogKeptYears, saved.RejectedKeptMonths));
        Assert.Contains("<option value=\"12\" selected=\"selected\">For 12 months after the decision</option>", page);
        Assert.Contains("Please choose one of the times.", await refused.Content.ReadAsStringAsync());
        Assert.Equal((2, 12), (afterRefused.ActivityLogKeptYears, afterRefused.RejectedKeptMonths));
        Assert.Equal((2, 12), (afterOldForm.ActivityLogKeptYears, afterOldForm.RejectedKeptMonths));
        Assert.Equal(((int?)null, (int?)null), (afterAlways.ActivityLogKeptYears, afterAlways.RejectedKeptMonths));
    }

    /// <summary>When older ones were rejected is not known, so their time starts with the update, and nothing goes at once.</summary>
    [Fact]
    public async Task The_update_starts_the_time_for_contributions_rejected_before_it()
    {
        var name = "retention_" + Guid.NewGuid().ToString("N");
        await using (var connection = new NpgsqlConnection(app.ConnectionString))
        {
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
            await create.ExecuteNonQueryAsync();
        }
        var connectionString = new NpgsqlConnectionStringBuilder(app.ConnectionString) { Database = name }.ConnectionString;
        await using var db = new CommonageDbContext(new DbContextOptionsBuilder<CommonageDbContext>().UseNpgsql(connectionString).Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261107180233_AddRequireTwoFactor");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "Collections" ("Title", "CreatedAt") VALUES ('Contributions', now());
            INSERT INTO "ArchiveItems" ("Title", "Status", "CollectionId", "CreatedAt", "UpdatedAt", "NeedsTranscription")
            SELECT title, status, (SELECT min("Id") FROM "Collections"), now(), now(), false
            FROM (VALUES ('Rejected', 3), ('Withdrawn', 4), ('Draft', 0), ('Published', 1)) AS v(title, status);
            """);

        await migrator.MigrateAsync();

        var dates = await db.ArchiveItems.ToDictionaryAsync(i => i.Title, i => i.RejectedOrWithdrawnAt);
        Assert.True(dates["Rejected"] > DateTime.UtcNow.AddMinutes(-5));
        Assert.True(dates["Withdrawn"] > DateTime.UtcNow.AddMinutes(-5));
        Assert.Null(dates["Draft"]);
        Assert.Null(dates["Published"]);
    }
}
