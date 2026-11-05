using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;

namespace Commonage.Web.Operations;

/// <summary>What a group has set up about its archive, as every page needs it.</summary>
public sealed class ArchiveIdentity(ArchiveSettings settings, IReadOnlyList<ArchiveText> texts)
{
    public const string SoftwareName = "Commonage";

    public string DefaultLanguage => settings.DefaultLanguage;
    public string? AccentColor => settings.AccentColor;
    public Guid? LogoId => settings.LogoId;
    public string? TimeZoneId => settings.TimeZone;
    public string? DefaultRightsUri => settings.DefaultRightsUri;
    public bool AllowSearchEngines => settings.AllowSearchEngines;
    public bool PrivacyReviewed => settings.PrivacyReviewed;
    public bool ImprintNotNeeded => settings.ImprintNotNeeded;
    public DateTime? RestoreTestedAt => settings.RestoreTestedAt;

    public string Name => Text(ArchiveTextKind.Name) ?? SoftwareName;

    public bool NameIsSet => Text(ArchiveTextKind.Name) is not null;

    /// <summary>The text in the reader's language, or else the archive's own, or else whatever there is.</summary>
    public string? Text(ArchiveTextKind kind)
    {
        var matching = texts.Where(t => t.Kind == kind).ToList();
        var language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return (matching.FirstOrDefault(t => t.Language == language)
            ?? matching.FirstOrDefault(t => t.Language == settings.DefaultLanguage)
            ?? matching.FirstOrDefault())?.Text;
    }

    /// <summary>Changes when the colours change, so browsers fetch the stylesheet again.</summary>
    public string BrandingVersion => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(AccentColor ?? "")))[..12];
}

/// <summary>Keeps the archive's settings at hand, because every page needs them. Saving them clears this.</summary>
public class ArchiveProfile(IServiceScopeFactory scopeFactory, IMemoryCache cache, ILogger<ArchiveProfile> logger)
{
    private const string CacheKey = "archive-identity";

    public async Task<ArchiveIdentity> GetAsync()
    {
        try
        {
            return (await cache.GetOrCreateAsync(CacheKey, async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<CommonageDbContext>();
                var settings = await db.ArchiveSettings.AsNoTracking().FirstOrDefaultAsync() ?? new ArchiveSettings();
                var texts = await db.ArchiveTexts.AsNoTracking().ToListAsync();
                return new ArchiveIdentity(settings, texts);
            }))!;
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            // With the database gone, the health check and the error page must still answer, not fail here as well.
            logger.LogWarning(exception, "Could not read the archive's settings.");
            return new ArchiveIdentity(new ArchiveSettings(), []);
        }
    }

    public void Forget() => cache.Remove(CacheKey);
}

/// <summary>The colour a group chose for its buttons and links. White text on it, and it on white, must stay readable.</summary>
public static class AccentColor
{
    /// <summary>WCAG AA for normal text.</summary>
    public const double MinimumContrast = 4.5;

    public static bool TryParse(string? text, out (int R, int G, int B) color)
    {
        color = default;
        if (text is not { Length: 7 } || text[0] != '#' || !int.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
            return false;
        color = ((value >> 16) & 255, (value >> 8) & 255, value & 255);
        return true;
    }

    public static double ContrastWithWhite((int R, int G, int B) color) => 1.05 / (Luminance(color) + 0.05);

    /// <summary>The nearest darker shade that is readable.</summary>
    public static string SuggestReadable((int R, int G, int B) color)
    {
        while (ContrastWithWhite(color) < MinimumContrast)
            color = Scale(color, 0.95);
        return Hex(color);
    }

    /// <summary>
    /// A stylesheet with the colour. Bootstrap writes its colours into each component, so changing --bs-primary alone
    /// would leave buttons and links as they are. Rules for each of them are needed.
    /// </summary>
    public static string Stylesheet(string accent)
    {
        if (!TryParse(accent, out var color))
            return "";
        var (hover, active) = (Scale(color, 0.85), Scale(color, 0.7));
        var (main, darker, darkest) = (Hex(color), Hex(hover), Hex(active));
        return $$"""
            :root {
              --bs-primary: {{main}};
              --bs-primary-rgb: {{Rgb(color)}};
              --bs-link-color: {{main}};
              --bs-link-color-rgb: {{Rgb(color)}};
              --bs-link-hover-color: {{darker}};
              --bs-link-hover-color-rgb: {{Rgb(hover)}};
              --commonage-focus-color: {{main}};
            }
            .btn-primary {
              --bs-btn-bg: {{main}};
              --bs-btn-border-color: {{main}};
              --bs-btn-hover-bg: {{darker}};
              --bs-btn-hover-border-color: {{darker}};
              --bs-btn-active-bg: {{darkest}};
              --bs-btn-active-border-color: {{darkest}};
              --bs-btn-disabled-bg: {{main}};
              --bs-btn-disabled-border-color: {{main}};
              --bs-btn-focus-shadow-rgb: {{Rgb(color)}};
            }
            .btn-outline-primary {
              --bs-btn-color: {{main}};
              --bs-btn-border-color: {{main}};
              --bs-btn-hover-bg: {{main}};
              --bs-btn-hover-border-color: {{main}};
              --bs-btn-active-bg: {{darker}};
              --bs-btn-active-border-color: {{darker}};
              --bs-btn-disabled-color: {{main}};
              --bs-btn-disabled-border-color: {{main}};
              --bs-btn-focus-shadow-rgb: {{Rgb(color)}};
            }
            .nav {
              --bs-nav-link-color: {{main}};
              --bs-nav-link-hover-color: {{darker}};
            }
            .nav-pills {
              --bs-nav-pills-link-active-bg: {{main}};
            }
            .pagination {
              --bs-pagination-color: {{main}};
              --bs-pagination-hover-color: {{darker}};
              --bs-pagination-focus-color: {{darker}};
              --bs-pagination-active-bg: {{main}};
              --bs-pagination-active-border-color: {{main}};
            }
            .form-check-input:checked {
              background-color: {{main}};
              border-color: {{main}};
            }
            """;
    }

    private static string Hex((int R, int G, int B) c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";

    private static string Rgb((int R, int G, int B) c) => $"{c.R}, {c.G}, {c.B}";

    private static (int R, int G, int B) Scale((int R, int G, int B) c, double factor) =>
        ((int)(c.R * factor), (int)(c.G * factor), (int)(c.B * factor));

    private static double Luminance((int R, int G, int B) c)
    {
        static double Channel(int value)
        {
            var share = value / 255.0;
            return share <= 0.03928 ? share / 12.92 : Math.Pow((share + 0.055) / 1.055, 2.4);
        }
        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
    }
}
