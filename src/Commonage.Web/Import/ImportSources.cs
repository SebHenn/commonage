using System.IO.Compression;

namespace Commonage.Web.Import;

/// <summary>A file offered by a ZIP or a folder. The path always uses "/" and is relative to where the import starts.</summary>
public record SourceEntry(string Path, long Length)
{
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];
}

public class EntryTooLargeException : Exception;

/// <summary>Where the pictures of an import come from. Nothing in here is ever written to a path made from a name inside it.</summary>
public interface IImportSource : IDisposable
{
    IReadOnlyList<SourceEntry> Entries { get; }

    /// <summary>A stream that can be read from the start more than once. Larger than <paramref name="maxBytes"/> throws <see cref="EntryTooLargeException"/>.</summary>
    Task<Stream> OpenSeekableAsync(SourceEntry entry, long maxBytes, CancellationToken cancellationToken);
}

public static class ImportSources
{
    /// <summary>What a ZIP made on a Mac or in Windows carries along without anyone asking.</summary>
    private static bool IsLeftover(string path) =>
        path.StartsWith("__MACOSX/", StringComparison.Ordinal)
        || path.Split('/').Any(part => part is ".DS_Store" or "Thumbs.db" or "desktop.ini" || part.StartsWith("._", StringComparison.Ordinal));

    public const int MaxEntries = 200_000;

    /// <summary>Null when the file is not a ZIP, or holds more files than an import can look at.</summary>
    public static IImportSource? OpenZip(string zipPath, string workDirectory)
    {
        FileStream? stream = null;
        try
        {
            stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            // ZipArchive reads the whole list of entries into memory, so a list that is too large is refused before that.
            if (CentralDirectoryTooLarge(stream))
            {
                stream.Dispose();
                return null;
            }

            var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count > MaxEntries)
            {
                archive.Dispose();
                return null;
            }

            return new ZipSource(archive, workDirectory);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or NotSupportedException)
        {
            stream?.Dispose();
            return null;
        }
    }

    /// <summary>About 250 bytes for each entry that may be there; a ZIP64 end record (all ones) is not looked into and counts as too large.</summary>
    private const uint MaxCentralDirectoryBytes = MaxEntries * 250u;

    private static bool CentralDirectoryTooLarge(FileStream stream)
    {
        var tail = (int)Math.Min(stream.Length, 22 + 65535);
        var buffer = new byte[tail];
        stream.Position = stream.Length - tail;
        stream.ReadExactly(buffer);
        stream.Position = 0;
        for (var i = tail - 22; i >= 0; i--)
        {
            if (buffer[i] == 0x50 && buffer[i + 1] == 0x4B && buffer[i + 2] == 5 && buffer[i + 3] == 6)
            {
                // Any field of all ones says "look in the ZIP64 record", which is not looked into: too large.
                var disk = BitConverter.ToUInt16(buffer, i + 4);
                var diskOfDirectory = BitConverter.ToUInt16(buffer, i + 6);
                var entriesOnDisk = BitConverter.ToUInt16(buffer, i + 8);
                var entries = BitConverter.ToUInt16(buffer, i + 10);
                var size = BitConverter.ToUInt32(buffer, i + 12);
                var offset = BitConverter.ToUInt32(buffer, i + 16);
                return size > MaxCentralDirectoryBytes
                    || disk == ushort.MaxValue || diskOfDirectory == ushort.MaxValue || entriesOnDisk == ushort.MaxValue
                    || entries == ushort.MaxValue || size == uint.MaxValue || offset == uint.MaxValue;
            }
        }

        return false; // not a ZIP; ZipArchive says so
    }

    public static IImportSource OpenFolder(string folder) => new FolderSource(folder);

    private sealed class ZipSource : IImportSource
    {
        private readonly ZipArchive _archive;
        private readonly string _workDirectory;
        private readonly Dictionary<string, ZipArchiveEntry> _byPath = new(StringComparer.Ordinal);

        public ZipSource(ZipArchive archive, string workDirectory)
        {
            _archive = archive;
            _workDirectory = workDirectory;
            var entries = new List<SourceEntry>();
            foreach (var entry in archive.Entries)
            {
                var path = entry.FullName.Replace('\\', '/');
                if (path.EndsWith('/') || IsLeftover(path) || _byPath.ContainsKey(path))
                    continue;
                _byPath[path] = entry;
                entries.Add(new SourceEntry(path, entry.Length));
            }

            Entries = entries;
        }

        public IReadOnlyList<SourceEntry> Entries { get; }

        public async Task<Stream> OpenSeekableAsync(SourceEntry entry, long maxBytes, CancellationToken cancellationToken)
        {
            // The length in a ZIP is only a claim; what really comes out is counted.
            if (entry.Length > maxBytes)
                throw new EntryTooLargeException();

            Directory.CreateDirectory(_workDirectory);
            var work = new FileStream(
                System.IO.Path.Combine(_workDirectory, Guid.NewGuid().ToString("N") + ".tmp"),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            try
            {
                await using var source = await _byPath[entry.Path].OpenAsync(cancellationToken);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > maxBytes)
                        throw new EntryTooLargeException();
                    await work.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                work.Position = 0;
                return work;
            }
            catch
            {
                await work.DisposeAsync();
                throw;
            }
        }

        public void Dispose() => _archive.Dispose();
    }

    private sealed class FolderSource : IImportSource
    {
        private readonly Dictionary<string, string> _fullPaths = new(StringComparer.Ordinal);

        public FolderSource(string folder)
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                // A link could lead out of the folder.
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,
            };
            var entries = new List<SourceEntry>();
            foreach (var path in Directory.EnumerateFiles(folder, "*", options).Take(ImportSources.MaxEntries))
            {
                var relative = System.IO.Path.GetRelativePath(folder, path).Replace('\\', '/');
                if (IsLeftover(relative))
                    continue;
                _fullPaths[relative] = path;
                entries.Add(new SourceEntry(relative, new FileInfo(path).Length));
            }

            Entries = entries;
        }

        public IReadOnlyList<SourceEntry> Entries { get; }

        public Task<Stream> OpenSeekableAsync(SourceEntry entry, long maxBytes, CancellationToken cancellationToken)
        {
            if (entry.Length > maxBytes)
                throw new EntryTooLargeException();
            return Task.FromResult<Stream>(new FileStream(
                _fullPaths[entry.Path], FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous));
        }

        public void Dispose()
        {
        }
    }
}
