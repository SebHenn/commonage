namespace Commonage.Web.Media;

/// <summary>
/// Recognises the image formats Commonage accepts by their first bytes ("magic numbers").
/// The file name and the browser's content type are never trusted.
/// </summary>
public static class ImageFormats
{
    public const int HeaderLength = 12;

    public const string AcceptAttribute = "image/jpeg,image/png,image/tiff,image/webp";

    public static string? DetectContentType(ReadOnlySpan<byte> header)
    {
        if (header.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
            return "image/jpeg";
        if (header.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]))
            return "image/png";
        if (header.StartsWith("II*\0"u8) || header.StartsWith("MM\0*"u8))
            return "image/tiff";
        if (header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8))
            return "image/webp";
        return null;
    }

    /// <summary>
    /// HEIC, the format of many phone photos. It is not accepted, because the bundled image library cannot be relied on to
    /// read it, but it is common enough that the person sending it deserves more than "not a supported format".
    /// </summary>
    public static bool IsHeic(ReadOnlySpan<byte> header) =>
        header.Length >= 12
        && header[4..8].SequenceEqual("ftyp"u8)
        && (header[8..12].SequenceEqual("heic"u8) || header[8..12].SequenceEqual("heix"u8)
            || header[8..12].SequenceEqual("hevc"u8) || header[8..12].SequenceEqual("hevx"u8)
            || header[8..12].SequenceEqual("mif1"u8) || header[8..12].SequenceEqual("msf1"u8));
}
