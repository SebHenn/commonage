using Microsoft.Extensions.Localization;

namespace Commonage.Web.Domain;

/// <summary>
/// What may be done with an item, as standard statements other archives and search services understand, each with a plain
/// label. Curators choose from these. Nothing here is ever assumed to be an open licence.
/// </summary>
public static class RightsOptions
{
    public const string InCopyright = "http://rightsstatements.org/vocab/InC/1.0/";
    public const string InCopyrightHolderUnknown = "http://rightsstatements.org/vocab/InC-RUU/1.0/";
    public const string InCopyrightNonCommercial = "http://rightsstatements.org/vocab/InC-NC/1.0/";
    public const string NotEvaluated = "http://rightsstatements.org/vocab/CNE/1.0/";
    public const string Undetermined = "http://rightsstatements.org/vocab/UND/1.0/";
    public const string PublicDomain = "https://creativecommons.org/publicdomain/mark/1.0/";
    public const string Cc0 = "https://creativecommons.org/publicdomain/zero/1.0/";
    public const string CcBy = "https://creativecommons.org/licenses/by/4.0/";
    public const string CcBySa = "https://creativecommons.org/licenses/by-sa/4.0/";
    public const string CcByNc = "https://creativecommons.org/licenses/by-nc/4.0/";

    public const int MaxUriLength = 200;

    public static readonly IReadOnlyList<string> All =
    [
        InCopyright, InCopyrightHolderUnknown, InCopyrightNonCommercial, NotEvaluated, Undetermined,
        PublicDomain, Cc0, CcBy, CcBySa, CcByNc,
    ];

    public static bool IsKnown(string uri) => All.Contains(uri);

    public static string Label(IStringLocalizer localizer, string uri) => uri switch
    {
        InCopyright => localizer["In copyright: ask before reusing"],
        InCopyrightHolderUnknown => localizer["In copyright, but we do not know who holds it"],
        InCopyrightNonCommercial => localizer["In copyright: free for non-commercial use"],
        NotEvaluated => localizer["Not checked yet: ask before reusing"],
        Undetermined => localizer["We checked, but could not find out"],
        PublicDomain => localizer["Public domain: free to use"],
        Cc0 => localizer["Given to the public domain (CC0): free to use"],
        CcBy => localizer["Free to use with credit (CC BY)"],
        CcBySa => localizer["Free to use with credit, and shared alike (CC BY-SA)"],
        CcByNc => localizer["Free for non-commercial use with credit (CC BY-NC)"],
        _ => uri,
    };

    /// <summary>
    /// A first suggestion from what a contributor answered. Even someone who made a photo has not given it away by sending
    /// it in, so the suggestion is never an open licence: the contributor agreed to publication on this website, no more.
    /// </summary>
    public static string ProposalFor(RightsStatement answer) => answer switch
    {
        RightsStatement.Unknown => NotEvaluated,
        _ => InCopyright,
    };
}
