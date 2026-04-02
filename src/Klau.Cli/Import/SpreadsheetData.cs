namespace Klau.Cli.Import;

/// <summary>
/// Format-agnostic parsed result from reading a tabular file (CSV, XLSX, etc.).
/// </summary>
public sealed record SpreadsheetData(string[] Headers, List<string[]> Rows, string SourceFormat);
