namespace Klau.Cli.Import;

/// <summary>
/// Parsed result from reading a CSV file.
/// </summary>
public sealed record CsvData(IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>
/// Reads CSV files with support for quoted fields, delimiter detection,
/// BOM handling, and various line endings.
/// </summary>
public static class CsvReader
{
    private static readonly char[] CandidateDelimiters = [',', '\t', ';', '|'];

    /// <summary>
    /// Read a CSV file and return parsed headers and rows.
    /// </summary>
    private const long MaxFileSizeBytes = 100 * 1024 * 1024; // 100MB
    private const int MaxRowCount = 50_000;

    public static CsvData Read(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"CSV file not found: {filePath}", filePath);

        var fileSize = new FileInfo(filePath).Length;
        if (fileSize > MaxFileSizeBytes)
            throw new InvalidOperationException(
                $"File is {fileSize / (1024 * 1024)}MB, exceeding the 100MB limit. " +
                "Split the file into smaller batches.");

        var text = File.ReadAllText(filePath);
        var result = Parse(text);

        if (result.Rows.Count > MaxRowCount)
            throw new InvalidOperationException(
                $"File has {result.Rows.Count:N0} data rows, exceeding the {MaxRowCount:N0} row limit. " +
                "Split the file into smaller batches.");

        return result;
    }

    /// <summary>
    /// Parse CSV content from a string.
    /// </summary>
    public static CsvData Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new FormatException("CSV content is empty.");

        // Strip BOM if present
        content = StripBom(content);

        // Normalize line endings to \n
        content = content.Replace("\r\n", "\n").Replace("\r", "\n");

        // Detect delimiter
        var delimiter = DetectDelimiter(content);

        // Parse all lines
        var allRows = ParseRows(content, delimiter);

        // Remove fully empty rows
        allRows = allRows
            .Where(row => row.Any(cell => !string.IsNullOrWhiteSpace(cell)))
            .ToList<string[]>();

        if (allRows.Count == 0)
            throw new FormatException("CSV contains no data rows.");

        IReadOnlyList<string> headers = allRows[0];
        IReadOnlyList<IReadOnlyList<string>> dataRows = allRows.Skip(1)
            .Select(r => (IReadOnlyList<string>)r).ToList();

        return new CsvData(headers, dataRows);
    }

    /// <summary>
    /// Detect the most likely delimiter by counting occurrences in the first line.
    /// </summary>
    internal static char DetectDelimiter(string content)
    {
        // Take the first non-empty line for delimiter detection
        var firstLine = content.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (firstLine is null) return ',';

        var bestDelimiter = ',';
        var bestCount = 0;

        foreach (var candidate in CandidateDelimiters)
        {
            var count = CountUnquotedOccurrences(firstLine, candidate);
            if (count > bestCount)
            {
                bestCount = count;
                bestDelimiter = candidate;
            }
        }

        return bestDelimiter;
    }

    private static int CountUnquotedOccurrences(string line, char delimiter)
    {
        var count = 0;
        var inQuotes = false;
        foreach (var ch in line)
        {
            if (ch == '"')
                inQuotes = !inQuotes;
            else if (ch == delimiter && !inQuotes)
                count++;
        }
        return count;
    }

    private static List<string[]> ParseRows(string content, char delimiter)
    {
        var rows = new List<string[]>();
        var fields = new List<string>();
        var field = new System.Text.StringBuilder();
        var inQuotes = false;
        var i = 0;

        while (i < content.Length)
        {
            var ch = content[i];

            if (inQuotes)
            {
                if (ch == '"')
                {
                    // Check for escaped quote ("")
                    if (i + 1 < content.Length && content[i + 1] == '"')
                    {
                        field.Append('"');
                        i += 2;
                    }
                    else
                    {
                        // End of quoted field
                        inQuotes = false;
                        i++;
                    }
                }
                else
                {
                    field.Append(ch);
                    i++;
                }
            }
            else
            {
                if (ch == '"' && field.Length == 0)
                {
                    // Start of quoted field
                    inQuotes = true;
                    i++;
                }
                else if (ch == delimiter)
                {
                    fields.Add(field.ToString().Trim());
                    field.Clear();
                    i++;
                }
                else if (ch == '\n')
                {
                    fields.Add(field.ToString().Trim());
                    field.Clear();
                    rows.Add(fields.ToArray());
                    fields.Clear();
                    i++;
                }
                else
                {
                    field.Append(ch);
                    i++;
                }
            }
        }

        // Handle last field/row (no trailing newline)
        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString().Trim());
            rows.Add(fields.ToArray());
        }

        return rows;
    }

    private static string StripBom(string content)
    {
        if (content.Length > 0 && content[0] == '\uFEFF')
            return content[1..];
        return content;
    }
}
