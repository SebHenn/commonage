using System.Text.RegularExpressions;

namespace Commonage.Tests;

/// <summary>
/// DateRange as it was before dates followed the archive's languages (M17), kept unchanged so that tests can check that
/// every date it accepted is still accepted, with the same meaning.
/// The earliest and latest day an uncertain date can stand for. Either end can be open, as in "before 1945".
/// <see cref="TryParse"/> reads dates the way people write them in English or German, for example
/// "1953", "12.03.1953", "March 1953", "circa 1950", "1950s", "1950er Jahre", "1950–1955" or "vor 1945".
/// </summary>
internal readonly partial record struct DateRangeBeforeM17(DateOnly? From, DateOnly? To)
{
    /// <summary>How many years "circa" adds on each side of a year, decade or century.</summary>
    public const int ApproximateYears = 5;

    public const int MaxTextLength = 100;

    private static readonly string[][] MonthNames =
    [
        ["january", "januar", "jänner"],
        ["february", "februar"],
        ["march", "märz"],
        ["april"],
        ["may", "mai"],
        ["june", "juni"],
        ["july", "juli"],
        ["august"],
        ["september"],
        ["october", "oktober"],
        ["november"],
        ["december", "dezember"],
    ];

    public static bool TryParse(string? text, out DateRangeBeforeM17 range)
    {
        range = default;
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength)
            return false;

        var s = text.Trim().ToLowerInvariant().Replace('–', '-').Replace('—', '-');
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
        else if (FindDay(s) is { } d)
        {
            if (!TryCreate(d.Year, d.Month, d.Day, out var day))
                return false;
            (from, to, dayOrMonth) = (day, day, true);
        }
        else if (FindMonth(s) is { } m)
        {
            if (!TryCreate(m.Year, m.Month, "1", out var month))
                return false;
            (from, to, dayOrMonth) = (month, month.AddMonths(1).AddDays(-1), true);
        }
        else if (Decade().Match(s) is { Success: true } decade)
        {
            var start = int.Parse(decade.Groups[1].Value) * 10;
            (from, to) = (new DateOnly(start, 1, 1), new DateOnly(start + 9, 12, 31));
        }
        else if (years.Count == 1)
        {
            (from, to) = (new DateOnly(years[0], 1, 1), new DateOnly(years[0], 12, 31));
        }
        else if (Century().Match(s) is { Success: true } century && int.Parse(century.Groups[1].Value) is >= 11 and <= 21 and var c)
        {
            // "19th century" and "19. Jahrhundert" mean the 1800s, as most people use them.
            (from, to) = (new DateOnly((c - 1) * 100, 1, 1), new DateOnly((c - 1) * 100 + 99, 12, 31));
        }
        else
        {
            return false;
        }

        if (Approximate().IsMatch(s))
        {
            var margin = dayOrMonth ? 1 : ApproximateYears;
            (from, to) = (from.AddYears(-margin), to.AddYears(margin));
        }

        // Open-ended forms only make sense with a single point in time, not with a range of years.
        range = years.Count >= 2 ? new(from, to)
            : Before().IsMatch(s) ? new(null, from.AddDays(-1))
            : Until().IsMatch(s) ? new(null, to)
            : After().IsMatch(s) ? new(to.AddDays(1), null)
            : Since().IsMatch(s) ? new(from, null)
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

    private static (string Year, string Month, string Day)? FindDay(string s)
    {
        if (IsoDate().Match(s) is { Success: true } iso)
            return (iso.Groups[1].Value, iso.Groups[2].Value, iso.Groups[3].Value);
        if (GermanDate().Match(s) is { Success: true } german)
            return (german.Groups[3].Value, german.Groups[2].Value, german.Groups[1].Value);
        if (DayMonthNameYear().Match(s) is { Success: true } dayFirst && MonthNumber(dayFirst.Groups[2].Value) is { } m1)
            return (dayFirst.Groups[3].Value, m1.ToString(), dayFirst.Groups[1].Value);
        if (MonthNameDayYear().Match(s) is { Success: true } monthFirst && MonthNumber(monthFirst.Groups[1].Value) is { } m2)
            return (monthFirst.Groups[3].Value, m2.ToString(), monthFirst.Groups[2].Value);
        return null;
    }

    private static (string Year, string Month)? FindMonth(string s)
    {
        if (MonthNameYear().Match(s) is { Success: true } named && MonthNumber(named.Groups[1].Value) is { } m)
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

    /// <summary>Full month names in English and German, or their first three letters or more ("Sept", "Dez").</summary>
    private static int? MonthNumber(string word)
    {
        if (word.Length < 3)
            return null;
        for (var i = 0; i < MonthNames.Length; i++)
        {
            if (MonthNames[i].Any(name => name.StartsWith(word, StringComparison.Ordinal)))
                return i + 1;
        }
        return null;
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

    // "1950s", "1950's", "1950er", "1950er Jahre", "in den 1950ern"
    [GeneratedRegex(@"(?<!\d)([12]\d{2})0\s*(?:'?s|ern?)\b")]
    private static partial Regex Decade();

    [GeneratedRegex(@"(?<!\d)(\d{1,2})(?:st|nd|rd|th|\.)?\s*(?:century|jahrhundert|jh)\b")]
    private static partial Regex Century();

    [GeneratedRegex(@"(?:^|\s)(?:circa|ca\.?|c\.|approx\.?|approximately|about|around|um|etwa|ungefähr|~)(?=\s|\d|$)")]
    private static partial Regex Approximate();

    [GeneratedRegex(@"\b(?:before|vor)\b")]
    private static partial Regex Before();

    [GeneratedRegex(@"\b(?:until|till|bis)\b")]
    private static partial Regex Until();

    [GeneratedRegex(@"\b(?:after|nach)\b")]
    private static partial Regex After();

    [GeneratedRegex(@"\b(?:since|from|seit|ab)\b")]
    private static partial Regex Since();
}
