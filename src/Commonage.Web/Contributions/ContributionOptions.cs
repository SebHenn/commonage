namespace Commonage.Web.Contributions;

/// <summary>Spam protection for contribution links. Configured in the "Contributions" section.</summary>
public class ContributionOptions
{
    public const string Section = "Contributions";

    /// <summary>A form sent back sooner than this after it was shown was filled in by a program, not a person.</summary>
    public int MinimumFillSeconds { get; set; } = 3;

    /// <summary>
    /// What one contribution link can take in within a day. Enough for a busy community event, but an open link cannot
    /// fill the disk overnight.
    /// </summary>
    public int MaxUploadMegabytesPerLinkPerDay { get; set; } = 5000;

    /// <summary>
    /// Submissions per IP address and hour. Generous, because everyone at a community event may share one
    /// address. Behind a reverse proxy, forwarded headers must be configured or all visitors share the proxy's.
    /// </summary>
    public int MaxSubmissionsPerHour { get; set; } = 30;
}
