using Commonage.Web.Data;
using Commonage.Web.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Commonage.Web.Operations;

/// <summary>
/// The checks behind /health. It answers only Healthy, Degraded or Unhealthy, so visitors learn nothing else, and admins
/// see each check on the System page. Degraded is still HTTP 200, so an uptime monitor that only looks at the status code
/// does not notice it; docs/operations.md says how to look for the word instead.
/// </summary>
public static class ArchiveHealthChecks
{
    public const string Database = "database";
    public const string Storage = "storage";
    public const string Disk = "disk";
    public const string Backup = "backup";

    public static IHealthChecksBuilder AddArchiveHealthChecks(this IServiceCollection services) =>
        services.AddHealthChecks()
            .AddDbContextCheck<CommonageDbContext>(Database)
            .AddCheck<StorageHealthCheck>(Storage)
            .AddCheck<DiskHealthCheck>(Disk)
            .AddCheck<BackupHealthCheck>(Backup);

    public const string Path = "/health";

    /// <summary>
    /// Anyone can ask, and each look writes a file and asks the database twice. So an answer is kept for a few seconds,
    /// and asking a thousand times a second costs the archive no more than asking once.
    /// </summary>
    public static readonly TimeSpan AnswerKeptFor = TimeSpan.FromSeconds(5);

    public static void MapArchiveHealth(this WebApplication app) =>
        app.MapGet(Path, async (HealthCheckService checks, IMemoryCache cache, HttpContext context) =>
        {
            var status = await cache.GetOrCreateAsync("Commonage.Health", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = AnswerKeptFor;
                // Not the visitor's cancellation: an answer that others wait for must not end with one of them.
                return (await checks.CheckHealthAsync(CancellationToken.None)).Status;
            });
            context.Response.Headers.CacheControl = "no-store";
            return Results.Text(status.ToString(), "text/plain",
                statusCode: status == HealthStatus.Unhealthy ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status200OK);
        });
}

/// <summary>
/// The media folder can be written to, and holds the newest original. An empty folder in its place, such as a volume that is
/// not mounted or a database restored without its files, would otherwise only show when someone opens a photo.
/// </summary>
public class StorageHealthCheck(IFileStorage storage, CommonageDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // A name of its own each time, because two checks at once must not write the same file.
        var probe = $"health-check-{Guid.NewGuid():N}";
        try
        {
            await storage.SaveAsync(probe, new MemoryStream([1]), cancellationToken);
            storage.Delete(probe);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return HealthCheckResult.Unhealthy("The media folder cannot be written to.", exception);
        }

        // An original is stored before its row and deleted after it, so a row whose file is missing means files are lost,
        // unless it was deleted just now, while this looked.
        var newest = await db.MediaFiles.IgnoreQueryFilters().AsNoTracking()
            .OrderByDescending(f => f.Id)
            .Select(f => new { f.Id, f.StorageId })
            .FirstOrDefaultAsync(cancellationToken);
        if (newest is not null
            && !storage.Exists($"originals/{newest.StorageId:N}")
            && await db.MediaFiles.IgnoreQueryFilters().AnyAsync(f => f.Id == newest.Id, cancellationToken))
            return HealthCheckResult.Unhealthy("The newest original is missing from the media folder. Is the media volume mounted?");

        return HealthCheckResult.Healthy();
    }
}

/// <summary>Degraded while the disk is filling up, and once uploads have stopped. The archive itself still works.</summary>
public class DiskHealthCheck(ArchiveHealth health) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(
            !health.CanAcceptUploads() ? HealthCheckResult.Degraded("The disk is nearly full, so uploads have stopped.")
            : health.IsLowOnSpace ? HealthCheckResult.Degraded("The disk is filling up.")
            : HealthCheckResult.Healthy());
}

/// <summary>Degraded when there has been no backup for longer than admins are warned after, or none at all.</summary>
public class BackupHealthCheck(ArchiveHealth health) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(
            health.LastBackupAt is null ? HealthCheckResult.Degraded("There has been no backup.")
            : health.BackupIsOverdue ? HealthCheckResult.Degraded("The last backup is too old.")
            : HealthCheckResult.Healthy());
}
