using System.Globalization;
using Microsoft.Extensions.Localization;

namespace Commonage.BrowserTests;

/// <summary>
/// A made-up language whose text is much longer than English and has long words, as German and Finnish do. A layout that
/// holds up with it has room for real translations. It keeps placeholders intact, so sentences still work.
/// </summary>
public sealed class LongTextLanguage(IStringLocalizerFactory inner) : IStringLocalizerFactory
{
    // A real language code, which has no translations in Commonage yet, so that pages stay valid.
    public const string Code = "fi";

    public IStringLocalizer Create(Type resourceSource) => new Localizer(inner.Create(resourceSource));

    public IStringLocalizer Create(string baseName, string location) => new Localizer(inner.Create(baseName, location));

    private sealed class Localizer(IStringLocalizer inner) : IStringLocalizer
    {
        public LocalizedString this[string name] => Lengthen(inner[name]);

        public LocalizedString this[string name, params object[] arguments] => Lengthen(inner[name, arguments]);

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) =>
            inner.GetAllStrings(includeParentCultures).Select(Lengthen);

        private static LocalizedString Lengthen(LocalizedString text) =>
            string.Equals(CultureInfo.CurrentUICulture.Name, Code, StringComparison.OrdinalIgnoreCase)
                ? new LocalizedString(text.Name, $"{text.Value}{new string('ü', text.Value.Length / 2)}", text.ResourceNotFound)
                : text;
    }
}
