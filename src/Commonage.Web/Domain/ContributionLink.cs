using System.Buffers.Text;
using System.Security.Cryptography;

namespace Commonage.Web.Domain;

/// <summary>
/// An invitation to contribute, such as "Share your photos of the Mill Street flood". Anyone with its address can
/// submit material to <see cref="Collection"/>. The address contains a random token, so the form cannot be found
/// without the link. Curators switch a link off instead of deleting it, so contributions keep their origin.
/// </summary>
public class ContributionLink
{
    public const int TitleMaxLength = 200;
    public const int IntroductionMaxLength = 4000;

    public int Id { get; set; }
    public required string Title { get; set; }
    public string? Introduction { get; set; }

    public int CollectionId { get; set; }
    public Collection Collection { get; set; } = null!;

    /// <summary>128 random bits, URL-safe. Part of the link's address.</summary>
    public required string Token { get; init; }

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// A curator chose to invite visitors to this link from the collection's public page. Off by default, because a link
    /// is otherwise only known to the people it was given to, and showing it gives that up.
    /// </summary>
    public bool InviteOnCollectionPage { get; set; }
    public DateTime CreatedAt { get; set; }

    public List<Contribution> Contributions { get; set; } = [];

    public static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
}
