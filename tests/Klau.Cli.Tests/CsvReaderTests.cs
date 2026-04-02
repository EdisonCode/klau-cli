using Klau.Cli.Import;
using Xunit;

namespace Klau.Cli.Tests;

public class CsvReaderTests
{
    [Fact]
    public void Parse_SimpleCSV_ReturnsHeadersAndRows()
    {
        var csv = "Name,City,State\nAcme,Denver,CO\nBeta,Austin,TX";
        var result = CsvReader.Parse(csv);

        Assert.Equal(["Name", "City", "State"], result.Headers);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("Acme", result.Rows[0][0]);
        Assert.Equal("Denver", result.Rows[0][1]);
        Assert.Equal("CO", result.Rows[0][2]);
        Assert.Equal("Beta", result.Rows[1][0]);
    }

    [Fact]
    public void Parse_QuotedFields_HandlesCommasInsideQuotes()
    {
        var csv = "Name,Address\n\"Smith, Inc.\",\"123 Main St, Suite 4\"";
        var result = CsvReader.Parse(csv);

        Assert.Equal(2, result.Headers.Count);
        Assert.Single(result.Rows);
        Assert.Equal("Smith, Inc.", result.Rows[0][0]);
        Assert.Equal("123 Main St, Suite 4", result.Rows[0][1]);
    }

    [Fact]
    public void Parse_EscapedQuotes_HandlesDoubleQuotes()
    {
        var csv = "Name,Note\nAcme,\"He said \"\"hello\"\"\"";
        var result = CsvReader.Parse(csv);

        Assert.Single(result.Rows);
        Assert.Equal("He said \"hello\"", result.Rows[0][1]);
    }

    [Fact]
    public void Parse_BOM_StrippedFromContent()
    {
        var csv = "\uFEFFName,City\nAcme,Denver";
        var result = CsvReader.Parse(csv);

        Assert.Equal("Name", result.Headers[0]);
        Assert.Single(result.Rows);
    }

    [Fact]
    public void Parse_WindowsLineEndings_Handled()
    {
        var csv = "Name,City\r\nAcme,Denver\r\nBeta,Austin\r\n";
        var result = CsvReader.Parse(csv);

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("Denver", result.Rows[0][1]);
    }

    [Fact]
    public void Parse_EmptyRows_Skipped()
    {
        var csv = "Name,City\n\nAcme,Denver\n\n\nBeta,Austin\n";
        var result = CsvReader.Parse(csv);

        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public void Parse_TabDelimited_Detected()
    {
        var csv = "Name\tCity\tState\nAcme\tDenver\tCO";
        var result = CsvReader.Parse(csv);

        Assert.Equal(["Name", "City", "State"], result.Headers);
        Assert.Single(result.Rows);
        Assert.Equal("Acme", result.Rows[0][0]);
    }

    [Fact]
    public void Parse_SemicolonDelimited_Detected()
    {
        var csv = "Name;City;State\nAcme;Denver;CO";
        var result = CsvReader.Parse(csv);

        Assert.Equal(["Name", "City", "State"], result.Headers);
        Assert.Single(result.Rows);
        Assert.Equal("CO", result.Rows[0][2]);
    }

    [Fact]
    public void Parse_EmptyContent_Throws()
    {
        Assert.Throws<FormatException>(() => CsvReader.Parse(""));
        Assert.Throws<FormatException>(() => CsvReader.Parse("   "));
    }

    [Fact]
    public void Parse_HeaderOnly_ReturnsEmptyRows()
    {
        var csv = "Name,City,State";
        var result = CsvReader.Parse(csv);

        Assert.Equal(["Name", "City", "State"], result.Headers);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void Parse_TrailingNewline_NoExtraRow()
    {
        var csv = "Name,City\nAcme,Denver\n";
        var result = CsvReader.Parse(csv);

        Assert.Single(result.Rows);
    }

    [Fact]
    public void DetectDelimiter_CommaIsDefault()
    {
        var delimiter = CsvReader.DetectDelimiter("Name,City,State");
        Assert.Equal(',', delimiter);
    }

    [Fact]
    public void DetectDelimiter_TabWhenMoreTabs()
    {
        var delimiter = CsvReader.DetectDelimiter("Name\tCity\tState\tZip");
        Assert.Equal('\t', delimiter);
    }

    [Fact]
    public void Parse_WhitespaceInFields_Trimmed()
    {
        var csv = "Name, City , State\n Acme , Denver , CO ";
        var result = CsvReader.Parse(csv);

        Assert.Equal("City", result.Headers[1]);
        Assert.Equal("Denver", result.Rows[0][1]);
    }
}
