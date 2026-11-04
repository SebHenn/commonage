using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace Commonage.Web.Forms;

/// <summary>
/// Text a group wrote for its pages: plain text with its lines kept, where web and email addresses become links. There is
/// no HTML or Markdown to make safe, because everything else is escaped.
/// </summary>
public static partial class PlainText
{
    public static IHtmlContent WithLinks(string text)
    {
        var content = new HtmlContentBuilder();
        var position = 0;
        foreach (Match match in Address().Matches(text))
        {
            // A sentence's closing punctuation is not part of the address.
            var address = match.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')');
            content.Append(text[position..match.Index]);

            var link = new TagBuilder("a");
            link.Attributes["href"] = address.Contains('@') && !address.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? $"mailto:{address}" : address;
            link.Attributes["rel"] = "noopener nofollow";
            link.InnerHtml.Append(address);
            content.AppendHtml(link);

            position = match.Index + address.Length;
        }
        content.Append(text[position..]);
        return content;
    }

    [GeneratedRegex(@"https?://[^\s<>""']+|[\p{L}\p{N}._%+-]+@[\p{L}\p{N}-]+(\.[\p{L}\p{N}-]+)+", RegexOptions.IgnoreCase)]
    private static partial Regex Address();
}
