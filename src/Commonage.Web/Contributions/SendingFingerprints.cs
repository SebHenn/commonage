using System.Security.Cryptography;
using System.Text.Json;

namespace Commonage.Web.Contributions;

/// <summary>
/// Tells the same form sent again from a new contribution. A form carries a random key from when it was shown. On a phone,
/// a connection can drop after everything arrived but before the answer did, and the person sensibly tries again.
/// What they sent is part of the fingerprint too, so going back to change something and sending that does count as new.
/// </summary>
public static class SendingFingerprints
{
    /// <summary>Null without a key, for a form that was open while the archive was updated: that one is simply not recognised.</summary>
    /// <remarks>Name and email address are left out on purpose, so that nothing about the contributor stays in here when their details are removed.</remarks>
    public static string? Of(string? sendingKey, int linkId, IEnumerable<string?> texts, IEnumerable<IFormFile> files)
    {
        if (string.IsNullOrEmpty(sendingKey))
            return null;

        var everything = JsonSerializer.SerializeToUtf8Bytes(new
        {
            sendingKey,
            linkId,
            texts,
            files = files.Select(file => new { file.FileName, file.Length }),
        });
        return Convert.ToHexStringLower(SHA256.HashData(everything));
    }
}
