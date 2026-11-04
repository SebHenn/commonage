using System.Globalization;
using System.Text.RegularExpressions;

namespace Commonage.Web;

/// <summary>
/// Dates and times as curators read them. Month names are written out in the reader's language, because a numeric date
/// such as 11/4/2026 means different days to different readers.
/// </summary>
public static partial class Dates
{
    public static string Day(DateTime utc) => utc.ToLocalTime().ToString(DayPattern(), CultureInfo.CurrentCulture);

    public static string Moment(DateTime utc) => $"{Day(utc)} {utc.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)}";

    /// <summary>The culture's own long date, in its own word order, without the name of the weekday.</summary>
    private static string DayPattern() =>
        Weekday().Replace(CultureInfo.CurrentCulture.DateTimeFormat.LongDatePattern, "").Trim();

    [GeneratedRegex(@"dddd[,،\s]*")]
    private static partial Regex Weekday();
}
