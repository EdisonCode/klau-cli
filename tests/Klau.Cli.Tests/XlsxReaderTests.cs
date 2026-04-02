using ClosedXML.Excel;
using Klau.Cli.Import;
using Xunit;

namespace Klau.Cli.Tests;

public class XlsxReaderTests
{
    private static string CreateTempXlsx(string[] headers, params string[][] rows)
    {
        var path = Path.Combine(Path.GetTempPath(), $"klau-test-{Guid.NewGuid()}.xlsx");
        using var workbook = new XLWorkbook();
        var ws = workbook.AddWorksheet("Sheet1");

        for (var col = 0; col < headers.Length; col++)
            ws.Cell(1, col + 1).Value = headers[col];

        for (var row = 0; row < rows.Length; row++)
            for (var col = 0; col < rows[row].Length; col++)
                ws.Cell(row + 2, col + 1).Value = rows[row][col];

        workbook.SaveAs(path);
        return path;
    }

    [Fact]
    public void Read_ParsesHeadersAndRows()
    {
        var path = CreateTempXlsx(
            ["Customer", "Address", "City"],
            ["Acme Corp", "123 Main St", "Springfield"],
            ["Beta LLC", "456 Oak Ave", "Shelbyville"]);
        try
        {
            var data = XlsxReader.Read(path);

            Assert.Equal(3, data.Headers.Count);
            Assert.Equal("Customer", data.Headers[0]);
            Assert.Equal(2, data.Rows.Count);
            Assert.Equal("Acme Corp", data.Rows[0][0]);
            Assert.Equal("Shelbyville", data.Rows[1][2]);
            Assert.Equal("XLSX", data.SourceFormat);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Read_SkipsEmptyRows()
    {
        var path = CreateTempXlsx(
            ["Name"],
            ["Row1"],
            [""],
            ["Row3"]);
        try
        {
            var data = XlsxReader.Read(path);
            Assert.Equal(2, data.Rows.Count);
            Assert.Equal("Row1", data.Rows[0][0]);
            Assert.Equal("Row3", data.Rows[1][0]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Read_HandlesNumericCells()
    {
        var path = Path.Combine(Path.GetTempPath(), $"klau-test-{Guid.NewGuid()}.xlsx");
        using (var workbook = new XLWorkbook())
        {
            var ws = workbook.AddWorksheet("Sheet1");
            ws.Cell(1, 1).Value = "Size";
            ws.Cell(2, 1).Value = 40;
            ws.Cell(3, 1).Value = 20;
            workbook.SaveAs(path);
        }

        try
        {
            var data = XlsxReader.Read(path);
            Assert.Equal("40", data.Rows[0][0]);
            Assert.Equal("20", data.Rows[1][0]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Read_ThrowsOnEmptyWorksheet()
    {
        var path = Path.Combine(Path.GetTempPath(), $"klau-test-{Guid.NewGuid()}.xlsx");
        using (var workbook = new XLWorkbook())
        {
            workbook.AddWorksheet("Empty");
            workbook.SaveAs(path);
        }

        try
        {
            Assert.Throws<FormatException>(() => XlsxReader.Read(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FileReader_DetectsXlsxByExtension()
    {
        var path = CreateTempXlsx(["Col1"], ["Val1"]);
        try
        {
            var data = FileReader.Read(path);
            Assert.Equal("XLSX", data.SourceFormat);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FileReader_RejectsUnsupportedExtension()
    {
        var path = Path.Combine(Path.GetTempPath(), "test.pdf");
        File.WriteAllText(path, "not a pdf");
        try
        {
            Assert.Throws<NotSupportedException>(() => FileReader.Read(path));
        }
        finally { File.Delete(path); }
    }
}
