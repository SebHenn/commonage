using System.Text.RegularExpressions;
using Commonage.Web.Domain;
using Commonage.Web.Media;
using Commonage.Web.Search;
using FsCheck;
using FsCheck.Xunit;

namespace Commonage.Tests;

/// <summary>
/// These parsers read what anyone types into the public forms or sends as a file. Instead of chosen examples, FsCheck
/// tries a hundred random inputs on each run, including control characters, unusual scripts and very long text.
/// </summary>
public partial class InputPropertyTests
{
    [Property]
    public void Any_text_either_parses_into_an_ordered_range_or_is_rejected(string? text)
    {
        if (DateRange.TryParse(text, out var range) && range.From is { } from && range.To is { } to)
            Assert.True(from <= to, $"\"{text}\" gave {from} to {to}");
    }

    [Property]
    public void Any_year_in_the_past_is_read_as_that_whole_year(PositiveInt number)
    {
        var year = 1000 + (number.Get % 1100);

        Assert.True(DateRange.TryParse(year.ToString(), out var range));
        Assert.Equal(new DateOnly(year, 1, 1), range.From);
        Assert.Equal(new DateOnly(year, 12, 31), range.To);
    }

    [Property]
    public void An_item_keeps_a_date_only_if_it_can_be_read(string? text)
    {
        var item = new ArchiveItem { Title = "Test" };

        var accepted = item.TrySetDate(text);

        if (accepted)
            Assert.Equal(string.IsNullOrWhiteSpace(text), item.DateText is null);
        else
            Assert.Null(item.DateText);
    }

    [Property]
    public void A_tag_list_never_holds_empty_duplicate_or_comma_separated_tags(string? input)
    {
        var tags = Tag.ParseList(input);

        Assert.All(tags, tag => Assert.False(string.IsNullOrWhiteSpace(tag)));
        Assert.All(tags, tag => Assert.DoesNotContain(',', tag));
        Assert.Equal(tags.Count, tags.Select(Tag.Normalize).Distinct().Count());
    }

    [Property]
    public void Any_search_text_gives_a_query_that_cannot_inject_search_operators(string? text)
    {
        var query = ItemSearch.ToPrefixQuery(text);

        if (query is not null)
            Assert.Matches(SafePrefixQuery(), query);
    }

    [Property]
    public void Any_bytes_are_recognised_as_a_supported_image_or_refused(byte[] header)
    {
        var contentType = ImageFormats.DetectContentType(header);

        Assert.Contains(contentType, new string?[] { null, "image/jpeg", "image/png", "image/tiff", "image/webp" });
    }

    // Words of letters, digits and marks, each followed by ":*" and joined by " & ". Nothing else reaches to_tsquery.
    [GeneratedRegex(@"^[\p{L}\p{N}\p{M}]+:\*( & [\p{L}\p{N}\p{M}]+:\*)*$")]
    private static partial Regex SafePrefixQuery();
}
