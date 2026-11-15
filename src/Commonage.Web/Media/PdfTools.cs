using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Options;

namespace Commonage.Web.Media;

/// <summary>
/// Reads PDF files with poppler's <c>pdfinfo</c> and <c>pdftoppm</c>, as separate processes. A PDF is a complicated format
/// and the parser is a large C++ library, so it never runs inside the app: each call has a time limit and is killed when
/// it exceeds it, the output size is set by us (<c>-scale-to</c>), and nothing from the file or its name is ever put on a
/// command line. Only curators can add a PDF, never a visitor.
/// </summary>
public class PdfTools(IOptions<UploadOptions> options)
{
    private readonly object _lock = new();
    private bool? _available;

    /// <summary>
    /// Removes copies left behind when the app was stopped while poppler was working; they hold whole PDFs. Called once at
    /// start. Only folders older than an hour go, so a copy in use is never touched.
    /// </summary>
    public static void RemoveStaleCopies()
    {
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(Path.GetTempPath(), "commonage-pdf-*"))
            {
                if (DateTime.UtcNow - Directory.GetCreationTimeUtc(folder) > TimeSpan.FromHours(1))
                    Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be removed now is tried again at the next start.
        }
    }

    /// <summary>Whether poppler is installed. An image built without it (see docs/deployment.md) refuses PDFs.</summary>
    public bool IsAvailable
    {
        get
        {
            lock (_lock)
            {
                // Looked at once, because installing poppler means a new image anyway.
                _available ??= Run("pdfinfo", ["-v"], out _, out _) is 0 or 99;
                return _available.Value;
            }
        }
    }

    /// <summary>A folder of our own with a copy of the PDF, removed again when this is disposed.</summary>
    public sealed class Copy : IDisposable
    {
        public Copy(Stream content)
        {
            FolderPath = Path.Combine(Path.GetTempPath(), "commonage-pdf-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(FolderPath);
            // Owner-only where the system has such permissions: the file may hold what the archive does not publish.
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(FolderPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            PdfPath = Path.Combine(FolderPath, "in.pdf");
            using var output = File.Create(PdfPath);
            content.CopyTo(output);
        }

        public string PdfPath { get; }

        public string FolderPath { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(FolderPath, recursive: true);
            }
            catch (IOException)
            {
                // Removed at the next start by RemoveStaleCopies.
            }
        }
    }

    /// <summary>How many pages the file has. Throws <see cref="InvalidDataException"/> for a file poppler cannot read.</summary>
    public int PageCount(Copy copy)
    {
        var exit = Run("pdfinfo", [copy.PdfPath], out var output, out var error);
        if (exit != 0)
            throw new InvalidDataException(Short("The PDF could not be read: " + (error.Length > 0 ? error : "exit code " + exit)));
        foreach (var line in output.Split('\n'))
        {
            if (line.StartsWith("Pages:", StringComparison.Ordinal)
                && int.TryParse(line["Pages:".Length..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var pages) && pages > 0)
                return pages;
        }
        throw new InvalidDataException("The PDF has no pages.");
    }

    /// <summary>One page as a PNG whose longer side is at most <paramref name="size"/> pixels, whatever the page size says.</summary>
    public byte[] RenderPage(Copy copy, int page, int size)
    {
        var root = Path.Combine(copy.FolderPath, "page");
        var number = page.ToString(CultureInfo.InvariantCulture);
        var exit = Run("pdftoppm",
            ["-png", "-singlefile", "-scale-to", size.ToString(CultureInfo.InvariantCulture), "-f", number, "-l", number, copy.PdfPath, root],
            out _, out var error);
        var png = root + ".png";
        if (exit != 0 || !File.Exists(png))
            throw new InvalidDataException(Short($"Page {page} of the PDF could not be drawn: " + (error.Length > 0 ? error : "exit code " + exit)));
        try
        {
            return File.ReadAllBytes(png);
        }
        finally
        {
            File.Delete(png);
        }
    }

    /// <summary>
    /// The text of the first pages, for search. A PDF of scans has none, and that is no error. Throws
    /// <see cref="InvalidDataException"/> only when poppler fails, which the caller may ignore: the text is an extra.
    /// </summary>
    public string ExtractText(Copy copy, int lastPage, int maxLength)
    {
        var exit = Run("pdftotext",
            ["-enc", "UTF-8", "-nopgbrk", "-f", "1", "-l", lastPage.ToString(CultureInfo.InvariantCulture), copy.PdfPath, "-"],
            out var output, out var error, maxLength);
        if (exit != 0)
            throw new InvalidDataException(Short("The text of the PDF could not be read: " + (error.Length > 0 ? error : "exit code " + exit)));
        // Control characters, and the NUL that PostgreSQL refuses, never reach the index.
        return FileTextIndex.Cut(string.Concat(output.Select(c => char.IsControl(c) && c is not ('\n' or '\t') ? ' ' : c)).Trim(), maxLength);
    }

    private int Run(string tool, IReadOnlyList<string> arguments, out string output, out string error, int outputLimit = 20_000)
    {
        var directory = options.Value.PdfToolsDirectory;
        var start = new ProcessStartInfo(string.IsNullOrEmpty(directory) ? tool : Path.Combine(directory, tool))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        Process process;
        try
        {
            process = Process.Start(start)!;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            output = "";
            error = "poppler is not installed";
            return -1;
        }

        using (process)
        {
            // Both streams are read while waiting, so a full pipe never holds the process up.
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(Math.Max(1, options.Value.PdfTimeoutSeconds))))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                output = "";
                error = "it took longer than " + options.Value.PdfTimeoutSeconds + " seconds";
                return -2;
            }
            // The error text can come from the file, so only a little of it is kept.
            output = Limit(outputTask.GetAwaiter().GetResult(), outputLimit);
            error = Limit(errorTask.GetAwaiter().GetResult(), 300).Trim();
            return process.ExitCode;
        }
    }

    private static string Limit(string text, int length) => text.Length <= length ? text : text[..length];

    private static string Short(string text) => text.Length <= 400 ? text : text[..400];
}
