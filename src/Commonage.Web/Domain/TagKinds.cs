using Microsoft.Extensions.Localization;

namespace Commonage.Web.Domain;

/// <summary>What each kind of tag is called, and where visitors find all tags of a kind.</summary>
public static class TagKinds
{
    /// <summary>
    /// The kinds a curator can give a tag. People are not tags any more (M30): the value stays in <see cref="TagKind"/> because it
    /// is stored and older exports name it, but nothing makes a tag of that kind.
    /// </summary>
    public static IReadOnlyList<TagKind> Choosable { get; } = [TagKind.Topic, TagKind.Place];

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

    /// <summary>
    /// The address of the visitors' page with every tag of this kind. Only places have one: topics are found from the start page and
    /// the search, and people have pages of their own (<c>/people</c>) that do not list tags.
    /// </summary>
    public static string? Slug(TagKind kind) => kind == TagKind.Place ? "places" : null;
}
