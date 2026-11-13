using Commonage.Web.Domain;
using Commonage.Web.Search;

namespace Commonage.Tests;

/// <summary>Finding the dates in what someone typed into the search, without taking the other words for dates.</summary>
public class SearchDateTests
{
    private static readonly DateLanguages English = DateLanguages.For("en", [], "en");
    private static readonly DateLanguages German = DateLanguages.For("de", [], "de");

    private static (string Words, string Dates) Read(string text, DateLanguages? languages = null)
    {
        var (words, dates) = SearchDates.Extract(text, languages ?? English);
        return (words, string.Join(" | ", dates.Select(d => $"{d.Text} = {d.Period}")));
    }

    [Theory]
    [InlineData("Mill Street 1950s", "Mill Street", "1950s = 1950–1959")]
    [InlineData("1958", "", "1958 = 1958")]
    [InlineData("Team photo 1958", "Team photo", "1958 = 1958")]
    [InlineData("flood March 1953", "flood", "March 1953 = 1953-03-01 – 1953-03-31")]
    [InlineData("circa 1950 bakery", "bakery", "circa 1950 = 1945–1955")]
    [InlineData("before 1945", "", "before 1945 = –1944")]
    [InlineData("tram 1930-1955", "tram", "1930-1955 = 1930–1955")]
    [InlineData("19th century church", "church", "19th century = 1800–1899")]
    [InlineData("school (1953),", "school", "1953 = 1953")]
    [InlineData("1958 Krause bakery", "Krause bakery", "1958 = 1958")]
    [InlineData("1950s church", "church", "1950s = 1950–1959")]
    [InlineData("12.03.1953", "", "12.03.1953 = 1953-03-12")]
    public void Dates_are_taken_out_and_the_other_words_stay(string text, string words, string dates)
    {
        Assert.Equal((words, dates), Read(text));
    }

    [Theory]
    [InlineData("Mühle 1950er", "Mühle", "1950er = 1950–1959")]
    [InlineData("Mühle 1950er Jahre", "Mühle", "1950er Jahre = 1950–1959")]
    [InlineData("Feste der 1960er Jahren", "Feste der", "1960er Jahren = 1960–1969")]
    [InlineData("Hochwasser März 1953", "Hochwasser", "März 1953 = 1953-03-01 – 1953-03-31")]
    [InlineData("vor 1945", "", "vor 1945 = –1944")]
    public void Dates_are_read_in_the_archives_languages(string text, string words, string dates)
    {
        Assert.Equal((words, dates), Read(text, German));
    }

    [Theory]
    [InlineData("Mill Street")]
    [InlineData("Plot 12")]
    [InlineData("Platform 9")]
    [InlineData("")]
    public void Text_without_a_date_stays_as_it_is(string text)
    {
        var (words, dates) = SearchDates.Extract(text, English);

        Assert.Equal(string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries)), words);
        Assert.Empty(dates);
    }

    [Fact]
    public void A_word_before_a_year_that_changes_nothing_stays_a_word()
    {
        Assert.Equal(("Krause bakery", "1958 = 1958"), Read("Krause bakery 1958"));
        Assert.Equal(("Street", "1950s = 1950–1959"), Read("Street 1950s"));
    }

    [Fact]
    public void At_most_a_few_dates_are_taken()
    {
        var (words, dates) = SearchDates.Extract("1950 1951 1952 1953 1954", English);

        Assert.Equal(SearchDates.MaxDates, dates.Count);
        Assert.Equal("1953 1954", words);
    }
}
