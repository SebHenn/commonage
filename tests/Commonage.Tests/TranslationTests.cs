using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Commonage.Web;
using Commonage.Web.Identity;
using Commonage.Web.Pages;
using Microsoft.Extensions.Localization;

namespace Commonage.Tests;

/// <summary>
/// English text is the key, and it lives in the code, so these tests read the code to find every text. Translators
/// get them in Resources/SharedResource.resx, the base file Weblate needs, which these tests keep up to date.
/// </summary>
public partial class TranslationTests
{
    /// <summary>Set this to 1 to rewrite the base file instead of failing when it is out of date.</summary>
    public const string UpdateVariable = "UPDATE_TRANSLATION_BASE";

    private static readonly Lazy<SortedSet<string>> Texts = new(FindTexts);

    [Fact]
    public void Every_text_in_the_code_has_a_translation()
    {
        foreach (var (language, translations) in Translations())
        {
            var missing = Texts.Value.Where(t => !translations.ContainsKey(t)).ToList();
            Assert.True(missing.Count == 0,
                $"SharedResource.{language}.resx has no translation for:\n{string.Join("\n", missing)}");
        }
    }

    [Fact]
    public void No_translation_is_left_over()
    {
        foreach (var (language, translations) in Translations())
        {
            var unused = translations.Keys.Where(k => !Texts.Value.Contains(k)).ToList();
            Assert.True(unused.Count == 0,
                $"SharedResource.{language}.resx translates texts the code no longer uses:\n{string.Join("\n", unused)}");
        }
    }

    [Fact]
    public void Translations_keep_every_placeholder()
    {
        foreach (var (language, translations) in Translations())
        {
            var wrong = translations
                .Where(t => !Placeholders(t.Key).SetEquals(Placeholders(t.Value)))
                .Select(t => $"{t.Key}\n  → {t.Value}")
                .ToList();
            Assert.True(wrong.Count == 0, $"In SharedResource.{language}.resx, these lose or add a {{0}}:\n{string.Join("\n", wrong)}");
        }
    }

    [Fact]
    public void Counts_have_every_form_their_language_needs()
    {
        foreach (var (language, translations) in Translations())
        {
            var needed = Plurals.CategoriesOf(language).Append(Plurals.Other).ToList();
            foreach (var (key, value) in translations.Where(t => Plurals.HasPlural(t.Key)))
            {
                var parts = Plurals.PluralParts(value);
                Assert.True(parts is { Count: > 0 }, $"In SharedResource.{language}.resx, this needs its count in ICU MessageFormat:\n{value}");
                foreach (var (argument, branches) in parts!)
                {
                    var lacking = needed.Where(c => !branches.ContainsKey(c)).ToList();
                    Assert.True(lacking.Count == 0,
                        $"In SharedResource.{language}.resx, the count {{{argument}}} needs the forms {string.Join(", ", lacking)}:\n{value}");
                }
            }
        }
    }

    [Fact]
    public void Texts_are_written_so_that_translators_can_read_them()
    {
        foreach (var text in Texts.Value)
        {
            // In ICU MessageFormat, which Weblate checks, an apostrophe before a brace starts quoted text.
            Assert.False(text.Contains("'{") || text.Contains("'}"), $"An apostrophe next to a brace reads differently in ICU MessageFormat: {text}");
            if (Plurals.HasPlural(text))
                Assert.True(Plurals.PluralParts(text) is { Count: > 0 }, $"This plural cannot be read: {text}");
            Assert.Equal(text.Trim(), text);
        }
        foreach (var key in ForReview.Keys)
            Assert.Contains(key, Texts.Value);
    }

    [Fact]
    public void The_base_file_for_translators_lists_every_text()
    {
        var path = Path.Combine(Root(), "src", "Commonage.Web", "Resources", "SharedResource.resx");
        var expected = BaseFile();
        var actual = File.Exists(path) ? File.ReadAllText(path) : "";
        if (actual.ReplaceLineEndings() == expected.ReplaceLineEndings())
            return;
        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            File.WriteAllText(path, expected, new UTF8Encoding(false));
            return;
        }
        Assert.Fail($"Resources/SharedResource.resx is out of date. Run the tests with {UpdateVariable}=1 to rewrite it, then commit it.");
    }

    [Theory]
    [InlineData(1, "1 dzień")]
    [InlineData(2, "2 dni")]
    [InlineData(5, "5 dni")]
    [InlineData(22, "22 dni")]
    public void Polish_days(int count, string expected) =>
        Assert.Equal(expected, Plurals.Format("{0, plural, one {# dzień} other {# dni}}", "pl", CultureInfo.GetCultureInfo("pl"), count));

    [Theory]
    [InlineData(1, "1 zdjęcie")]
    [InlineData(2, "2 zdjęcia")]
    [InlineData(4, "4 zdjęcia")]
    [InlineData(5, "5 zdjęć")]
    [InlineData(12, "12 zdjęć")]
    [InlineData(13, "13 zdjęć")]
    [InlineData(21, "21 zdjęć")]
    [InlineData(22, "22 zdjęcia")]
    [InlineData(25, "25 zdjęć")]
    [InlineData(0, "0 zdjęć")]
    [InlineData(102, "102 zdjęcia")]
    [InlineData(112, "112 zdjęć")]
    [InlineData(1000, "1 000 zdjęć")]
    public void Polish_photos(int count, string expected)
    {
        var text = Plurals.Format("{0, plural, one {# zdjęcie} few {# zdjęcia} many {# zdjęć} other {# zdjęcia}}", "pl",
            CultureInfo.GetCultureInfo("pl"), count);
        // Polish groups thousands with a no-break space.
        Assert.Equal(expected, text.Replace(' ', ' ').Replace(' ', ' '));
    }

    [Theory]
    [InlineData("en", 0, Plurals.Other)]
    [InlineData("en", 1, Plurals.One)]
    [InlineData("en", 2, Plurals.Other)]
    [InlineData("fr", 0, Plurals.One)]
    [InlineData("fr", 2, Plurals.Other)]
    [InlineData("ru", 21, Plurals.One)]
    [InlineData("ru", 11, Plurals.Many)]
    [InlineData("ru", 23, Plurals.Few)]
    [InlineData("cs", 3, Plurals.Few)]
    [InlineData("cs", 5, Plurals.Other)]
    [InlineData("ar", 0, Plurals.Zero)]
    [InlineData("ar", 2, Plurals.Two)]
    [InlineData("ar", 103, Plurals.Few)]
    [InlineData("ar", 111, Plurals.Many)]
    [InlineData("ar", 100, Plurals.Other)]
    [InlineData("ja", 1, Plurals.Other)]
    [InlineData("lt", 11, Plurals.Other)]
    [InlineData("lt", 21, Plurals.One)]
    [InlineData("ro", 101, Plurals.Few)]
    [InlineData("ro", 120, Plurals.Other)]
    public void Each_language_chooses_its_own_form(string language, int count, string expected) =>
        Assert.Equal(expected, Plurals.CategoryOf(language, count));

    [Fact]
    public void An_exact_number_comes_before_the_form_and_other_text_stays()
    {
        const string text = "{1}: {0, plural, =0 {nothing yet} one {one photo} other {# photos}}, sent by {2}";
        var en = CultureInfo.GetCultureInfo("en");
        Assert.Equal("Box: nothing yet, sent by Ann", Plurals.Format(text, "en", en, 0, "Box", "Ann"));
        Assert.Equal("Box: one photo, sent by Ann", Plurals.Format(text, "en", en, 1, "Box", "Ann"));
        Assert.Equal("Box: 1,200 photos, sent by Ann", Plurals.Format(text, "en", en, 1200, "Box", "Ann"));
    }

    [Theory]
    [InlineData("{0, plural, one {# day}}")]
    [InlineData("{0, plural, one {# day} other {# days}")]
    [InlineData("{0, plural, one {# day} one {# days} other {# days}}")]
    [InlineData("{0, plural one {# day} other {# days}}")]
    public void A_broken_plural_is_found(string text) => Assert.Null(Plurals.PluralParts(text));

    [Fact]
    public void An_untranslated_text_keeps_English_forms()
    {
        // A Polish reader of a text nobody translated yet reads English, so "few" must not be looked for.
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pl");
            Assert.Equal("3 days", new Untranslated().Plural("{0, plural, one {# day} other {# days}}", 3));
            Assert.Equal("1 day", new Untranslated().Plural("{0, plural, one {# day} other {# days}}", 1));
        }
        finally
        {
            CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = previous;
        }
    }

    private sealed class Untranslated : IStringLocalizer
    {
        public LocalizedString this[string name] => new(name, name, resourceNotFound: true);
        public LocalizedString this[string name, params object[] arguments] => new(name, string.Format(name, arguments), true);
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }

    /// <summary>Texts a second person should look at in every language, because people agree to them or rely on them.</summary>
    private static Dictionary<string, string> ForReview => new(
        PrivacyText.Paragraphs(new Untranslated()).Select(p => KeyValuePair.Create(p,
            "Part of the privacy text an archive starts with. Please have a second translator review it.")))
    {
        ["I agree that the archive may keep what I send in, describe it and publish it on this website, also where a link to it is shared and in its list of what is new. If I chose so above, my name is shown with it. I can ask the archive to take it down at any time, but it cannot reach copies others have made."] =
            "What a contributor agrees to. It is stored with what they send in, in their language. Please have a second translator review it.",
        ["I also allow anyone to reuse it for any purpose, with credit (CC BY 4.0)."] =
            "Added to what a contributor agrees to. Please have a second translator review it.",
        ["I also allow anyone to reuse it for non-commercial purposes, with credit (CC BY-NC 4.0)."] =
            "Added to what a contributor agrees to. Please have a second translator review it.",
        ["Choose yes only if this is yours to give. It cannot be taken back from those who have already reused it."] =
            "Explains the reuse offer to a contributor. Please have a second translator review it.",
    };

    private static string BaseFile()
    {
        var notes = ForReview;
        var text = new StringBuilder();
        text.Append("""
            <?xml version="1.0" encoding="utf-8"?>
            <!-- English, for translators in Weblate. Written by TranslationTests from the code, so do not change it by hand. -->
            <root>
              <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>
              <resheader name="version"><value>2.0</value></resheader>
              <resheader name="reader"><value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
              <resheader name="writer"><value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>

            """);
        foreach (var key in Texts.Value)
        {
            var comment = notes.TryGetValue(key, out var note) ? note
                : Plurals.HasPlural(key) ? "Has a count. Write a form for each one your language needs, in ICU MessageFormat."
                : null;
            text.Append($"  <data name=\"{Escape(key, true)}\" xml:space=\"preserve\"><value>{Escape(key, false)}</value>");
            if (comment is not null)
                text.Append($"<comment>{Escape(comment, false)}</comment>");
            text.Append("</data>\n");
        }
        text.Append("</root>\n");
        return text.ToString();
    }

    private static string Escape(string text, bool attribute)
    {
        var escaped = text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        return attribute ? escaped.Replace("\"", "&quot;") : escaped;
    }

    private static HashSet<string> Placeholders(string text) =>
        [.. Placeholder().Matches(text).Select(m => m.Groups[1].Value)];

    [GeneratedRegex(@"\{(\d+)\s*[,}]")]
    private static partial Regex Placeholder();

    private static IEnumerable<(string Language, Dictionary<string, string> Translations)> Translations()
    {
        var folder = Path.Combine(Root(), "src", "Commonage.Web", "Resources");
        var files = Directory.GetFiles(folder, "SharedResource.*.resx");
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var language = Path.GetFileNameWithoutExtension(file)["SharedResource.".Length..];
            var translations = XDocument.Load(file).Root!.Elements("data")
                .ToDictionary(d => (string)d.Attribute("name")!, d => (string)d.Element("value")!, StringComparer.Ordinal);
            yield return (language, translations);
        }
    }

    private static SortedSet<string> FindTexts()
    {
        var source = Path.Combine(Root(), "src", "Commonage.Web");
        var texts = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(source, "*.*", SearchOption.AllDirectories))
        {
            if (!file.EndsWith(".cs", StringComparison.Ordinal) && !file.EndsWith(".cshtml", StringComparison.Ordinal))
                continue;
            var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
            if (relative.StartsWith("bin/") || relative.StartsWith("obj/") || relative.StartsWith("Data/Migrations/"))
                continue;
            var code = File.ReadAllText(file);
            foreach (var regex in new[] { Lookup(), PluralLookup(), AttributeText() })
            {
                foreach (Match match in regex.Matches(code))
                    texts.Add(Unescape(match.Groups["text"].Value));
            }
        }
        // Shown through a variable, so not found above.
        foreach (var role in Roles.All)
            texts.Add(role);
        return texts;
    }

    private const string Literal = @"""(?<text>(?:[^""\\\r\n]|\\.)*)""";

    [GeneratedRegex(@"(?<![\w.])(?:T|localizer)\[\s*" + Literal)]
    private static partial Regex Lookup();

    [GeneratedRegex(@"(?<![\w.])(?:T|localizer)\.Plural\(\s*" + Literal)]
    private static partial Regex PluralLookup();

    [GeneratedRegex(@"(?<![\w.])(?:Display|Required|StringLength|EmailAddress|Compare|Range|MaxLength|MinLength)\s*\([^\]\r\n]*?\b(?:Name|ErrorMessage)\s*=\s*" + Literal)]
    private static partial Regex AttributeText();

    private static string Unescape(string literal) => Regex.Unescape(literal);

    internal static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Commonage.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("The repository was not found above " + AppContext.BaseDirectory);
    }
}
