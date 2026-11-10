using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Commonage.Web.Domain;

/// <summary>
/// The words one language uses around a date, such as "circa" and "before". Month names come from .NET for every language,
/// so a language works without a vocabulary, with month names only. It is written in C#, not translated, because some of
/// it is grammar rather than words. Words are written in lower case; accents do not matter, since they are removed before
/// reading.
/// </summary>
public sealed record DateVocabulary(
    string Language,
    IReadOnlyList<string> Approximately,
    IReadOnlyList<string> Before,
    IReadOnlyList<string> Until,
    IReadOnlyList<string> After,
    IReadOnlyList<string> Since,
    IReadOnlyList<string> Century,
    string CenturyOrdinal,
    string DecadeEnding,
    IReadOnlyDictionary<string, int> ExtraMonths)
{
    /// <summary>Understood in every language: "circa 1950", "ca. 1950", "~1950".</summary>
    public static readonly IReadOnlyList<string> AlwaysApproximately = ["circa", "ca.", "ca", "~"];

    public static readonly DateVocabulary English = new(
        "en",
        Approximately: ["c.", "approx.", "approx", "approximately", "about", "around"],
        Before: ["before"],
        Until: ["until", "till"],
        After: ["after"],
        Since: ["since", "from"],
        Century: ["century"],
        CenturyOrdinal: "st|nd|rd|th",
        DecadeEnding: "'?s",
        ExtraMonths: new Dictionary<string, int>());

    public static readonly DateVocabulary German = new(
        "de",
        Approximately: ["um", "etwa", "ungefähr"],
        Before: ["vor"],
        Until: ["bis"],
        After: ["nach"],
        Since: ["seit", "ab"],
        Century: ["jahrhundert", "jh"],
        CenturyOrdinal: @"\.",
        // "1950er", and "1950er Jahre" as people often write it.
        DecadeEnding: @"ern?(?:\s+jahren?)?",
        // Austrian. .NET knows only "Januar".
        ExtraMonths: new Dictionary<string, int> { ["jänner"] = 1 });

    public static readonly IReadOnlyList<DateVocabulary> All = [English, German];

    public static DateVocabulary? For(string language) => All.FirstOrDefault(v => v.Language == language);
}

/// <summary>
/// The languages dates are read in, most important first. A month name that means different months in two languages, such
/// as "listopad" (November in Polish, October in Croatian), is read in the first of them.
/// </summary>
public sealed partial class DateLanguages
{
    /// <summary>Before an archive chose the languages of its material, dates were read in English and German.</summary>
    public static readonly IReadOnlyList<string> BeforeTheSetting = ["en", "de"];

    /// <summary>Stands for "several languages, none of them most" where a language is chosen.</summary>
    public const string Several = "mul";

    private static readonly AsyncLocal<DateLanguages?> CurrentValue = new();
    private static readonly ConcurrentDictionary<string, DateWords> Compiled = new();

    public static DateLanguages Default { get; } = new(BeforeTheSetting);

    /// <summary>The languages of this request: set for each one from the archive's settings and the reader's language.</summary>
    public static DateLanguages Current
    {
        get => CurrentValue.Value ?? Default;
        set => CurrentValue.Value = value;
    }

    public IReadOnlyList<string> Codes { get; }

    public DateLanguages(IEnumerable<string> codes)
    {
        Codes = codes.Select(c => c.Trim().ToLowerInvariant()).Where(Readable).Distinct().ToList();
    }

    /// <summary>
    /// The material's main language, its other languages, and the language of whoever types the date. An archive that has
    /// not chosen a main language yet reads English and German, as it always did.
    /// </summary>
    public static DateLanguages For(string? mainLanguage, IEnumerable<string> otherLanguages, string interfaceLanguage)
    {
        IEnumerable<string> content = mainLanguage is null ? BeforeTheSetting
            : mainLanguage == Several ? otherLanguages
            : otherLanguages.Prepend(mainLanguage);
        return new(content.Append(interfaceLanguage));
    }

    internal DateWords Words => Compiled.GetOrAdd(string.Join(',', Codes), _ => new DateWords(Codes));

    /// <summary>Lower case, without accents, so that "Marz" finds "März" and "pazdziernika" finds "października".</summary>
    internal static string Fold(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var folded = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                folded.Append(c);
        }
        return folded.ToString().Normalize(NormalizationForm.FormC);
    }

    private static bool Readable(string code)
    {
        if (code.Length == 0 || code == Several)
            return false;
        try
        {
            return CultureInfo.GetCultureInfo(code) is { } culture && culture.DateTimeFormat is not null;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    /// <summary>The words of some languages, made into patterns once.</summary>
    internal sealed class DateWords
    {
        private readonly List<(string Name, int Month)> months = [];

        public Regex Approximately { get; }
        public Regex Before { get; }
        public Regex Until { get; }
        public Regex After { get; }
        public Regex Since { get; }
        public Regex Century { get; }
        public Regex Decade { get; }

        public DateWords(IReadOnlyList<string> languages)
        {
            var vocabularies = languages.Select(DateVocabulary.For).OfType<DateVocabulary>().ToList();
            foreach (var language in languages)
            {
                foreach (var (name, month) in DateVocabulary.For(language)?.ExtraMonths ?? new Dictionary<string, int>())
                    months.Add((Fold(name), month));
                var format = CultureInfo.GetCultureInfo(language).DateTimeFormat;
                foreach (var names in new[] { format.MonthNames, format.MonthGenitiveNames, format.AbbreviatedMonthNames, format.AbbreviatedMonthGenitiveNames })
                {
                    for (var i = 0; i < 12 && i < names.Length; i++)
                    {
                        var name = Fold(names[i]).Trim().TrimEnd('.');
                        if (name.Length > 0)
                            months.Add((name, i + 1));
                    }
                }
            }

            Approximately = new(@"(?:^|\s)(?:" + Words(vocabularies.SelectMany(v => v.Approximately).Concat(DateVocabulary.AlwaysApproximately)) + @")(?=\s|\d|$)");
            Before = WordPattern(vocabularies.SelectMany(v => v.Before));
            Until = WordPattern(vocabularies.SelectMany(v => v.Until));
            After = WordPattern(vocabularies.SelectMany(v => v.After));
            Since = WordPattern(vocabularies.SelectMany(v => v.Since));
            var ordinals = string.Join('|', vocabularies.Select(v => v.CenturyOrdinal));
            Century = vocabularies.Count == 0 ? Never
                : new(@"(?<!\d)(\d{1,2})(?:" + ordinals + @")?\s*(?:" + Words(vocabularies.SelectMany(v => v.Century)) + @")\b");
            Decade = vocabularies.Count == 0 ? Never
                : new(@"(?<!\d)([12]\d{2})0\s*(?:" + string.Join('|', vocabularies.Select(v => v.DecadeEnding)) + @")\b");
        }

        /// <summary>A month's full name, or its first three letters or more ("Sept", "Dez").</summary>
        public int? MonthNumber(string word)
        {
            if (word.Length < 3)
                return null;
            foreach (var (name, month) in months)
            {
                if (name.StartsWith(word, StringComparison.Ordinal))
                    return month;
            }
            return null;
        }

        private static readonly Regex Never = new("(?!)");

        private static Regex WordPattern(IEnumerable<string> words)
        {
            var list = words.ToList();
            return list.Count == 0 ? Never : new(@"\b(?:" + Words(list) + @")\b");
        }

        /// <summary>Longest first, so that "approx." is tried before "approx".</summary>
        private static string Words(IEnumerable<string> words) =>
            string.Join('|', words.Select(Fold).Distinct().OrderByDescending(w => w.Length).Select(Regex.Escape));
    }
}
