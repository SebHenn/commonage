using Commonage.Web.Import;

namespace Commonage.Tests;

public sealed class ImportStagingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "staging-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
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
