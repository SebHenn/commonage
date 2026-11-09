using Commonage.Web.Audit;
using Commonage.Web.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Localization;

namespace Commonage.Tests;

/// <summary>Narrowing the activity log down by who, what and a title. An archive of its own, so that only these entries are in its log.</summary>
[Collection(nameof(AppCollection))]
public class ActivityFilterTests(CommonageAppFactory app)
{
    private const string Curator = "someone@example.org";

    private static async Task<(WebApplicationFactory<Program> Archive, HttpClient Admin)> ArchiveWithLogAsync(CommonageAppFactory app)
    {
        var archive = await app.NewArchiveAsync();
        var admin = archive.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        await archive.WithDbAsync(async db =>
        {
            var at = DateTime.UtcNow.AddHours(-1);
            db.AuditEntries.AddRange(
                new AuditEntry { At = at, UserName = CommonageAppFactory.AdminEmail, Action = AuditAction.ItemPublished, Subject = "Mill Street flood" },
                new AuditEntry { At = at, UserName = CommonageAppFactory.AdminEmail, Action = AuditAction.SettingsChanged },
                new AuditEntry { At = at, UserName = Curator, Action = AuditAction.PhotosAdded, Subject = "School photo 1950" },
                new AuditEntry { At = at, Action = AuditAction.ContributionReceived, Subject = "Harbour in winter" },
                new AuditEntry { At = at, Action = AuditAction.ItemPurged, Subject = "Old bridge" });
            return await db.SaveChangesAsync();
        });
        return (archive, admin);
    }

    private static readonly string[] Subjects = ["Mill Street flood", "School photo 1950", "Harbour in winter", "Old bridge"];

    private static async Task<IReadOnlyList<string>> ShownAsync(HttpClient admin, string query)
    {
        var page = await admin.GetStringAsync("/Admin/Activity" + query);
        return Subjects.Where(page.Contains).ToList();
    }

    [Fact]
    public async Task The_log_can_be_narrowed_down_by_who_what_and_a_title()
    {
        var (archive, admin) = await ArchiveWithLogAsync(app);
        await using (archive)
        using (admin)
        {
            Assert.Equal(Subjects, await ShownAsync(admin, ""));
            Assert.Equal(["School photo 1950"], await ShownAsync(admin, $"?who={Uri.EscapeDataString(Curator)}"));
            Assert.Equal(["Harbour in winter"], await ShownAsync(admin, "?who=contributors"));
            Assert.Equal(["Old bridge"], await ShownAsync(admin, "?who=automatic"));
            Assert.Equal(["Mill Street flood", "Old bridge"], await ShownAsync(admin, "?what=Items"));
            Assert.Equal(["School photo 1950"], await ShownAsync(admin, "?what=Photos"));
            Assert.Equal(["Mill Street flood"], await ShownAsync(admin, "?q=mill+street"));
            Assert.Equal(["Mill Street flood"], await ShownAsync(admin, $"?who={Uri.EscapeDataString(CommonageAppFactory.AdminEmail)}&what=Items"));
            Assert.Empty(await ShownAsync(admin, "?q=%25"));
            Assert.Contains("No entries match.", await admin.GetStringAsync("/Admin/Activity?q=nothing+like+this"));
        }
    }

    [Fact]
    public async Task The_filters_offer_everyone_in_the_log_and_stay_chosen()
    {
        var (archive, admin) = await ArchiveWithLogAsync(app);
        await using (archive)
        using (admin)
        {
            var page = await admin.GetStringAsync($"/Admin/Activity?who={Uri.EscapeDataString(Curator)}&what=Photos&q=school");

            Assert.Contains($"<option value=\"{Curator}\" selected=\"selected\">{Curator}</option>", page);
            Assert.Contains("<option value=\"Photos\" selected=\"selected\">Photos</option>", page);
            Assert.Contains("value=\"school\"", page);
            Assert.Contains("Show everything", page);
        }
    }

    [Fact]
    public async Task Paging_keeps_the_filters()
    {
        await using var archive = await app.NewArchiveAsync();
        using var admin = archive.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        await archive.WithDbAsync(async db =>
        {
            for (var i = 0; i < 101; i++)
                db.AuditEntries.Add(new AuditEntry { At = DateTime.UtcNow.AddMinutes(-i), UserName = Curator, Action = AuditAction.ItemChanged, Subject = $"Item {i}" });
            return await db.SaveChangesAsync();
        });

        var page = await admin.GetStringAsync($"/Admin/Activity?who={Uri.EscapeDataString(Curator)}&what=Items");
        var next = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(page, @"rel=""next""\s+href=""([^""]+)""").Groups[1].Value);
        var second = await admin.GetStringAsync(next);

        Assert.Contains($"who={Curator}", Uri.UnescapeDataString(next));
        Assert.Contains("what=Items", next);
        Assert.Contains("p=2", next);
        Assert.Contains("Item 100", second);
        Assert.DoesNotContain("Item 99<", second);
    }

    private sealed class Echo : IStringLocalizer
    {
        public LocalizedString this[string name] => new(name, name);
        public LocalizedString this[string name, params object[] arguments] => new(name, string.Format(name, arguments));
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }

    [Fact]
    public void Every_action_has_a_kind_and_a_description()
    {
        foreach (var action in Enum.GetValues<AuditAction>())
        {
            Assert.True(Enum.IsDefined(AuditText.KindOf(action)), $"{action} has no kind.");
            Assert.NotEqual(action.ToString(), AuditText.Describe(new Echo(), action));
        }
        var kinds = Enum.GetValues<AuditKind>();
        Assert.Equal(kinds.Length, kinds.Select(k => AuditText.Describe(new Echo(), k)).Distinct().Count());
    }
}
