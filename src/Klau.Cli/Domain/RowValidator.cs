namespace Klau.Cli.Domain;

/// <summary>
/// Validates mapped rows before they hit the API. Catches data quality issues
/// early with clear, actionable messages instead of cryptic API errors.
/// </summary>
public static class RowValidator
{
    private static readonly HashSet<string> ValidJobTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "DELIVERY", "PICKUP", "DUMP_RETURN", "SWAP", "INTERNAL_DUMP", "SERVICE_VISIT"
    };

    private static readonly HashSet<string> ValidTimeWindows = new(StringComparer.OrdinalIgnoreCase)
    {
        "MORNING", "AFTERNOON", "ANYTIME"
    };

    private static readonly HashSet<string> ValidPriorities = new(StringComparer.OrdinalIgnoreCase)
    {
        "NORMAL", "HIGH", "URGENT"
    };

    private static readonly HashSet<string> ValidContainerSizes = new()
    {
        "10", "15", "20", "30", "35", "40"
    };

    /// <summary>
    /// Validate all rows and return warnings for fixable issues.
    /// Rows with blocking issues are flagged but not removed —
    /// the caller decides whether to proceed or abort.
    /// </summary>
    public static ValidationReport Validate(IReadOnlyList<JobImportRow> rows)
    {
        var warnings = new List<RowWarning>();
        var addressMissing = 0;
        var containerSizeIssues = new List<(int Row, string Value)>();
        var jobTypeIssues = new List<(int Row, string Value)>();
        var duplicateExternalIds = new HashSet<string>();
        var seenExternalIds = new Dictionary<string, int>();

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var rowNum = i + 2; // +1 for 0-index, +1 for header row

            // Address check — critical for geocoding and route optimization
            if (string.IsNullOrWhiteSpace(row.SiteAddress))
            {
                addressMissing++;
                warnings.Add(new RowWarning(rowNum,
                    $"no address for \"{row.CustomerName}\" — geocoding will fail, job may not be routable"));
            }

            // Container size — must be a known roll-off size
            if (row.ContainerSize is not null && !ValidContainerSizes.Contains(row.ContainerSize))
            {
                // Try to extract a number from strings like "40YD COMPACTOR" or "20 YARD ROLL OFF"
                var extracted = ExtractContainerSize(row.ContainerSize);
                if (extracted is null)
                    containerSizeIssues.Add((rowNum, row.ContainerSize));
            }

            // Job type — must be a valid enum
            if (row.JobType is not null && !ValidJobTypes.Contains(row.JobType))
                jobTypeIssues.Add((rowNum, row.JobType));

            // Time window — must be valid if provided
            if (row.TimeWindow is not null && !ValidTimeWindows.Contains(row.TimeWindow))
                warnings.Add(new RowWarning(rowNum,
                    $"unknown time window \"{row.TimeWindow}\" (expected: MORNING, AFTERNOON, ANYTIME)"));

            // Priority — must be valid if provided
            if (row.Priority is not null && !ValidPriorities.Contains(row.Priority))
                warnings.Add(new RowWarning(rowNum,
                    $"unknown priority \"{row.Priority}\" (expected: NORMAL, HIGH, URGENT)"));

            // External ID uniqueness
            if (row.ExternalId is not null)
            {
                if (seenExternalIds.TryGetValue(row.ExternalId, out var firstRow))
                {
                    duplicateExternalIds.Add(row.ExternalId);
                    if (duplicateExternalIds.Count == 1) // only warn on first duplicate
                        warnings.Add(new RowWarning(rowNum,
                            $"duplicate external ID \"{row.ExternalId}\" (first seen at row {firstRow})"));
                }
                else
                {
                    seenExternalIds[row.ExternalId] = rowNum;
                }
            }
        }

        // Container size issues — group for cleaner output
        if (containerSizeIssues.Count > 0)
        {
            var uniqueValues = containerSizeIssues.Select(c => c.Value).Distinct().ToList();
            warnings.Add(new RowWarning(containerSizeIssues[0].Row,
                $"{containerSizeIssues.Count} row(s) have non-standard container size " +
                $"({string.Join(", ", uniqueValues.Select(v => $"\"{v}\""))}) — " +
                $"expected: {string.Join(", ", ValidContainerSizes.Order())}"));
        }

        // Job type issues — group for cleaner output
        if (jobTypeIssues.Count > 0)
        {
            var uniqueValues = jobTypeIssues.Select(j => j.Value).Distinct().ToList();
            warnings.Add(new RowWarning(jobTypeIssues[0].Row,
                $"{jobTypeIssues.Count} row(s) have unmapped job type " +
                $"({string.Join(", ", uniqueValues.Select(v => $"\"{v}\""))}) — " +
                $"configure service code mappings in Settings > Company or map in your CSV"));
        }

        return new ValidationReport(
            warnings,
            AddressMissingCount: addressMissing,
            DuplicateExternalIdCount: duplicateExternalIds.Count,
            TotalRows: rows.Count);
    }

    /// <summary>
    /// Try to extract a numeric container size from descriptive strings
    /// like "40YD COMPACTOR", "20 YARD ROLL OFF", "30-YD ROLLOFF".
    /// </summary>
    private static string? ExtractContainerSize(string value)
    {
        var digits = new string(value.TakeWhile(char.IsDigit).ToArray());
        return digits.Length > 0 && ValidContainerSizes.Contains(digits) ? digits : null;
    }
}

/// <summary>
/// Summary of row-level validation results.
/// </summary>
public sealed record ValidationReport(
    IReadOnlyList<RowWarning> Warnings,
    int AddressMissingCount,
    int DuplicateExternalIdCount,
    int TotalRows)
{
    public bool HasBlockingIssues => AddressMissingCount > TotalRows / 2; // >50% missing = likely mapping error
}
