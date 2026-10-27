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
}
