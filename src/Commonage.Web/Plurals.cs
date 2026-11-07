using System.Globalization;
using System.Text;
using Microsoft.Extensions.Localization;

namespace Commonage.Web;

/// <summary>
/// Counts in sentences, such as "1 day" and "30 days". Each language has its own forms: English has two, Polish four
/// ("1 dzień", "2 dni", "5 dni"). A text with a count holds all of a language's forms in ICU MessageFormat, which Weblate
/// checks for translators: <c>{0, plural, one {# day} other {# days}}</c>. Only this part of ICU is read: plural with
/// <c>=n</c>, the categories below and <c>#</c>. Apostrophes are plain text.
/// </summary>
public static class Plurals
{
    public const string Zero = "zero", One = "one", Two = "two", Few = "few", Many = "many", Other = "other";

    /// <summary>
    /// The text in the reader's language, with each plural part chosen for its count and every <c>{n}</c> filled in.
    /// A text without a translation stays English, so English rules choose its form.
    /// </summary>
    public static string Plural(this IStringLocalizer localizer, string text, params object[] arguments)
    {
        var found = localizer[text];
        var culture = CultureInfo.CurrentCulture;
        var language = found.ResourceNotFound ? "en" : CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return Format(found.Value, language, culture, arguments);
    }

    public static string Format(string message, string language, CultureInfo culture, params object[] arguments)
    {
        var chosen = Choose(message, language, culture, arguments);
        return string.Format(culture, chosen, arguments);
    }

    /// <summary>
    /// The forms a language uses for whole numbers, besides "other", which ICU always asks for.
    /// From the CLDR plural rules; a language not listed is treated like English.
    /// </summary>
    public static IReadOnlyList<string> CategoriesOf(string language) => Rules(language).Categories;

    public static string CategoryOf(string language, long count) => Rules(language).Choose(Math.Abs(count));

    /// <summary>Whether the text holds a plural part, and so has to be written in ICU MessageFormat by translators too.</summary>
    public static bool HasPlural(string message) => Parts(message).Any(p => p.Branches is not null);

    /// <summary>The plural parts of a text: which argument they count, and the text for each form. Null if it cannot be read.</summary>
    public static IReadOnlyList<(int Argument, IReadOnlyDictionary<string, string> Branches)>? PluralParts(string message)
    {
        try
        {
            return Parts(message).Where(p => p.Branches is not null).Select(p => (p.Argument, p.Branches!)).ToList();
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string Choose(string message, string language, CultureInfo culture, object[] arguments)
    {
        if (!message.Contains("plural", StringComparison.Ordinal))
            return message;
        var result = new StringBuilder();
        foreach (var part in Parts(message))
        {
            if (part.Branches is null)
            {
                result.Append(part.Text);
                continue;
            }
            var count = Convert.ToInt64(arguments[part.Argument], CultureInfo.InvariantCulture);
            var branch = part.Branches.TryGetValue("=" + count, out var exact) ? exact
                : part.Branches.TryGetValue(CategoryOf(language, count), out var form) ? form
                : part.Branches[Other];
            result.Append(branch.Replace("#", count.ToString("N0", culture), StringComparison.Ordinal));
        }
        return result.ToString();
    }

    private record Part(string Text, int Argument = -1, IReadOnlyDictionary<string, string>? Branches = null);

    /// <summary>Splits a message into plain text, with its <c>{n}</c> left for string.Format, and plural parts.</summary>
    private static List<Part> Parts(string message)
    {
        var parts = new List<Part>();
        var plain = new StringBuilder();
        var i = 0;
        while (i < message.Length)
        {
            if (message[i] == '{' && PluralStart(message, i) is { } start)
            {
                if (plain.Length > 0)
                {
                    parts.Add(new Part(plain.ToString()));
                    plain.Clear();
                }
                var (branches, end) = ReadBranches(message, start.BranchesAt);
                parts.Add(new Part("", start.Argument, branches));
                i = end;
                continue;
            }
            plain.Append(message[i]);
            i++;
        }
        if (plain.Length > 0)
            parts.Add(new Part(plain.ToString()));
        return parts;
    }

    /// <summary>"{0, plural," at this position: the argument and where its branches begin.</summary>
    private static (int Argument, int BranchesAt)? PluralStart(string message, int at)
    {
        var comma = message.IndexOf(',', at);
        if (comma < 0 || !int.TryParse(message.AsSpan(at + 1, comma - at - 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var argument))
            return null;
        var rest = message.AsSpan(comma + 1).TrimStart();
        if (!rest.StartsWith("plural", StringComparison.Ordinal))
            return null;
        var afterWord = message.IndexOf("plural", comma, StringComparison.Ordinal) + "plural".Length;
        var secondComma = afterWord;
        while (secondComma < message.Length && char.IsWhiteSpace(message[secondComma]))
            secondComma++;
        if (secondComma >= message.Length || message[secondComma] != ',')
            throw new FormatException("A plural needs a comma after the word plural.");
        return (argument, secondComma + 1);
    }

    private static (Dictionary<string, string> Branches, int End) ReadBranches(string message, int at)
    {
        var branches = new Dictionary<string, string>(StringComparer.Ordinal);
        var i = at;
        while (true)
        {
            while (i < message.Length && char.IsWhiteSpace(message[i]))
                i++;
            if (i >= message.Length)
                throw new FormatException("A plural is not closed with }.");
            if (message[i] == '}')
                break;
            var open = message.IndexOf('{', i);
            if (open < 0)
                throw new FormatException("A plural form has no text in { }.");
            var selector = message[i..open].Trim();
            if (selector.Length == 0 || selector.Contains(' '))
                throw new FormatException($"\"{selector}\" is not a plural form.");
            var depth = 0;
            var close = open;
            for (; close < message.Length; close++)
            {
                if (message[close] == '{')
                    depth++;
                else if (message[close] == '}' && --depth == 0)
                    break;
            }
            if (close >= message.Length)
                throw new FormatException("A plural form is not closed with }.");
            if (!branches.TryAdd(selector, message[(open + 1)..close]))
                throw new FormatException($"The plural form {selector} appears twice.");
            i = close + 1;
        }
        if (!branches.ContainsKey(Other))
            throw new FormatException("A plural needs the form other.");
        return (branches, i + 1);
    }

    private sealed record Rule(IReadOnlyList<string> Categories, Func<long, string> Choose);

    private static Rule Rules(string language) => language switch
    {
        // No forms for numbers.
        "ja" or "zh" or "ko" or "vi" or "th" or "id" or "ms" or "lo" or "my" or "km" => new([], _ => Other),
        // One for 0 and 1.
        "hi" or "bn" or "fa" or "gu" or "kn" or "mr" or "zu" or "am" or "hy" or "ff" or "kab" => new([One], n => n is 0 or 1 ? One : Other),
        // And many for whole millions.
        "fr" or "pt" => new([One, Many], n => n is 0 or 1 ? One : n % 1_000_000 == 0 ? Many : Other),
        "es" or "it" or "ca" => new([One, Many], n => n == 1 ? One : n != 0 && n % 1_000_000 == 0 ? Many : Other),
        "pl" => new([One, Few, Many], n => n == 1 ? One : n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? Few : Many),
        "ru" or "uk" or "be" => new([One, Few, Many], n =>
            n % 10 == 1 && n % 100 != 11 ? One : n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? Few : Many),
        "hr" or "sr" or "bs" => new([One, Few], n =>
            n % 10 == 1 && n % 100 != 11 ? One : n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? Few : Other),
        "cs" or "sk" => new([One, Few], n => n == 1 ? One : n is >= 2 and <= 4 ? Few : Other),
        "sl" => new([One, Two, Few], n => (n % 100) switch { 1 => One, 2 => Two, 3 or 4 => Few, _ => Other }),
        "lt" => new([One, Few], n =>
            n % 100 is >= 11 and <= 19 ? Other : n % 10 == 1 ? One : n % 10 >= 2 ? Few : Other),
        "lv" => new([Zero, One], n => n % 10 == 0 || n % 100 is >= 11 and <= 19 ? Zero : n % 10 == 1 ? One : Other),
        "ro" => new([One, Few], n => n == 1 ? One : n == 0 || n % 100 is >= 1 and <= 19 ? Few : Other),
        "ar" => new([Zero, One, Two, Few, Many], n => n switch
        {
            0 => Zero,
            1 => One,
            2 => Two,
            _ => (n % 100) switch { >= 3 and <= 10 => Few, >= 11 and <= 99 => Many, _ => Other },
        }),
        "he" => new([One, Two], n => n switch { 1 => One, 2 => Two, _ => Other }),
        "ga" => new([One, Two, Few, Many], n => n switch { 1 => One, 2 => Two, >= 3 and <= 6 => Few, >= 7 and <= 10 => Many, _ => Other }),
        "cy" => new([Zero, One, Two, Few, Many], n => n switch { 0 => Zero, 1 => One, 2 => Two, 3 => Few, 6 => Many, _ => Other }),
        "is" or "mk" => new([One], n => n % 10 == 1 && n % 100 != 11 ? One : Other),
        // English, German and most others: one for exactly 1.
        _ => new([One], n => n == 1 ? One : Other),
    };
}
