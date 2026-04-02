namespace Klau.Cli.Domain;

/// <summary>
/// A single validated job ready for import into Klau.
/// Immutable — invalid states are unrepresentable.
/// </summary>
public sealed record JobImportRow
{
    public required string CustomerName { get; init; }
    public string? SiteName { get; init; }
    public string? SiteAddress { get; init; }
    public string? SiteCity { get; init; }
    public string? SiteState { get; init; }
    public string? SiteZip { get; init; }
    public string? JobType { get; init; }
    public string? ContainerSize { get; init; }
    public string? TimeWindow { get; init; }
    public string? Priority { get; init; }
    public string? Notes { get; init; }
    public string RequestedDate { get; init; } = DateTime.Today.ToString("yyyy-MM-dd");
    public string? ExternalId { get; init; }
}

/// <summary>
/// Result of mapping spreadsheet rows to typed domain objects.
/// </summary>
public sealed record MappedBatch(
    IReadOnlyList<JobImportRow> Rows,
    IReadOnlyList<RowWarning> Warnings);

/// <summary>
/// A warning for a specific row that was skipped or had issues.
/// </summary>
public sealed record RowWarning(int RowNumber, string Message);
