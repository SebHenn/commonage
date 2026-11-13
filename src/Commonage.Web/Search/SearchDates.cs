using Commonage.Web.Domain;

namespace Commonage.Web.Search;

/// <summary>A date someone typed into the search, such as "1950s" or "March 1953", as they typed it and as the days it stands for.</summary>
public record SearchDate(string Text, DateOnly? From, DateOnly? To)
{
    /// <summary>The days in a form for people, such as "1950–1959".</summary>
    public string Period => new DateRange(From, To).ToString();
}

/// <summary>
/// Finds the dates in what someone typed into the search. "Mill Street 1950s" is the words "Mill Street" and the 1950s, so that it
/// also finds the tram of 1930–1955, which never says "1950s". The date still finds the word too: an item called "Team photo 1958"
/// is found by "1958" whether or not it is dated.
/// </summary>
public static class SearchDates
{
    public const int MaxDates = 3;

    public static (string Words, IReadOnlyList<SearchDate> Dates) Extract(string? text) => Extract(text, DateLanguages.Current);

    public static (string Words, IReadOnlyList<SearchDate> Dates) Extract(string? text, DateLanguages languages)
    {
        if (string.IsNullOrWhiteSpace(text))
            return ("", []);

        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        var used = new bool[tokens.Count];
        var dates = new List<SearchDate>();

        for (var i = 0; i < tokens.Count && dates.Count < MaxDates; i++)
        {
            if (used[i] || !tokens[i].Any(char.IsDigit))
                continue;
            var token = Clean(tokens[i]);
            var alone = Read(token, languages);

            // A word before can make it a month or a day ("March 1953") or change it ("circa 1950", "before 1945"). A word that
            // changes nothing ("Street 1950s") is left to the text, because the date reader looks past words it does not know.
            if (i > 0 && !used[i - 1] && !tokens[i - 1].Any(char.IsDigit)
                && Read($"{Clean(tokens[i - 1])} {token}", languages) is { } withBefore && withBefore != alone)
            {
                dates.Add(new SearchDate($"{Clean(tokens[i - 1])} {token}", withBefore.From, withBefore.To));
                used[i - 1] = used[i] = true;
            }
            // A word after, only where the number alone is no date: "19th century".
            else if (alone is null && i + 1 < tokens.Count && !tokens[i + 1].Any(char.IsDigit)
                && Read($"{token} {Clean(tokens[i + 1])}", languages) is { } withAfter)
            {
                dates.Add(new SearchDate($"{token} {Clean(tokens[i + 1])}", withAfter.From, withAfter.To));
                used[i] = used[i + 1] = true;
            }
            // A word that belongs to the decade: "1950er Jahre".
            else if (alone is { } range && i + 1 < tokens.Count && IsDecadeWithWord(token, Clean(tokens[i + 1]), languages))
            {
                dates.Add(new SearchDate($"{token} {Clean(tokens[i + 1])}", range.From, range.To));
                used[i] = used[i + 1] = true;
            }
            else if (alone is { } range2)
            {
                dates.Add(new SearchDate(token, range2.From, range2.To));
                used[i] = true;
            }
        }

        var words = string.Join(' ', tokens.Where((_, i) => !used[i]));
        return (words, dates);
    }

    private static bool IsDecadeWithWord(string token, string next, DateLanguages languages)
    {
        var text = DateLanguages.Fold($"{token} {next}");
        return languages.Words.Decade.Match(text) is { Success: true, Index: 0 } match && match.Length == text.Length;
    }

    private static DateRange? Read(string text, DateLanguages languages) =>
        DateRange.TryParse(text, languages, out var range) ? range : null;

    /// <summary>Without the commas and quotes around it: "1950s," and "(1953)".</summary>
    private static string Clean(string token) => token.Trim(',', ';', '"', '\'', '(', ')', '“', '”', '„', '«', '»');
}
