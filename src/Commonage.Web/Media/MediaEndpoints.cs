using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Net.Http.Headers;

namespace Commonage.Web.Media;

/// <summary>
/// Every file goes through these endpoints; the storage directory is never exposed as static files.
/// Visitors get previews of published items only. Curators get everything, including originals.
/// </summary>
public static class MediaEndpoints
{
    private const int MaxStatusQuestions = 200;
    private const string VariantPattern = "{variant:regex(^(preview|thumbnail|card)$)}";

    public static void MapMediaEndpoints(this WebApplication app)
    {
        app.MapGet($"/media/{{id:int}}/{VariantPattern}", ServePublicDerivativeAsync);

        var curator = app.MapGroup("/admin/media").RequireAuthorization(Policies.ManageArchive);
        curator.MapGet($"/{{id:int}}/{VariantPattern}", ServeDerivativeAsync);
        curator.MapGet("/{id:int}/original", ServeOriginalAsync);
        curator.MapGet("/status", PreviewStatusAsync);
    }

    /// <summary>Which previews are done, so a page can show them without a reload, and how many the archive still has to make.</summary>
    private static async Task<IResult> PreviewStatusAsync(string? ids, CommonageDbContext db, IStringLocalizer<SharedResource> localizer)
    {
        var wanted = (ids ?? "").Split(',').Take(MaxStatusQuestions).Select(id => int.TryParse(id, out var number) ? number : 0).Where(id => id > 0).ToList();
        var files = await db.MediaFiles
            .Where(f => wanted.Contains(f.Id))
            .Select(f => new { f.Id, f.Status })
            .ToListAsync();
        var waiting = await db.MediaFiles.CountAsync(f => f.Status == ProcessingStatus.Pending);
        return Results.Json(new
        {
            ready = files.Where(f => f.Status == ProcessingStatus.Ready).Select(f => f.Id),
            failed = files.Where(f => f.Status == ProcessingStatus.Failed).Select(f => f.Id),
            waiting = files.Where(f => f.Status == ProcessingStatus.Pending).Select(f => f.Id),
            message = waiting == 0 ? "" : localizer["Previews still to be created: {0}", waiting].Value,
        });
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
