namespace Commonage.Web.Import;

/// <summary>
/// Where a spreadsheet and a ZIP file wait between the dry run and the import, and where an admin can put a folder of scans
/// instead of sending them over the web. Both are inside the media volume, which nothing serves by path: the app only ever
/// hands out a stored file by its id. Names of folders come from a listing and are never joined from what a browser sent.
/// </summary>
public sealed class ImportStaging(string storageRoot)
{
    public const long MaxCsvBytes = 20L * 1024 * 1024;
    public const long MaxZipBytes = 4L * 1024 * 1024 * 1024;

    private string Waiting => Path.Combine(storageRoot, "import-staging");

    /// <summary>The folders in here are offered as places to import from.</summary>
    public string FoldersRoot => Path.Combine(storageRoot, "import");

    public Guid Create()
    {
        var id = Guid.NewGuid();
        Directory.CreateDirectory(DirectoryOf(id));
        return id;
    }

    public string DirectoryOf(Guid id) => Path.Combine(Waiting, id.ToString("N"));

    public string CsvPath(Guid id) => Path.Combine(DirectoryOf(id), "sheet.csv");

    public string ZipPath(Guid id) => Path.Combine(DirectoryOf(id), "scans.zip");

    public string WorkDirectory(Guid id) => Path.Combine(DirectoryOf(id), "work");

    public bool Exists(Guid id) => File.Exists(CsvPath(id));

    public void Delete(Guid id)
    {
        try
        {
            if (Directory.Exists(DirectoryOf(id)))
                Directory.Delete(DirectoryOf(id), recursive: true);
        }
        catch (IOException)
        {
            // Left over, and removed with the next old one.
        }
    }

    /// <summary>What nobody went on with is a copy of private material and a lot of disk, so a day is all it gets.</summary>
    public void DeleteOld(TimeSpan age, DateTime now)
    {
        if (!Directory.Exists(Waiting))
            return;
        foreach (var directory in Directory.EnumerateDirectories(Waiting))
        {
            try
            {
                if (now - Directory.GetLastWriteTimeUtc(directory) > age)
                    Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // In use, so it is not old.
            }
        }
    }

    public IReadOnlyList<string> Folders() =>
        !Directory.Exists(FoldersRoot)
            ? []
            : Directory.EnumerateDirectories(FoldersRoot)
                .Where(d => !new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint))
                .Select(d => Path.GetFileName(d))
                .Order(StringComparer.CurrentCulture)
                .ToList();

    /// <summary>The folder with this name, if the listing has it.</summary>
    public string? FolderPath(string? name) =>
        name is not null && Folders().Contains(name, StringComparer.Ordinal) ? Path.Combine(FoldersRoot, name) : null;
}
