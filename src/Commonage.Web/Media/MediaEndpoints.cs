using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Identity;
using Commonage.Web.Operations;
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
    private const string VariantPattern = "{variant:regex(^(preview|thumbnail|card|large)$)}";

    public static void MapMediaEndpoints(this WebApplication app)
    {
        app.MapGet($"/media/{{id:int}}/{VariantPattern}", ServePublicDerivativeAsync);
        app.MapGet("/media/{id:int}/page/{page:int}", ServePublicPageAsync);

        // Pictures made for the screen and the state of their making are for everyone who may describe items. The original, which
        // may hold what the pictures do not, is for curators.
        var describers = app.MapGroup("/admin/media").RequireAuthorization(Policies.Describe);
        describers.MapGet($"/{{id:int}}/{VariantPattern}", ServeDerivativeAsync);
        describers.MapGet("/{id:int}/page/{page:int}", ServePageAsync);
        describers.MapGet("/status", PreviewStatusAsync);
        app.MapGroup("/admin/media").RequireAuthorization(Policies.ManageArchive).MapGet("/{id:int}/original", ServeOriginalAsync);
    }

    /// <summary>Which previews are done, so a page can show them without a reload, and how many the archive still has to make.</summary>
    private static async Task<IResult> PreviewStatusAsync(string? ids, CommonageDbContext db, IStringLocalizer<SharedResource> localizer)
    {
        var wanted = (ids ?? "").Split(',').Take(MaxStatusQuestions).Select(id => int.TryParse(id, out var number) ? number : 0).Where(id => id > 0).ToList();
        var files = await db.MediaFiles
            .Where(f => wanted.Contains(f.Id))
            .Select(f => new { f.Id, f.Status, Turning = f.Rotation != f.RenderedRotation })
            .ToListAsync();
        var waiting = await db.MediaFiles.CountAsync(f => f.Status == ProcessingStatus.Pending || (f.Status == ProcessingStatus.Ready && f.Rotation != f.RenderedRotation));
        return Results.Json(new
        {
            ready = files.Where(f => f.Status == ProcessingStatus.Ready && !f.Turning).Select(f => f.Id),
            failed = files.Where(f => f.Status == ProcessingStatus.Failed).Select(f => f.Id),
            waiting = files.Where(f => f.Status == ProcessingStatus.Pending || (f.Status == ProcessingStatus.Ready && f.Turning)).Select(f => f.Id),
            message = waiting == 0 ? "" : localizer["Previews still to be created: {0}", waiting].Value,
        });
    }

    private static async Task<IResult> ServePublicDerivativeAsync(
        int id, string variant, CommonageDbContext db, IFileStorage storage, ArchiveProfile profile, HttpContext context)
    {
        var file = await db.PublishedItems
            .SelectMany(i => i.MediaFiles)
            .FirstOrDefaultAsync(f => f.Id == id && f.Status == ProcessingStatus.Ready);
        if (file is null)
            return Results.NotFound();
        if (variant == "large" && !(file.HasLarge && (await profile.GetAsync()).LargePhotos))
            return Results.NotFound();

        // Short lifetime so an unpublished item disappears from browser caches soon. After that, browsers ask again and
        // get "not changed" without the picture being sent, but only while the item is still public, as checked above.
        context.Response.Headers.CacheControl = "public, max-age=600";
        return Results.Stream(storage.OpenRead(KeyFor(file, variant)), "image/jpeg",
            lastModified: file.UploadedAt,
            entityTag: new EntityTagHeaderValue($"\"{file.StorageId:N}-{variant}-{file.DerivativeVersion}-{file.RenderedRotation}\""));
    }

    /// <summary>One page of a published PDF, drawn as a picture. The PDF itself is never served to visitors.</summary>
    private static async Task<IResult> ServePublicPageAsync(int id, int page, CommonageDbContext db, IFileStorage storage, HttpContext context)
    {
        var file = await db.PublishedItems
            .SelectMany(i => i.MediaFiles)
            .FirstOrDefaultAsync(f => f.Id == id && f.Status == ProcessingStatus.Ready);
        if (file is null || page < 1 || page > (file.PageCount ?? 0))
            return Results.NotFound();

        context.Response.Headers.CacheControl = "public, max-age=600";
        return Results.Stream(storage.OpenRead(file.PageKey(page)), "image/jpeg",
            lastModified: file.UploadedAt,
            entityTag: new EntityTagHeaderValue($"\"{file.StorageId:N}-page{page}\""));
    }

    /// <summary>
    /// A file that is ready. Curators also see what is in the bin, to decide whether to restore it. A helper only sees the files of
    /// drafts and published items that are not in the bin: contributions nobody has reviewed yet are not theirs to look at.
    /// </summary>
    private static async Task<MediaFile?> FindForStaffAsync(CommonageDbContext db, int id, System.Security.Claims.ClaimsPrincipal user)
    {
        var files = db.MediaFiles.IgnoreQueryFilters().Where(f => f.Id == id && f.Status == ProcessingStatus.Ready);
        if (!user.MayManage())
            files = files.Where(f => f.DeletedAt == null && f.ArchiveItem.DeletedAt == null
                && f.ArchiveItem.Status == ItemStatus.Draft);
        return await files.FirstOrDefaultAsync();
    }

    private static async Task<IResult> ServePageAsync(int id, int page, CommonageDbContext db, IFileStorage storage, HttpContext context)
    {
        var file = await FindForStaffAsync(db, id, context.User);
        if (file is null || page < 1 || page > (file.PageCount ?? 0))
            return Results.NotFound();

        context.Response.Headers.CacheControl = "private, no-store";
        return Results.Stream(storage.OpenRead(file.PageKey(page)), "image/jpeg");
    }

    private static async Task<IResult> ServeDerivativeAsync(
        int id, string variant, CommonageDbContext db, IFileStorage storage, HttpContext context)
    {
        // Curators also see what is in the bin, to decide whether to restore it.
        var file = await FindForStaffAsync(db, id, context.User);
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
        // A curator looking at what visitors will see, of a file that has no larger size.
        "large" => file.HasLarge ? file.LargeKey : file.PreviewKey,
        _ => file.PreviewKey,
    };
}
