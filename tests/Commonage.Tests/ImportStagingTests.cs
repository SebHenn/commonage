using Commonage.Web.Import;

namespace Commonage.Tests;

/// <summary>A test about Unix permissions: skipped on Windows, which has no such mode bits (files there get their folder's access list).</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
            Skip = "Unix file modes do not exist on Windows.";
    }
}

public sealed class ImportStagingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "staging-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }

    [UnixFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void What_waits_for_the_import_can_only_be_read_by_the_account_running_the_app()
    {
        var staging = new ImportStaging(_root);
        var id = staging.Create();
        using (var file = ImportStaging.CreateOwnerOnlyFile(staging.ExportPath(id)))
            file.WriteByte(1);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(staging.DirectoryOf(id)));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(staging.ExportPath(id)));
    }

    [Fact]
    public void A_file_made_owner_only_is_still_written_and_replaced_on_every_system()
    {
        var staging = new ImportStaging(_root);
        var id = staging.Create();
        for (var round = 1; round <= 2; round++)
        {
            using var file = ImportStaging.CreateOwnerOnlyFile(staging.ExportPath(id));
            file.WriteByte((byte)round);
        }

        Assert.Equal([(byte)2], File.ReadAllBytes(staging.ExportPath(id)));
    }

    [Fact]
    public void A_folder_that_cannot_be_removed_stays_and_the_other_old_ones_are_still_cleared()
    {
        var staging = new ImportStaging(_root);
        var stuck = staging.Create();
        var other = staging.Create();
        // A read-only file stops Windows from deleting its folder, which is an UnauthorizedAccessException, not an IOException.
        var readOnly = Path.Combine(staging.DirectoryOf(stuck), "sheet.csv");
        File.WriteAllText(readOnly, "a");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        File.WriteAllText(staging.CsvPath(other), "b");

        var exception = Record.Exception(() => staging.DeleteOld(TimeSpan.FromDays(1), DateTime.UtcNow.AddDays(3)));

        Assert.Null(exception);
        Assert.False(Directory.Exists(staging.DirectoryOf(other)));
    }
}
