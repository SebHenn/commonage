namespace Commonage.Web.Search;

/// <summary>Patterns for ILIKE that match typed text literally, so % and _ are not wildcards.</summary>
public static class LikePattern
{
    public static string Containing(string text) => "%" + text.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";
}
