using System.Globalization;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Commonage.Web.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Commonage.Web.Contributions;

/// <summary>
/// Measures how long a form was open. The form carries the time it was shown, encrypted so it cannot be forged.
/// Programs that submit forms usually do so within a second; people need longer to fill in a title and consent.
/// </summary>
public class FormTimer(IDataProtectionProvider dataProtection, IOptions<ContributionOptions> options)
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("Commonage.Contributions.FormTimer");

    public string Start() =>
        _protector.Protect(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));

    public bool WasFilledInByAPerson(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return false;
        try
        {
            var shownAt = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(_protector.Unprotect(token), CultureInfo.InvariantCulture));
            return DateTimeOffset.UtcNow - shownAt >= TimeSpan.FromSeconds(options.Value.MinimumFillSeconds);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return false;
        }
    }
}

public static class SpamProtection
{
    /// <summary>Limits how often one IP address can send in contributions and try to log in. Only those POSTs count.</summary>
    public static IServiceCollection AddRateLimits(this IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                if (!HttpMethods.IsPost(context.Request.Method))
                    return RateLimitPartition.GetNoLimiter("not limited");

                if (context.Request.Path.StartsWithSegments("/contribute"))
                {
                    var limit = context.RequestServices.GetRequiredService<IOptions<ContributionOptions>>().Value.MaxSubmissionsPerHour;
                    return RateLimitPartition.GetFixedWindowLimiter(
                        "contribute " + context.Connection.RemoteIpAddress,
                        _ => new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = TimeSpan.FromHours(1) });
                }

                if (context.Request.Path.StartsWithSegments("/Account/Login"))
                {
                    var limit = context.RequestServices.GetRequiredService<IOptions<LoginOptions>>().Value.MaxAttemptsPerHour;
                    return RateLimitPartition.GetFixedWindowLimiter(
                        "login " + context.Connection.RemoteIpAddress,
                        _ => new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = TimeSpan.FromHours(1) });
                }

                return RateLimitPartition.GetNoLimiter("not limited");
            });
            options.OnRejected = async (rejected, cancellationToken) =>
            {
                // A refused login gets the normal error page, because the response stays empty.
                if (!rejected.HttpContext.Request.Path.StartsWithSegments("/contribute"))
                    return;

                var localizer = rejected.HttpContext.RequestServices.GetRequiredService<IStringLocalizer<SharedResource>>();
                rejected.HttpContext.Response.ContentType = "text/plain; charset=utf-8";
                await rejected.HttpContext.Response.WriteAsync(
                    localizer["Many contributions were sent from your network in the last hour. Please try again later."], cancellationToken);
            };
        });
}
