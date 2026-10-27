using System.Text.RegularExpressions;

namespace Commonage.Tests;

/// <summary>Submits forms the way a browser does, including the antiforgery token from the page.</summary>
public static partial class Browser
{
    public static async Task LogInAsCuratorAsync(HttpClient client) =>
        await PostFormAsync(client, "/Account/Login", new()
        {
            ["Input.Email"] = CommonageAppFactory.AdminEmail,
            ["Input.Password"] = CommonageAppFactory.AdminPassword,
        });

    public static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string url, Dictionary<string, string> fields)
    {
        fields["__RequestVerificationToken"] = await GetAntiforgeryTokenAsync(client, url);
        var response = await client.PostAsync(url, new FormUrlEncodedContent(fields));
        response.EnsureSuccessStatusCode();
        return response;
    }

    /// <summary>Uploads a file through the upload form on <paramref name="pageUrl"/>.</summary>
    public static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string pageUrl, string handler, string fileName, byte[] content)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(await GetAntiforgeryTokenAsync(client, pageUrl)), "__RequestVerificationToken" },
            { new ByteArrayContent(content), "files", fileName },
        };
        var response = await client.PostAsync($"{pageUrl}?handler={handler}", form);
        response.EnsureSuccessStatusCode();
        return response;
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, string url)
    {
        var page = await client.GetStringAsync(url);
        return AntiforgeryToken().Match(page).Groups[1].Value;
    }

    [GeneratedRegex(@"name=""__RequestVerificationToken"" type=""hidden"" value=""([^""]+)""")]
    private static partial Regex AntiforgeryToken();
}
