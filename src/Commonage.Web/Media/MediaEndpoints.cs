using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Identity;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Media;

/// <summary>
/// Every file goes through these endpoints; the storage directory is never exposed as static files.
/// Visitors get previews of published items only. Curators get everything, including originals.
/// </summary>
public static class MediaEndpoints
{
    private const string VariantPattern = "{variant:regex(^(preview|thumbnail)$)}";

    public static void MapMediaEndpoints(this WebApplication app)
    {
        app.MapGet($"/media/{{id:int}}/{VariantPattern}", ServePublicDerivativeAsync);

        var curator = app.MapGroup("/admin/media").RequireAuthorization(Policies.ManageArchive);
        curator.MapGet($"/{{id:int}}/{VariantPattern}", ServeDerivativeAsync);
        curator.MapGet("/{id:int}/original", ServeOriginalAsync);
    }

    private static async Task<IResult> ServePublicDerivativeAsync(
        int id, string variant, CommonageDbContext db, IFileStorage storage, HttpContext context)
    {
        var file = await db.PublishedItems
            .SelectMany(i => i.MediaFiles)
            .FirstOrDefaultAsync(f => f.Id == id && f.Status == ProcessingStatus.Ready);
        if (file is null)
            return Results.NotFound();

        // Short lifetime so an unpublished item disappears from browser caches soon.
        context.Response.Headers.CacheControl = "public, max-age=600";
        return Results.Stream(storage.OpenRead(KeyFor(file, variant)), "image/jpeg");
    }

    private static async Task<IResult> ServeDerivativeAsync(
        int id, string variant, CommonageDbContext db, IFileStorage storage, HttpContext context)
    {
        var file = await db.MediaFiles.FirstOrDefaultAsync(f => f.Id == id && f.Status == ProcessingStatus.Ready);
        if (file is null)
            return Results.NotFound();

        context.Response.Headers.CacheControl = "private, no-store";
        return Results.Stream(storage.OpenRead(KeyFor(file, variant)), "image/jpeg");
    }

    private static async Task<IResult> ServeOriginalAsync(int id, CommonageDbContext db, IFileStorage storage, HttpContext context)
    {
        var file = await db.MediaFiles.FindAsync(id);
        if (file is null)
            return Results.NotFound();

        context.Response.Headers.CacheControl = "private, no-store";
        return Results.Stream(storage.OpenRead(file.OriginalKey), file.ContentType, fileDownloadName: file.OriginalFileName);
    }

    private static string KeyFor(MediaFile file, string variant) =>
        variant == "thumbnail" ? file.ThumbnailKey : file.PreviewKey;
}
