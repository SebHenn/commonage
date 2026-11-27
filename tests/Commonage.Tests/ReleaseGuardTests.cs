using System.Text.RegularExpressions;

namespace Commonage.Tests;

/// <summary>
/// M30 ships in one release. Until its last part (the person bin and the move of person tags to people) exists, a pull request that
/// dates the top entry of the changelog, which is what a release is, must fail. Removed in part 4 of M30.
/// </summary>
public class ReleaseGuardTests
{
    [Fact]
    public void A_release_waits_for_the_last_part_of_people()
    {
        var changelog = File.ReadAllLines(Path.Combine(RepositoryRoot(), "CHANGELOG.md"));
        var top = changelog.First(l => l.StartsWith("## "));
        var isRelease = Regex.IsMatch(top, @"^## \d+\.\d+\.\d+ \(\d{4}-\d{2}-\d{2}\)");
        if (!isRelease)
            return;

        var web = Path.Combine(RepositoryRoot(), "src", "Commonage.Web");
        var source = string.Concat(Directory.EnumerateFiles(web, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")).Select(File.ReadAllText));
        Assert.True(source.Contains("PersonTagsToPeople") && source.Contains("DeletePersonAsync"),
            "The top changelog entry is dated, but the last part of People (the PersonTagsToPeople migration and ArchiveBin.DeletePersonAsync) is not merged. Do not release the first parts alone.");
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CHANGELOG.md")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("CHANGELOG.md not found.");
    }
}
