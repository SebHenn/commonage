using Microsoft.Extensions.Localization;

namespace Commonage.Web;

/// <summary>How large a file or a disk is, in the reader's language: "1.2 GB", or "1,2 Go" in French.</summary>
public static class Sizes
{
    private const double Kilobyte = 1024, Megabyte = Kilobyte * 1024, Gigabyte = Megabyte * 1024;

    public static string Of(IStringLocalizer localizer, long bytes) => bytes switch
    {
        >= (long)Gigabyte => localizer["{0} GB", (bytes / Gigabyte).ToString(bytes >= 100 * Gigabyte ? "0" : "0.0")],
        >= (long)Megabyte => localizer["{0} MB", Math.Round(bytes / Megabyte)],
        _ => localizer["{0} KB", Math.Ceiling(bytes / Kilobyte)],
    };
}
