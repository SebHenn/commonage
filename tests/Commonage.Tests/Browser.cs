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

    /// <param name="pageUrl">The page that shows the form, if it is not <paramref name="url"/> itself.</param>
    public static async Task<HttpResponseMessage> PostFormAsync(
        HttpClient client, string url, Dictionary<string, string> fields, string? pageUrl = null)
    {
        var response = await SendFormAsync(client, url, fields, pageUrl);
        response.EnsureSuccessStatusCode();
        return response;
    }

    /// <summary>Like <see cref="PostFormAsync"/>, but leaves the status code to the test, so it can look at refusals.</summary>
    public static async Task<HttpResponseMessage> SendFormAsync(
        HttpClient client, string url, Dictionary<string, string> fields, string? pageUrl = null)
    {
        fields["__RequestVerificationToken"] = await GetAntiforgeryTokenAsync(client, pageUrl ?? url);
        return await client.PostAsync(url, new FormUrlEncodedContent(fields));
    }

    /// <summary>Whether the client can open the curators' pages, rather than being sent to the login page.</summary>
    public static async Task<bool> IsLoggedInAsync(HttpClient client)
    {
        var response = await client.GetAsync("/Admin");
        return response.IsSuccessStatusCode && response.RequestMessage!.RequestUri!.AbsolutePath == "/Admin";
    }

    public static async Task LogInAsync(HttpClient client, string email, string password) =>
        await PostFormAsync(client, "/Account/Login", new()
        {
            ["Input.Email"] = email,
            ["Input.Password"] = password,
        });

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

    /// <summary>
    /// Fills in the form on <paramref name="url"/> and sends it with files, as a browser does for multipart forms.
    /// Hidden fields come from the page. The status code is not checked, so tests can look at refusals.
    /// </summary>
    public static async Task<HttpResponseMessage> SendFormWithFilesAsync(
        HttpClient client, string url, Dictionary<string, string> fields, params (string FileName, byte[] Content)[] files)
    {
        var page = await client.GetStringAsync(url);
        var form = new MultipartFormDataContent();
        var sent = new HashSet<string>(fields.Keys);
        foreach (Match input in Input().Matches(page))
        {
            // Every form on a page has its own copy of the antiforgery token; send each hidden field once.
            if (Attribute(input.Value, "type") == "hidden" && Attribute(input.Value, "name") is { } name && sent.Add(name))
                form.Add(new StringContent(System.Net.WebUtility.HtmlDecode(Attribute(input.Value, "value") ?? "")), name);
        }
        foreach (var (name, value) in fields)
            form.Add(new StringContent(value), name);
        foreach (var (fileName, content) in files)
            form.Add(new ByteArrayContent(content), "Files", fileName);
        return await client.PostAsync(url, form);
    }

    private static string? Attribute(string tag, string name) =>
        Regex.Match(tag, $@"\s{name}=""([^""]*)""") is { Success: true } match ? match.Groups[1].Value : null;

    /// <summary>The antiforgery token of a page already fetched.</summary>
    public static string Token(string page) => AntiforgeryToken().Match(page).Groups[1].Value;

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, string url)
    {
        var page = await client.GetStringAsync(url);
        return AntiforgeryToken().Match(page).Groups[1].Value;
    }

    [GeneratedRegex(@"<input\s[^>]*>")]
    private static partial Regex Input();

    [GeneratedRegex(@"name=""__RequestVerificationToken"" type=""hidden"" value=""([^""]+)""")]
    private static partial Regex AntiforgeryToken();
}
