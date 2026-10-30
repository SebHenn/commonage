using Microsoft.AspNetCore.WebUtilities;

namespace Commonage.Web.Search;

public static class QueryUrls
{
    /// <summary>Query key for the page number. "page" itself is reserved by Razor Pages for routing.</summary>
    public const string PageKey = "p";

    /// <summary>
    /// The current URL with one query value replaced, or removed when <paramref name="value"/> is null.
    /// The page number is reset, because a changed filter starts again on the first page.
    /// </summary>
    public static string WithQueryValue(this HttpRequest request, string key, string? value)
    {
        var query = QueryHelpers.ParseQuery(request.QueryString.Value);
        query.Remove(PageKey);
        query.Remove(key);
        if (value is not null)
            query[key] = value;
        return QueryHelpers.AddQueryString(request.PathBase + request.Path, query);
    }
}
