namespace Klau.Cli.Domain;

/// <summary>
/// Exit codes for CLI process. Process supervisors use these to determine restart behavior.
/// </summary>
public static class ExitCodes
{
    public const int Success = 0;
    public const int ConfigError = 1;
    public const int InputError = 2;
    public const int ApiError = 3;
    public const int PartialFailure = 4;
}

/// <summary>
/// Discriminated result from the import pipeline. Expected failures are results, not exceptions.
/// </summary>
public abstract record ImportOutcome
{
    public sealed record Success(
        int Imported,
        int Skipped,
        int CustomersCreated,
        int SitesCreated,
        IReadOnlyList<string> Errors) : ImportOutcome;

    public sealed record OptimizationComplete(
        string? Grade,
        int? PlanQuality,
        int? FlowScore,
        int? Assigned,
        int? Unassigned,
        string? DriveTimeSource) : ImportOutcome;

    public sealed record PartialFailure(
        int Imported,
        int Skipped,
        IReadOnlyList<string> Errors) : ImportOutcome;

    public sealed record ConfigurationMissing(string What, string Hint) : ImportOutcome;

    public sealed record InputError(string Message, string Hint) : ImportOutcome;

    public sealed record ApiError(string Code, string Message, string? Hint) : ImportOutcome;

    public sealed record DryRunComplete(int RowCount) : ImportOutcome;
}
