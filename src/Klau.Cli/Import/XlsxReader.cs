using ClosedXML.Excel;

namespace Klau.Cli.Import;

/// <summary>
/// Reads tabular data from Excel (.xlsx) files using ClosedXML.
/// Reads the first worksheet, treats the first row as headers.
/// </summary>
public static class XlsxReader
{
    /// <summary>
    /// Read an Excel file and return parsed headers and rows.
    /// </summary>
    public static SpreadsheetData Read(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Excel file not found: {filePath}", filePath);

        using var workbook = new XLWorkbook(filePath);
        var worksheet = workbook.Worksheets.FirstOrDefault()
            ?? throw new FormatException("Excel file contains no worksheets.");

        var range = worksheet.RangeUsed();
        if (range is null)
            throw new FormatException("Excel worksheet is empty.");

        var rowCount = range.RowCount();
        var colCount = range.ColumnCount();

        if (rowCount < 1)
            throw new FormatException("Excel worksheet contains no data.");

        // First row is headers
        var headers = new string[colCount];
        for (var col = 1; col <= colCount; col++)
        {
            var cell = worksheet.Cell(1, col);
            headers[col - 1] = CellToString(cell).Trim();
        }

        // Remaining rows are data
        var rows = new List<string[]>();
        for (var row = 2; row <= rowCount; row++)
        {
            var rowData = new string[colCount];
            var hasData = false;

            for (var col = 1; col <= colCount; col++)
            {
                var cell = worksheet.Cell(row, col);
                var value = CellToString(cell);
                rowData[col - 1] = value;
                if (!string.IsNullOrWhiteSpace(value))
                    hasData = true;
            }

            if (hasData)
                rows.Add(rowData);
        }

        return new SpreadsheetData(headers, rows, "XLSX");
    }

    private static string CellToString(IXLCell cell)
    {
        if (cell.IsEmpty()) return string.Empty;

        // For dates, preserve ISO format
        if (cell.DataType == XLDataType.DateTime)
            return cell.GetDateTime().ToString("yyyy-MM-dd HH:mm:ss");

        // For numbers, use invariant culture to avoid locale issues
        if (cell.DataType == XLDataType.Number)
            return cell.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture);

        return cell.GetFormattedString().Trim();
    }
}
