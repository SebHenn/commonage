using System.Text;

namespace Commonage.Web.Import;

public enum CsvError
{
    None,
    Empty,
    NotUtf8,
    TooManyRows,
    UnclosedQuote,
}

/// <summary>One record of the file. <see cref="Number"/> is the row as a spreadsheet shows it: the header is row 1.</summary>
public record CsvRow(int Number, IReadOnlyList<string> Cells);

public record CsvTable(IReadOnlyList<string> Header, IReadOnlyList<CsvRow> Rows);

public record CsvResult(CsvError Error, CsvTable? Table = null);

/// <summary>
/// Reads a spreadsheet saved as CSV: UTF-8 with or without a byte order mark, quoted cells that hold commas, quotes and line
/// breaks, and a comma, semicolon or tab between cells, because a spreadsheet program picks the one of its country.
/// </summary>
public static class CsvReader
{
    public const int MaxRows = 100_000;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static CsvResult Read(ReadOnlySpan<byte> bytes)
    {
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return new CsvResult(CsvError.NotUtf8);
        }

        return Read(text.TrimStart('﻿'));
    }

    public static CsvResult Read(string text)
    {
        var delimiter = DelimiterOf(text);
        var records = new List<List<string>>();
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;     // inside quotes
        var wasQuoted = false;  // the current cell started with a quote, so an empty cell is not "no cell"
        var anything = false;   // the current record holds something, so a blank line is not a record

        void EndCell()
        {
            cells.Add(cell.ToString());
            cell.Clear();
            wasQuoted = false;
        }

        void EndRecord()
        {
            if (anything || cells.Count > 0 || cell.Length > 0 || wasQuoted)
            {
                EndCell();
                records.Add(cells);
            }
            cells = [];
            anything = false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c != '"')
                    cell.Append(c);
                else if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else
                    quoted = false;
                continue;
            }

            if (c == '"' && cell.Length == 0 && !wasQuoted)
            {
                quoted = wasQuoted = anything = true;
            }
            else if (c == delimiter)
            {
                anything = true;
                EndCell();
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                EndRecord();
                if (records.Count > MaxRows + 1)
                    return new CsvResult(CsvError.TooManyRows);
            }
            else
            {
                cell.Append(c);
                anything = true;
            }
        }

        if (quoted)
            return new CsvResult(CsvError.UnclosedQuote);
        EndRecord();

        // A row of nothing but empty cells is what a spreadsheet leaves behind below the last row.
        records.RemoveAll(record => record.All(c => c.Length == 0));
        if (records.Count == 0)
            return new CsvResult(CsvError.Empty);
        if (records.Count > MaxRows + 1)
            return new CsvResult(CsvError.TooManyRows);

        var number = 1;
        return new CsvResult(CsvError.None, new CsvTable(
            records[0].Select(h => h.Trim()).ToList(),
            records.Skip(1).Select(record => new CsvRow(++number, record)).ToList()));
    }

    /// <summary>The character that most often separates the cells of the first line.</summary>
    private static char DelimiterOf(string text)
    {
        var counts = new Dictionary<char, int> { [','] = 0, [';'] = 0, ['\t'] = 0 };
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"')
                quoted = !quoted;
            else if (!quoted && c is '\r' or '\n')
                break;
            else if (!quoted && counts.ContainsKey(c))
                counts[c]++;
        }

        var best = counts.MaxBy(pair => pair.Value);
        return best.Value == 0 ? ',' : best.Key;
    }
}
