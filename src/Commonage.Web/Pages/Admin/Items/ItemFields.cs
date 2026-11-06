using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Commonage.Web.Pages.Admin.Items;

/// <summary>The checks for fields that can be set on one item, on many new ones or on a selection, so they all answer the same.</summary>
public static class ItemFields
{
    public const int MaxTagsPerItem = 30;

    public static string? DateError(IStringLocalizer localizer, string? date) =>
        !string.IsNullOrWhiteSpace(date) && !DateRange.TryParse(date, out _)
            ? localizer["This date could not be understood. Please include a year, for example: 1953, 12.03.1953, circa 1950, 1950s, 1950–1955."].Value
            : null;

    public static IEnumerable<string> TagErrors(IStringLocalizer localizer, IReadOnlyList<string> names)
    {
        if (names.Any(name => name.Length > Tag.MaxLength))
            yield return localizer["A tag can be at most {0} characters long.", Tag.MaxLength];
        if (names.Count > MaxTagsPerItem)
            yield return localizer["An item can have at most {0} tags.", MaxTagsPerItem];
    }

    public static string? RightsError(IStringLocalizer localizer, string? rightsUri) =>
        !string.IsNullOrEmpty(rightsUri) && !RightsOptions.IsKnown(rightsUri) ? localizer["Please choose one of the statements."].Value : null;

    public static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>Reuses existing tags regardless of case, so "mill street" joins the existing tag "Mill Street".</summary>
    public static async Task<List<Tag>> FindOrCreateTagsAsync(this CommonageDbContext db, IReadOnlyList<string> names, CancellationToken cancellationToken = default)
    {
        var normalizedNames = names.Select(Tag.Normalize).ToList();
        var existing = await db.Tags.Where(t => normalizedNames.Contains(t.NormalizedName)).ToDictionaryAsync(t => t.NormalizedName, cancellationToken);
        return names
            .Select(name => existing.GetValueOrDefault(Tag.Normalize(name)) ?? new Tag { Name = name, NormalizedName = Tag.Normalize(name) })
            .ToList();
    }

    /// <summary>The form field for many files at once answers with one page. Keeps that page's message within what a cookie can carry.</summary>
    public static string ListOf(IEnumerable<string> names)
    {
        const int shown = 5;
        var all = names.ToList();
        return all.Count <= shown ? string.Join(", ", all) : $"{string.Join(", ", all.Take(shown))}, … (+{all.Count - shown})";
    }
}

/// <summary>What the archive answers about one file sent on its own, for the upload queue in uploads.js.</summary>
public record UploadReply(string Outcome, string? Message = null, int? ItemId = null, string? ItemUrl = null, int? MediaFileId = null, string? CardUrl = null)
{
    public static UploadReply Refused(string message) => new("refused", message);

    public static UploadReply Identical(IStringLocalizer localizer, IdenticalFile same, string itemUrl) =>
        new("identical", localizer["The archive already has this file: {0}", ArchiveItem.ShownTitle(localizer, same.ItemTitle)], same.ItemId, itemUrl);

    public static UploadReply Added(int itemId, string? itemUrl, int mediaFileId, string? cardUrl = null) => new("added", null, itemId, itemUrl, mediaFileId, cardUrl);
}
