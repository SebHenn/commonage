using Commonage.Web.Domain;

namespace Commonage.Tests;

public class DateRangeTests
{
    [Theory]
    [InlineData("1953", "1953")]
    [InlineData("12.03.1953", "1953-03-12")]
    [InlineData("1953-03-12", "1953-03-12")]
    [InlineData("12. März 1953", "1953-03-12")]
    [InlineData("March 12, 1953", "1953-03-12")]
    [InlineData("March 1953", "1953-03-01 – 1953-03-31")]
    [InlineData("Sept. 1953", "1953-09-01 – 1953-09-30")]
    [InlineData("03/1953", "1953-03-01 – 1953-03-31")]
    [InlineData("Sommer 1962", "1962")]
    [InlineData("1950s", "1950–1959")]
    [InlineData("1950er Jahre", "1950–1959")]
    [InlineData("in den 1950ern", "1950–1959")]
    [InlineData("circa 1950", "1945–1955")]
    [InlineData("ca. 1950", "1945–1955")]
    [InlineData("um 1950", "1945–1955")]
    [InlineData("ca. März 1953", "1952-03-01 – 1954-03-31")]
    [InlineData("1950–1955", "1950–1955")]
    [InlineData("1950-1955", "1950–1955")]
    [InlineData("zwischen 1950 und 1955", "1950–1955")]
    [InlineData("before 1945", "–1944")]
    [InlineData("vor 1945", "–1944")]
    [InlineData("bis 1950", "–1950")]
    [InlineData("after 1950", "1951–")]
    [InlineData("seit 1950", "1950–")]
    [InlineData("19. Jahrhundert", "1800–1899")]
    [InlineData("19th century", "1800–1899")]
    public void Reads_dates_as_people_write_them(string text, string expected)
    {
        Assert.True(DateRange.TryParse(text, out var range));
        Assert.Equal(expected, range.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("Mitte des Jahrhunderts")]
    [InlineData("31.02.1953")]
    [InlineData("1953-13")]
    public void Rejects_text_without_a_usable_date(string text)
    {
        Assert.False(DateRange.TryParse(text, out _));
    }

    [Fact]
    public void An_unreadable_date_leaves_the_item_unchanged()
    {
        var item = new ArchiveItem { Title = "Letter" };
        item.TrySetDate("circa 1950");

        Assert.False(item.TrySetDate("someday"));

        Assert.Equal("circa 1950", item.DateText);
        Assert.Equal(new DateOnly(1945, 1, 1), item.DateFrom);
        Assert.Equal(new DateOnly(1955, 12, 31), item.DateTo);
    }
}
