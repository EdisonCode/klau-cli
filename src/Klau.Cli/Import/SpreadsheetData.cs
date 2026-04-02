namespace Klau.Cli.Import;

/// <summary>
/// Format-agnostic parsed result from reading a tabular file (CSV, XLSX, etc.).
/// Fully immutable — all collections are read-only.
/// </summary>
public sealed record SpreadsheetData(
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    string SourceFormat);
