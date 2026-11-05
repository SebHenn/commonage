using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace Commonage.Web.Media;

/// <summary>
/// Every file goes through these endpoints; the storage directory is never exposed as static files.
/// Visitors get previews of published items only. Curators get everything, including originals.
/// </summary>
public static class MediaEndpoints
{
    private const string VariantPattern = "{variant:regex(^(preview|thumbnail|card)$)}";

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

        // Short lifetime so an unpublished item disappears from browser caches soon. After that, browsers ask again and
        // get "not changed" without the picture being sent, but only while the item is still public, as checked above.
        context.Response.Headers.CacheControl = "public, max-age=600";
        return Results.Stream(storage.OpenRead(KeyFor(file, variant)), "image/jpeg",
            lastModified: file.UploadedAt,
            entityTag: new EntityTagHeaderValue($"\"{file.StorageId:N}-{variant}-{file.DerivativeVersion}\""));
    }

    private static async Task<IResult> ServeDerivativeAsync(
        int id, string variant, CommonageDbContext db, IFileStorage storage, HttpContext context)
    {
        // Curators also see what is in the bin, to decide whether to restore it.
        var file = await db.MediaFiles.IgnoreQueryFilters().FirstOrDefaultAsync(f => f.Id == id && f.Status == ProcessingStatus.Ready);
        if (file is null)
            return Results.NotFound();

        context.Response.Headers.CacheControl = "private, no-store";
        return Results.Stream(storage.OpenRead(KeyFor(file, variant)), "image/jpeg");
    }

    private static async Task<IResult> ServeOriginalAsync(int id, CommonageDbContext db, IFileStorage storage, HttpContext context)
    {
        var file = await db.MediaFiles.IgnoreQueryFilters().FirstOrDefaultAsync(f => f.Id == id);
        if (file is null)
            return Results.NotFound();

        context.Response.Headers.CacheControl = "private, no-store";
        return Results.Stream(storage.OpenRead(file.OriginalKey), file.ContentType, fileDownloadName: file.OriginalFileName);
    }

    private static string KeyFor(MediaFile file, string variant) => variant switch
    {
        "thumbnail" => file.ThumbnailKey,
        // Until the archive has made the larger size for an older file, the smaller one has to do.
        "card" => file.DerivativeVersion >= 2 ? file.CardKey : file.ThumbnailKey,
        _ => file.PreviewKey,
    };
}
