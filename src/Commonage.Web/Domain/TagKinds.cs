using Microsoft.Extensions.Localization;

namespace Commonage.Web.Domain;

/// <summary>What each kind of tag is called, and where visitors find all tags of a kind.</summary>
public static class TagKinds
{
    public static string Name(IStringLocalizer localizer, TagKind kind) => kind switch
    {
        TagKind.Place => localizer["Place"],
        TagKind.Person => localizer["Person"],
        _ => localizer["Topic"],
    };

    public static string Plural(IStringLocalizer localizer, TagKind kind) => kind switch
    {
        TagKind.Place => localizer["Places"],
        TagKind.Person => localizer["People"],
        _ => localizer["Topics"],
    };

    /// <summary>The address of the visitors' page with every tag of this kind. Topics have none: the start page and the search show them.</summary>
    public static string? Slug(TagKind kind) => kind switch
    {
        TagKind.Place => "places",
        TagKind.Person => "people",
        _ => null,
    };

    public static TagKind? FromSlug(string? slug) => slug?.ToLowerInvariant() switch
    {
        "places" => TagKind.Place,
        "people" => TagKind.Person,
        _ => null,
    };
}
