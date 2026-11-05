using Commonage.Web.Media;
using Microsoft.Extensions.Options;
using NetVips;

namespace Commonage.Web.Operations;

/// <summary>Makes the logo's sizes from what an admin uploads: PNG files without any of the original's metadata.</summary>
public class LogoImages(IFileStorage storage, IOptions<UploadOptions> options)
{
    public const long MaxBytes = 5 * 1024 * 1024;

    private static readonly (string Variant, int Size)[] Variants = [("logo", 256), ("icon", 180), ("favicon", 32)];

    public static string KeyFor(Guid id, string variant) => $"branding/{id:N}-{variant}.png";

    public static bool IsVariant(string variant) => Variants.Any(v => v.Variant == variant);

    /// <summary>Stores the logo under a new id, or returns null if the file is not an image that can be read.</summary>
    public async Task<Guid?> SaveAsync(IFormFile upload, CancellationToken cancellationToken)
    {
        if (upload.Length is 0 or > MaxBytes)
            return null;

        await using var content = upload.OpenReadStream();
        var header = new byte[ImageFormats.HeaderLength];
        var read = await content.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
        if (ImageFormats.DetectContentType(header.AsSpan(0, read)) is null)
            return null;

        var id = Guid.NewGuid();
        try
        {
            // A small file can claim to be a huge image; the header is enough to find out, without decoding the pixels.
            content.Position = 0;
            using (var picture = Image.NewFromStream(content, access: Enums.Access.Sequential))
            {
                if ((long)picture.Width * picture.Height > options.Value.MaxPreviewMegapixels * 1_000_000L)
                    return null;
            }

            foreach (var (variant, size) in Variants)
            {
                content.Position = 0;
                using var image = Image.ThumbnailStream(content, size, height: size, size: Enums.Size.Down);
                using var output = new MemoryStream();
                image.PngsaveStream(output, keep: Enums.ForeignKeep.None);
                output.Position = 0;
                await storage.SaveAsync(KeyFor(id, variant), output, cancellationToken);
            }
        }
        catch (VipsException)
        {
            Delete(id);
            return null;
        }
        return id;
    }

    public void Delete(Guid id)
    {
        foreach (var (variant, _) in Variants)
            storage.Delete(KeyFor(id, variant));
    }
}

/// <summary>What browsers and search engines fetch about the archive itself.</summary>
public static class BrandingEndpoints
{
    public const string StylesheetPath = "/branding.css";

    public static void MapBrandingEndpoints(this WebApplication app)
    {
        app.MapGet(StylesheetPath, async (ArchiveProfile profile, HttpContext context) =>
        {
            var identity = await profile.GetAsync();
            // The address carries a version of the colours, so it can be kept for long.
            context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            return Results.Text(identity.AccentColor is { } accent ? AccentColor.Stylesheet(accent) : "", "text/css");
        });

        app.MapGet("/branding/{id:guid}/{variant}", async (Guid id, string variant, ArchiveProfile profile, IFileStorage storage, HttpContext context) =>
        {
            if ((await profile.GetAsync()).LogoId != id || !LogoImages.IsVariant(variant))
                return Results.NotFound();

            context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            return Results.Stream(storage.OpenRead(LogoImages.KeyFor(id, variant)), "image/png");
        });

        app.MapGet("/robots.txt", async (ArchiveProfile profile) =>
            Results.Text((await profile.GetAsync()).AllowSearchEngines
                ? "User-agent: *\nDisallow: /Admin\nDisallow: /Account\nDisallow: /contribute\n"
                : "User-agent: *\nDisallow: /\n", "text/plain"));
    }
}
