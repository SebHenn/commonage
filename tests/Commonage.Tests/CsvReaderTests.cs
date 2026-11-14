using System.Text;
using Commonage.Web.Import;

namespace Commonage.Tests;

public class CsvReaderTests
{
    [Theory]
    [InlineData("a,b\r\n1,2\r\n")]
    [InlineData("a;b\n1;2\n")]
    [InlineData("a\tb\n1\t2")]
    public void Finds_the_delimiter_a_spreadsheet_program_chose(string text)
    {
        var table = CsvReader.Read(text).Table!;

        Assert.Equal(["a", "b"], table.Header);
        Assert.Equal(["1", "2"], table.Rows.Single().Cells);
        Assert.Equal(2, table.Rows.Single().Number);
    }

    [Fact]
    public void Reads_quoted_cells_with_delimiters_quotes_and_line_breaks()
    {
        var table = CsvReader.Read("title,note\n\"Mill, old\",\"He said \"\"hi\"\"\nthen left\"\n").Table!;

        Assert.Equal(["Mill, old", "He said \"hi\"\nthen left"], table.Rows.Single().Cells);
    }

    [Fact]
    public void Ignores_a_byte_order_mark_and_blank_lines()
    {
        var bytes = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes("title\nMühle\n\n\n")).ToArray();

        var table = CsvReader.Read(bytes).Table!;

        Assert.Equal(["title"], table.Header);
        Assert.Equal("Mühle", table.Rows.Single().Cells.Single());
    }

    [Fact]
    public void Refuses_text_that_is_not_utf8_rather_than_guessing_letters()
    {
        var latin1 = Encoding.Latin1.GetBytes("title\nMühle\n");

        Assert.Equal(CsvError.NotUtf8, CsvReader.Read(latin1).Error);
    }

    [Fact]
    public void Refuses_a_quote_that_is_never_closed()
    {
        Assert.Equal(CsvError.UnclosedQuote, CsvReader.Read("title\n\"open\n").Error);
    }

    [Fact]
    public void Refuses_an_empty_file()
    {
        Assert.Equal(CsvError.Empty, CsvReader.Read("").Error);
    }

    [Theory]
    [InlineData("'=SUM(A1)", "=SUM(A1)")]
    [InlineData("'+1", "+1")]
    [InlineData("'-x", "-x")]
    [InlineData("'@home", "@home")]
    [InlineData("'Tis the season", "'Tis the season")]
    [InlineData("O'Brien", "O'Brien")]
    [InlineData("=1", "=1")]
    public void Takes_off_only_the_apostrophe_an_export_adds_for_spreadsheet_safety(string cell, string expected)
    {
        Assert.Equal(expected, ItemImport.Unprotect(cell));
    }
}
