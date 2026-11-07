using System.Globalization;

namespace Commonage.Web;

/// <summary>The languages of the interface, in one place. To add one, add its resource file and its code here.</summary>
public class SupportedLanguages(IConfiguration configuration)
{
    /// <summary>Languages:ForTesting adds languages without translations, to test right-to-left and long text.</summary>
    public IReadOnlyList<string> Codes { get; } =
    [
        "en",
        "de",
        .. (configuration["Languages:ForTesting"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
    ];

    public bool Offers(string? code) => code is not null && Codes.Contains(code);

    /// <summary>
    /// Every language .NET knows by a code of its own, such as "pl" or "haw", sorted by its name in that language. The
    /// material can be in any of them, not only in the languages of the interface.
    /// </summary>
    public static IReadOnlyList<string> AllLanguages { get; } = CultureInfo.GetCultures(CultureTypes.NeutralCultures)
        .Where(c => c.Name.Length is 2 or 3 && c.Name.All(char.IsAsciiLetterLower))
        .Select(c => c.Name)
        .Distinct()
        .OrderBy(NameOf, StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace))
        .ToList();

    /// <summary>A language's name in that language, as people look for their own.</summary>
    public static string NameOf(string code)
    {
        var culture = CultureInfo.GetCultureInfo(code);
        var name = culture.NativeName;
        return name.Length == 0 ? code : culture.TextInfo.ToUpper(name[0]) + name[1..];
    }
}
