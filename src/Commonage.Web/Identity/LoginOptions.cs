namespace Commonage.Web.Identity;

public class LoginOptions
{
    public const string Section = "Login";

    /// <summary>
    /// Login attempts per IP address and hour, on top of the lockout of a single account. Generous, because a group
    /// may share one internet connection, but it stops someone trying passwords across many accounts.
    /// </summary>
    public int MaxAttemptsPerHour { get; set; } = 100;
}
