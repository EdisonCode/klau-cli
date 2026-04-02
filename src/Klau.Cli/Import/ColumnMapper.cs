namespace Klau.Cli.Import;

/// <summary>
/// Result of mapping a single CSV column to a Klau field.
/// </summary>
public sealed record ColumnMatch(string CsvHeader, string KlauField, double Confidence);

/// <summary>
/// Full mapping result for all CSV columns.
/// </summary>
public sealed record ColumnMapping(IReadOnlyList<ColumnMatch> Matches, IReadOnlyList<string> UnmappedHeaders);

/// <summary>
/// Fuzzy-matches CSV column headers to Klau ImportJobRecord field names.
/// Uses a three-pass approach: exact match, substring match, then token overlap.
/// </summary>
public static class ColumnMapper
{
    /// <summary>
    /// Known aliases for each Klau field. Keys are the canonical field names;
    /// values are lowercase aliases (partial/full) that should map to them.
    /// </summary>
    private static readonly Dictionary<string, string[]> FieldAliases = new()
    {
        ["CustomerName"] = [
            "customer", "cust name", "customer name", "customername", "account name", "accountname", "cust",
            "service name", "billing name", "company", "company name", "client", "client name"
        ],
        ["SiteName"] = [
            "site", "site name", "sitename", "location", "job site", "jobsite",
            "site name 2", "display details"
        ],
        ["SiteAddress"] = [
            "address", "street", "site address", "siteaddress", "addr", "address1", "street address",
            "service address", "job address", "location address"
        ],
        ["SiteCity"] = [
            "city", "site city", "sitecity",
            "service city"
        ],
        ["SiteState"] = [
            "state", "st", "site state", "sitestate",
            "service state"
        ],
        ["SiteZip"] = [
            "zip", "zipcode", "zip code", "postal", "site zip", "sitezip", "postalcode", "postal code",
            "site zip code"
        ],
        ["JobType"] = [
            "type", "job type", "jobtype", "service type", "servicetype", "service code", "servicecode",
            "service description", "order action"
        ],
        ["ContainerSize"] = [
            "size", "container", "container size", "containersize", "yard", "yards",
            "size value", "box size", "can size"
        ],
        ["TimeWindow"] = [
            "window", "time window", "timewindow", "delivery window"
        ],
        ["Priority"] = [
            "priority", "pri", "order priority"
        ],
        ["Notes"] = [
            "notes", "instructions", "comments", "special instructions",
            "billing notes", "dispatch notes"
        ],
        ["RequestedDate"] = [
            "date", "requested date", "requesteddate", "request date", "delivery date",
            "scheduled", "scheduled date", "service date"
        ],
        ["ExternalId"] = [
            "external", "external id", "externalid", "order number", "ordernumber",
            "work order", "workorder", "wo", "po", "reference", "ref",
            "order nbr", "order no", "ticket", "ticket number"
        ],
    };

    /// <summary>
    /// Map CSV headers to Klau fields using fuzzy matching.
    /// </summary>
    public static ColumnMapping Map(IReadOnlyList<string> csvHeaders)
    {
        var matches = new List<ColumnMatch>();
        var unmapped = new List<string>();
        var usedFields = new HashSet<string>();

        // Build a flat lookup: normalized alias -> field name
        var aliasLookup = new List<(string Alias, string Field)>();
        foreach (var (field, aliases) in FieldAliases)
        {
            foreach (var alias in aliases)
                aliasLookup.Add((Normalize(alias), field));
        }

        foreach (var header in csvHeaders)
        {
            var normalized = Normalize(header);
            if (string.IsNullOrEmpty(normalized))
            {
                unmapped.Add(header);
                continue;
            }

            var match = TryMatch(normalized, aliasLookup, usedFields);
            if (match is not null)
            {
                matches.Add(match with { CsvHeader = header });
                usedFields.Add(match.KlauField);
            }
            else
            {
                unmapped.Add(header);
            }
        }

        return new ColumnMapping(matches, unmapped);
    }

    private static ColumnMatch? TryMatch(
        string normalized,
        List<(string Alias, string Field)> aliasLookup,
        HashSet<string> usedFields)
    {
        // Pass 1: Exact match
        foreach (var (alias, field) in aliasLookup)
        {
            if (usedFields.Contains(field)) continue;
            if (normalized == alias)
                return new ColumnMatch("", field, 1.0);
        }

        // Pass 2: Substring match (alias contained in header or header contained in alias)
        ColumnMatch? bestSubstring = null;
        foreach (var (alias, field) in aliasLookup)
        {
            if (usedFields.Contains(field)) continue;

            if (normalized.Contains(alias) || (normalized.Length > 3 && alias.Contains(normalized)))
            {
                // Score by how close the lengths are
                var score = 0.7 * Math.Min(normalized.Length, alias.Length) / Math.Max(normalized.Length, alias.Length);
                score = Math.Max(score, 0.5);
                if (bestSubstring is null || score > bestSubstring.Confidence)
                    bestSubstring = new ColumnMatch("", field, score);
            }
        }

        if (bestSubstring is not null)
            return bestSubstring;

        // Pass 3: Token overlap (split on spaces, measure intersection)
        var headerTokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        ColumnMatch? bestToken = null;

        foreach (var (alias, field) in aliasLookup)
        {
            if (usedFields.Contains(field)) continue;

            var aliasTokens = alias.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            var intersection = headerTokens.Intersect(aliasTokens).Count();
            var union = headerTokens.Union(aliasTokens).Count();

            if (intersection > 0 && union > 0)
            {
                var score = 0.3 * intersection / union;
                score = Math.Max(score, 0.2);
                if (bestToken is null || score > bestToken.Confidence)
                    bestToken = new ColumnMatch("", field, score);
            }
        }

        return bestToken;
    }

    /// <summary>
    /// Normalize a header string: lowercase, strip underscores/hyphens, collapse whitespace.
    /// </summary>
    internal static string Normalize(string header) =>
        System.Text.RegularExpressions.Regex.Replace(
            header.ToLowerInvariant().Replace('_', ' ').Replace('-', ' ').Trim(),
            @"\s+", " ");
}
