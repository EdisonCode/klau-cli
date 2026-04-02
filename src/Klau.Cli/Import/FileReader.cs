namespace Klau.Cli.Import;

/// <summary>
/// Reads tabular data from CSV or XLSX files. Detects format by extension.
/// </summary>
public static class FileReader
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csv", ".tsv", ".txt", ".xlsx", ".xls"
    };

    /// <summary>
    /// Read a tabular file and return parsed headers and rows.
    /// Supported formats: .csv, .tsv, .txt (delimited), .xlsx
    /// </summary>
    public static SpreadsheetData Read(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"File not found: {filePath}", filePath);

        var ext = Path.GetExtension(filePath);

        if (!SupportedExtensions.Contains(ext))
            throw new NotSupportedException(
                $"Unsupported file format '{ext}'. Supported: {string.Join(", ", SupportedExtensions.Order())}");

        return ext.ToLowerInvariant() switch
        {
            ".xlsx" or ".xls" => XlsxReader.Read(filePath),
            _ => ToCsvSpreadsheet(CsvReader.Read(filePath)),
        };
    }

    /// <summary>
    /// Check whether a file extension is supported.
    /// </summary>
    public static bool IsSupported(string filePath) =>
        SupportedExtensions.Contains(Path.GetExtension(filePath));

    private static SpreadsheetData ToCsvSpreadsheet(CsvData csv) =>
        new(csv.Headers, csv.Rows, "CSV");
}
