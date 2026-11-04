using System.Text.RegularExpressions;
using Commonage.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Tests;

/// <summary>Two people working on one item must not silently overwrite each other's work.</summary>
[Collection(nameof(AppCollection))]
public partial class SafeEditingTests(CommonageAppFactory app)
{
    private Dictionary<string, string> Fields(ArchiveItem item, string description, string version) => new()
    {
        ["Input.Title"] = item.Title,
        ["Input.CollectionId"] = item.CollectionId.ToString(),
        ["Input.Description"] = description,
        ["Input.Version"] = version,
    };

    private Task<string?> SavedDescriptionAsync(int id) =>
        app.WithDbAsync(db => db.ArchiveItems.Where(i => i.Id == id).Select(i => i.Description).SingleAsync());

    [Fact]
    public async Task A_save_from_an_outdated_form_keeps_both_versions_and_saves_nothing()
    {
        var item = await app.CreateContributionAsync(ItemStatus.Draft);
        var page = $"/Admin/Items/Edit/{item.Id}";
        using var curator = app.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);
        var version = VersionField().Match(await curator.GetStringAsync(page)).Groups[1].Value;
        Assert.NotEmpty(version);

        await Browser.PostFormAsync(curator, page, Fields(item, "Saved by the first person", version));
        var response = await Browser.SendFormAsync(curator, page, Fields(item, "Typed by the second person", version));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal("Saved by the first person", await SavedDescriptionAsync(item.Id));
        Assert.Contains("Someone else changed this item", html);
        Assert.Contains("Saved by the first person", html);
        Assert.Contains("Typed by the second person", html);
        Assert.DoesNotContain("Your changes are saved", html);
        Assert.Contains("data-warn-unsaved=\"dirty\"", html);
        var entries = await app.WithDbAsync(db => db.AuditEntries.CountAsync(e => e.ArchiveItemId == item.Id && e.Action == AuditAction.ItemChanged));
        Assert.Equal(1, entries);

        // Saving again is the person's decision to replace the other version.
        var newVersion = VersionField().Match(html).Groups[1].Value;
        await Browser.PostFormAsync(curator, page, Fields(item, "Typed by the second person", newVersion));
        Assert.Equal("Typed by the second person", await SavedDescriptionAsync(item.Id));
    }

    [Fact]
    public async Task Uploading_a_photo_does_not_save_text_or_outdate_the_open_form()
    {
        var item = await app.CreateContributionAsync(ItemStatus.Draft);
        var page = $"/Admin/Items/Edit/{item.Id}";
        using var curator = app.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);
        var version = VersionField().Match(await curator.GetStringAsync(page)).Groups[1].Value;

        await Browser.UploadAsync(curator, page, "Upload", "flood.jpg", TestData.Photo);
        Assert.Null(await SavedDescriptionAsync(item.Id));

        await Browser.PostFormAsync(curator, page, Fields(item, "Saved after the upload", version));
        Assert.Equal("Saved after the upload", await SavedDescriptionAsync(item.Id));
    }

    [Theory]
    [InlineData("/Admin/Items/Edit")]
    [InlineData("/Admin/Collections/Edit")]
    [InlineData("/Admin/Contributions/Edit")]
    public async Task Forms_with_long_text_warn_before_unsaved_text_is_lost(string page)
    {
        using var curator = app.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);

        Assert.Contains("data-warn-unsaved", await curator.GetStringAsync(page));
    }

    [GeneratedRegex(@"name=""Input\.Version"" value=""(\d+)""")]
    private static partial Regex VersionField();
}
