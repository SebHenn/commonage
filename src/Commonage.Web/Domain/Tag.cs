using System.Text.RegularExpressions;

namespace Commonage.Web.Domain;

/// <summary>A keyword such as a place, a person or a topic. Curators enter tags freely; matching ignores case.</summary>
public partial class Tag
{
    public const int MaxLength = 50;

    public int Id { get; set; }
    public required string Name { get; set; }

    /// <summary>Lower-case form of <see cref="Name"/>, so "Mill Street" and "mill street" are the same tag.</summary>
    public required string NormalizedName { get; set; }

    public List<ArchiveItem> Items { get; set; } = [];

    public static string Normalize(string name) => Clean(name).ToLowerInvariant();

    /// <summary>Splits a comma-separated list into tag names, dropping blanks and duplicates.</summary>
    public static IReadOnlyList<string> ParseList(string? input) =>
        (input ?? "").Split([',', ';', '\n'])
            .Select(Clean)
            .Where(name => name.Length > 0)
            .DistinctBy(name => name.ToLowerInvariant())
            .ToList();

    private static string Clean(string name) => Whitespace().Replace(name, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
