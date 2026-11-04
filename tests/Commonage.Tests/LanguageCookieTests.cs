using Microsoft.AspNetCore.Mvc.Testing;

namespace Commonage.Tests;

[Collection(nameof(AppCollection))]
public class LanguageCookieTests(CommonageAppFactory app)
{
    private async Task<string> SetCookieAfterChoosingLanguageAsync(string baseAddress)
    {
        using var visitor = app.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri(baseAddress),
            AllowAutoRedirect = false,
        });

        var response = await Browser.SendFormAsync(visitor, "/SetLanguage", new()
        {
            ["culture"] = "de",
            ["returnUrl"] = "/",
        }, pageUrl: "/");

        return response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(".AspNetCore.Culture="));
    }

    [Fact]
    public async Task Over_https_the_language_cookie_is_only_sent_over_https()
    {
        var cookie = await SetCookieAfterChoosingLanguageAsync("https://localhost");

        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Over_plain_http_on_a_development_machine_the_language_can_still_be_chosen()
    {
        var cookie = await SetCookieAfterChoosingLanguageAsync("http://localhost");

        Assert.DoesNotContain("secure", cookie, StringComparison.OrdinalIgnoreCase);
    }
}
