using System.Text.RegularExpressions;

namespace Commonage.Web.Domain;

/// <summary>
/// The earliest and latest day an uncertain date can stand for. Either end can be open, as in "before 1945".
/// <see cref="TryParse(string?, out DateRange)"/> reads dates the way people write them, for example "1953", "12.03.1953",
/// "March 1953", "circa 1950", "1950s", "1950er Jahre", "1950–1955" or "vor 1945", in the languages of
/// <see cref="DateLanguages.Current"/>. Only the days stay stored, so other languages never change what is stored.
/// </summary>
public readonly partial record struct DateRange(DateOnly? From, DateOnly? To)
{
    /// <summary>How many years "circa" adds on each side of a year, decade or century.</summary>
    public const int ApproximateYears = 5;

    public const int MaxTextLength = 100;

    public static bool TryParse(string? text, out DateRange range) => TryParse(text, DateLanguages.Current, out range);

    public static bool TryParse(string? text, DateLanguages languages, out DateRange range)
    {
        range = default;
        var words = languages.Words;
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength)
            return false;

        var s = DateLanguages.Fold(text.Trim()).Replace('–', '-').Replace('—', '-');
        var years = Year().Matches(s).Select(m => int.Parse(m.Value)).Distinct().ToList();

        DateOnly from, to;
        bool dayOrMonth = false;
        if (years.Count >= 2)
        {
            // "1950-1955", "between 1950 and 1955", "zwischen 1950 und 1955"
            from = new DateOnly(years.Min(), 1, 1);
            to = new DateOnly(years.Max(), 12, 31);
        }
        // A day or month that does not exist, such as 31.02.1953, is a typo. It is rejected rather than read as a year.
        else if (FindDay(s, words) is { } d)
        {
            if (!TryCreate(d.Year, d.Month, d.Day, out var day))
                return false;
            (from, to, dayOrMonth) = (day, day, true);
        }
        else if (FindMonth(s, words) is { } m)
        {
            if (!TryCreate(m.Year, m.Month, "1", out var month))
                return false;
            (from, to, dayOrMonth) = (month, month.AddMonths(1).AddDays(-1), true);
        }
        else if (words.Decade.Match(s) is { Success: true } decade)
        {
            var start = int.Parse(decade.Groups[1].Value) * 10;
            (from, to) = (new DateOnly(start, 1, 1), new DateOnly(start + 9, 12, 31));
        }
        else if (years.Count == 1)
        {
            (from, to) = (new DateOnly(years[0], 1, 1), new DateOnly(years[0], 12, 31));
        }
        else if (words.Century.Match(s) is { Success: true } century && int.Parse(century.Groups[1].Value) is >= 11 and <= 21 and var c)
        {
            // "19th century" and "19. Jahrhundert" mean the 1800s, as most people use them.
            (from, to) = (new DateOnly((c - 1) * 100, 1, 1), new DateOnly((c - 1) * 100 + 99, 12, 31));
        }
        else
        {
            return false;
        }

        if (words.Approximately.IsMatch(s))
        {
            var margin = dayOrMonth ? 1 : ApproximateYears;
            (from, to) = (from.AddYears(-margin), to.AddYears(margin));
        }

        // Open-ended forms only make sense with a single point in time, not with a range of years.
        range = years.Count >= 2 ? new(from, to)
            : words.Before.IsMatch(s) ? new(null, from.AddDays(-1))
            : words.Until.IsMatch(s) ? new(null, to)
            : words.After.IsMatch(s) ? new(to.AddDays(1), null)
            : words.Since.IsMatch(s) ? new(from, null)
            : new(from, to);
        return true;
    }

    /// <summary>A short, language-neutral description such as "1945–1955", "1953-03-12" or "–1944".</summary>
    public override string ToString() => (From, To) switch
    {
        ({ } f, { } t) when f == t => f.ToString("yyyy-MM-dd"),
        ({ } f, { } t) when IsWholeYears(f, t) => f.Year == t.Year ? $"{f.Year}" : $"{f.Year}–{t.Year}",
        ({ } f, { } t) => $"{f:yyyy-MM-dd} – {t:yyyy-MM-dd}",
        ({ } f, null) => f is { Month: 1, Day: 1 } ? $"{f.Year}–" : $"{f:yyyy-MM-dd} –",
        (null, { } t) => t is { Month: 12, Day: 31 } ? $"–{t.Year}" : $"– {t:yyyy-MM-dd}",
        _ => "",
    };

    private static bool IsWholeYears(DateOnly from, DateOnly to) => from is { Month: 1, Day: 1 } && to is { Month: 12, Day: 31 };

    private static (string Year, string Month, string Day)? FindDay(string s, DateLanguages.DateWords words)
    {
        if (IsoDate().Match(s) is { Success: true } iso)
            return (iso.Groups[1].Value, iso.Groups[2].Value, iso.Groups[3].Value);
        if (GermanDate().Match(s) is { Success: true } german)
            return (german.Groups[3].Value, german.Groups[2].Value, german.Groups[1].Value);
        if (DayMonthNameYear().Match(s) is { Success: true } dayFirst && words.MonthNumber(dayFirst.Groups[2].Value) is { } m1)
            return (dayFirst.Groups[3].Value, m1.ToString(), dayFirst.Groups[1].Value);
        if (MonthNameDayYear().Match(s) is { Success: true } monthFirst && words.MonthNumber(monthFirst.Groups[1].Value) is { } m2)
            return (monthFirst.Groups[3].Value, m2.ToString(), monthFirst.Groups[2].Value);
        return null;
    }

    private static (string Year, string Month)? FindMonth(string s, DateLanguages.DateWords words)
    {
        if (MonthNameYear().Match(s) is { Success: true } named && words.MonthNumber(named.Groups[1].Value) is { } m)
            return (named.Groups[2].Value, m.ToString());
        if (IsoMonth().Match(s) is { Success: true } iso)
            return (iso.Groups[1].Value, iso.Groups[2].Value);
        if (NumericMonth().Match(s) is { Success: true } numeric)
            return (numeric.Groups[2].Value, numeric.Groups[1].Value);
        return null;
    }

    private static bool TryCreate(string year, string month, string day, out DateOnly date)
    {
        date = default;
        int y = int.Parse(year), m = int.Parse(month), d = int.Parse(day);
        if (m is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, m))
            return false;
        date = new DateOnly(y, m, d);
        return true;
    }

    // Four-digit years from 1000 to 2999. Other numbers (days, months, house numbers) are not years.
    [GeneratedRegex(@"(?<!\d)[12]\d{3}(?!\d)")]
    private static partial Regex Year();

    [GeneratedRegex(@"(?<!\d)([12]\d{3})-(\d{1,2})-(\d{1,2})(?!\d)")]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"(?<!\d)(\d{1,2})\.\s*(\d{1,2})\.\s*([12]\d{3})(?!\d)")]
    private static partial Regex GermanDate();

    [GeneratedRegex(@"(?<!\d)(\d{1,2})\.?\s+(\p{L}+)\.?\s+([12]\d{3})(?!\d)")]
    private static partial Regex DayMonthNameYear();

    [GeneratedRegex(@"(\p{L}+)\.?\s+(\d{1,2}),?\s+([12]\d{3})(?!\d)")]
    private static partial Regex MonthNameDayYear();

    [GeneratedRegex(@"(\p{L}+)\.?\s+([12]\d{3})(?!\d)")]
    private static partial Regex MonthNameYear();

    [GeneratedRegex(@"(?<!\d)([12]\d{3})-(\d{1,2})(?![\d-])")]
    private static partial Regex IsoMonth();

    [GeneratedRegex(@"(?<![\d.])(\d{1,2})[./]([12]\d{3})(?!\d)")]
    private static partial Regex NumericMonth();
}
