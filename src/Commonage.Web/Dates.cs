using System.Globalization;
using System.Text.RegularExpressions;

namespace Commonage.Web;

/// <summary>
/// Dates and times as curators read them. Month names are written out in the reader's language, because a numeric date
/// such as 11/4/2026 means different days to different readers.
/// </summary>
public static partial class Dates
{
    /// <summary>The archive's time zone for this request, or null for the server's clock.</summary>
    public static AsyncLocal<TimeZoneInfo?> Zone { get; } = new();

    /// <summary>The day it is now in the archive's time zone.</summary>
    public static DateOnly Today(DateTimeOffset utcNow) => DateOnly.FromDateTime(Local(utcNow.UtcDateTime));

    /// <summary>The moment a day starts in the archive's time zone.</summary>
    public static DateTime StartOfDayUtc(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        if (Zone.Value is not { } zone)
            return local.ToUniversalTime();
        // A day that starts in the gap of a clock change starts when the gap ends.
        if (zone.IsInvalidTime(local))
            local = local.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    public static string Day(DateTime utc) => Local(utc).ToString(DayPattern(), CultureInfo.CurrentCulture);

    public static string Moment(DateTime utc) => $"{Day(utc)} {Local(utc).ToString("t", CultureInfo.CurrentCulture)}";

    private static DateTime Local(DateTime utc) =>
        Zone.Value is { } zone ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone) : utc.ToLocalTime();

    /// <summary>The culture's own long date, in its own word order, without the name of the weekday.</summary>
    private static string DayPattern() =>
        Weekday().Replace(CultureInfo.CurrentCulture.DateTimeFormat.LongDatePattern, "").Trim();

    [GeneratedRegex(@"dddd[,،\s]*")]
    private static partial Regex Weekday();
}
