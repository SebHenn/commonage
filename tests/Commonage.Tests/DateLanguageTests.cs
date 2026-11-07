using Commonage.Web.Domain;
using FsCheck;
using FsCheck.Xunit;

namespace Commonage.Tests;

/// <summary>Dates are read in the archive's languages and in the reader's, and nothing that was accepted before stops being accepted.</summary>
public class DateLanguageTests
{
    private static string Read(string text, DateLanguages languages) =>
        DateRange.TryParse(text, languages, out var range) ? range.ToString() : "not understood";

    /// <summary>What an archive reads before and after an admin saves the settings without changing them.</summary>
    private static readonly string[] Archives = ["no setting, English reader", "no setting, German reader", "saved in English", "saved in German"];

    public static readonly TheoryData<string> AsBefore = new(Archives);

    private static DateLanguages Languages(string archive) => archive switch
    {
        "no setting, English reader" => DateLanguages.For(null, [], "en"),
        "no setting, German reader" => DateLanguages.For(null, [], "de"),
        "saved in English" => DateLanguages.For("en", ["de"], "en"),
        _ => DateLanguages.For("de", ["en"], "de"),
    };

    // Words, numbers and signs that dates were written with before, in any order, so that FsCheck finds combinations
    // nobody would think of.
    private static readonly string[] Pieces =
    [
        "1953", "1950", "1945", "1899", "2001", "12", "3", "31", "03", "19", "195", "0", " ", " ", " ", ".", "/", "-", "–", ",", "'",
        "s", "'s", "er", "ern", "jahre", "st", "th", "19th", "19.", "circa", "ca.", "ca", "c.", "~", "approx.", "approx", "approximately",
        "about", "around", "um", "etwa", "ungefähr", "Ungefähr", "before", "vor", "until", "till", "bis", "after", "nach", "since", "from",
        "seit", "ab", "century", "Jahrhundert", "jh", "between", "and", "zwischen", "und", "in den", "Sommer", "summer", "January",
        "Januar", "Jänner", "jän", "feb", "March", "März", "MÄRZ", "mär", "mar", "April", "May", "Mai", "June", "Juni", "jun", "July",
        "Juli", "Aug.", "Sept.", "sep", "October", "Oktober", "okt", "Nov", "December", "Dezember", "Dez.", "x", "the", "of",
    ];

    [Property(MaxTest = 3000)]
    public void Every_date_accepted_before_still_means_the_same(int[] picks)
    {
        var text = string.Concat((picks ?? []).Take(8).Select(p => Pieces[(p & int.MaxValue) % Pieces.Length]));
        if (!DateRangeBeforeM17.TryParse(text, out var before))
            return;
        foreach (var archive in Archives)
        {
            Assert.True(DateRange.TryParse(text, Languages(archive), out var now), $"\"{text}\" is no longer understood ({archive}).");
            Assert.Equal((before.From, before.To), (now.From, now.To));
        }
    }

    [Theory]
    [MemberData(nameof(AsBefore))]
    public void The_examples_people_were_given_mean_the_same(string archive)
    {
        foreach (var text in new[]
        {
            "1953", "12.03.1953", "1953-03-12", "12. März 1953", "March 12, 1953", "March 1953", "Sept. 1953", "03/1953", "Sommer 1962",
            "1950s", "1950er Jahre", "in den 1950ern", "circa 1950", "ca. 1950", "um 1950", "ca. März 1953", "1950–1955", "1950-1955",
            "zwischen 1950 und 1955", "before 1945", "vor 1945", "bis 1950", "after 1950", "seit 1950", "19. Jahrhundert", "19th century",
            "Jänner 1950", "approx. 1950", "~1950", "c. 1900", "around 1960", "etwa 1930", "ungefähr 1930", "until 1950", "since 1950",
            "ab 1950", "nach 1950", "12 Dec 1953", "Dez. 1953", "1950's", "1953-03",
        })
        {
            Assert.True(DateRangeBeforeM17.TryParse(text, out var before), text);
            Assert.Equal(before.ToString(), Read(text, Languages(archive)));
        }
    }

    [Fact]
    public void Month_names_come_in_every_language_also_as_they_are_written_after_a_day()
    {
        var polish = DateLanguages.For("pl", [], "pl");
        var french = DateLanguages.For("fr", [], "fr");

        Assert.Equal("1953-03-12", Read("12 marca 1953", polish));
        Assert.Equal("1953-03-01 – 1953-03-31", Read("marzec 1953", polish));
        Assert.Equal("1953-10-12", Read("12 października 1953", polish));
        Assert.Equal("1953-02-12", Read("12 février 1953", french));
        Assert.Equal("1953-08-01 – 1953-08-31", Read("août 1953", french));
    }

    [Fact]
    public void Accents_can_be_left_out()
    {
        Assert.Equal("1953-03-01 – 1953-03-31", Read("Marz 1953", DateLanguages.Default));
        Assert.Equal("1945–1955", Read("ungefahr 1950", DateLanguages.Default));
        Assert.Equal("1953-10-12", Read("12 pazdziernika 1953", DateLanguages.For("pl", [], "pl")));
        Assert.Equal("1953-02-01 – 1953-02-28", Read("fevrier 1953", DateLanguages.For("fr", [], "fr")));
    }

    [Fact]
    public void A_month_name_with_two_meanings_is_read_in_the_archive_language_first()
    {
        // November in Polish, October in Croatian.
        Assert.Equal("1953-11-01 – 1953-11-30", Read("listopad 1953", DateLanguages.For("pl", [], "hr")));
        Assert.Equal("1953-10-01 – 1953-10-31", Read("listopad 1953", DateLanguages.For("hr", [], "pl")));
        Assert.Equal("1953-11-01 – 1953-11-30", Read("listopad 1953", DateLanguages.For(DateLanguages.Several, ["pl", "hr"], "en")));
    }

    [Fact]
    public void Words_of_languages_the_archive_does_not_use_are_not_read()
    {
        var polish = DateLanguages.For("pl", [], "pl");

        // "um" is German. In a Polish archive it is just a word, and the year is read on its own.
        Assert.Equal("1950", Read("um 1950", polish));
        Assert.Equal("1953", Read("12. März 1953", DateLanguages.For("ja", [], "ja")));
        // Understood everywhere.
        Assert.Equal("1945–1955", Read("ca. 1950", polish));
        Assert.Equal("1945–1955", Read("~1950", polish));
        Assert.Equal("1953-03-12", Read("12.03.1953", polish));
        Assert.Equal("1950–1955", Read("1950–1955", polish));
    }

    [Fact]
    public void Whoever_types_the_date_is_understood_in_their_own_language()
    {
        Assert.Equal("1945–1955", Read("um 1950", DateLanguages.For("pl", [], "de")));
        Assert.Equal("1950–1959", Read("1950s", DateLanguages.For("pl", [], "en")));
    }

    [Fact]
    public void Several_languages_without_a_main_one_and_unknown_codes()
    {
        Assert.Equal(["de", "en"], DateLanguages.For(DateLanguages.Several, ["de"], "en").Codes);
        Assert.Equal(["en", "de"], DateLanguages.For(null, [], "en").Codes);
        Assert.Equal(["pl", "de"], DateLanguages.For("pl", ["", "not a language!"], "de").Codes);
    }

    [Fact]
    public void A_request_reads_dates_in_its_own_languages()
    {
        var before = DateLanguages.Current;
        try
        {
            DateLanguages.Current = DateLanguages.For("pl", [], "pl");
            Assert.True(DateRange.TryParse("12 marca 1953", out var range));
            Assert.Equal(new DateOnly(1953, 3, 12), range.From);
            var item = new ArchiveItem { Title = "List" };
            Assert.True(item.TrySetDate("12 marca 1953"));
            Assert.Equal(new DateOnly(1953, 3, 12), item.DateTo);
        }
        finally
        {
            DateLanguages.Current = before;
        }
    }
}
