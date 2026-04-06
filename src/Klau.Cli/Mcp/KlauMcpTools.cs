using System.ComponentModel;
using Klau.Cli.Commands;
using Klau.Cli.Domain;
using Klau.Cli.Output;
using ModelContextProtocol.Server;

namespace Klau.Cli.Mcp;

/// <summary>
/// MCP tool definitions for klau-cli. Each tool delegates to the same RunAsync
/// methods that the CLI commands use, with JSON output forced on.
/// </summary>
[McpServerToolType]
public sealed class KlauMcpTools(McpCredentials credentials)
{
    /// <summary>
    /// Import jobs from a CSV or Excel file into Klau.
    /// </summary>
    [McpServerTool(Name = "import"), Description(
        "Import jobs from a CSV or Excel file into Klau for dispatch optimization. " +
        "Returns import counts, errors, and optionally optimization results.")]
    public async Task<string> Import(
        [Description("Absolute path to the CSV or XLSX file to import")] string file,
        [Description("Dispatch date in YYYY-MM-DD format. Defaults to today.")] string? date = null,
        [Description("Path to a column mapping JSON file")] string? mapping = null,
        [Description("Run dispatch optimization after import")] bool optimize = false,
        [Description("Export dispatch plan to this CSV path after optimization")] string? export = null,
        [Description("Validate and preview without importing")] bool dryRun = false,
        CancellationToken ct = default)
    {
        if (ValidatePath(file) is { } fileErr)
            return fileErr;
        if (mapping is not null && ValidatePath(mapping) is { } mapErr)
            return mapErr;

        var json = new CliJsonResponse("import");
        var exitCode = await ImportCommand.RunAsync(
            new FileInfo(file), date, mapping, optimize, export, dryRun,
            credentials.ApiKey, credentials.TenantId, ct, json);
        return json.ToJsonString(exitCode);
    }

    /// <summary>
    /// Run dispatch optimization for a date.
    /// </summary>
    [McpServerTool(Name = "optimize"), Description(
        "Run dispatch optimization for a date. Use after importing jobs, " +
        "or anytime there are unassigned jobs on the board.")]
    public async Task<string> Optimize(
        [Description("Dispatch date in YYYY-MM-DD format. Defaults to today.")] string? date = null,
        [Description("Optimization mode: full-day (default), new-job, or rebalance")] string? mode = null,
        [Description("Export dispatch plan to this CSV path")] string? export = null,
        CancellationToken ct = default)
    {
        var json = new CliJsonResponse("optimize");
        var exitCode = await OptimizeCommand.RunAsync(
            date, mode, export, credentials.ApiKey, credentials.TenantId, ct, json);
        return json.ToJsonString(exitCode);
    }

    /// <summary>
    /// Check CLI environment, authentication, and account readiness.
    /// </summary>
    [McpServerTool(Name = "doctor"), Description(
        "Check CLI environment, authentication, API connectivity, and Klau account " +
        "configuration for dispatch readiness.")]
    public async Task<string> Doctor(CancellationToken ct = default)
    {
        var json = new CliJsonResponse("doctor");
        var exitCode = await DoctorCommand.RunAsync(
            credentials.ApiKey, credentials.TenantId, ct, json);
        return json.ToJsonString(exitCode);
    }

    /// <summary>
    /// Show current authentication and configuration state.
    /// </summary>
    [McpServerTool(Name = "status"), Description(
        "Show current authentication, tenant configuration, and config directory.")]
    public Task<string> Status(CancellationToken ct = default)
    {
        var json = new CliJsonResponse("status");
        StatusCommand.RunSync(credentials.ApiKey, credentials.TenantId, json);
        return Task.FromResult(json.ToJsonString(ExitCodes.Success));
    }

    // ── Path validation ─────────────────────────────────────────────────────

    /// <summary>
    /// Restrict file access to the user's home directory to prevent an AI agent
    /// from reading arbitrary files on disk via MCP tool parameters.
    /// Returns an error JSON string if the path is invalid, null if OK.
    /// </summary>
    private static string? ValidatePath(string path)
    {
        try
        {
            var resolved = Path.GetFullPath(path);
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!resolved.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !resolved.Equals(home, StringComparison.Ordinal))
            {
                var json = new CliJsonResponse("import");
                json.SetError("PATH_DENIED",
                    $"Path is outside the user's home directory: {resolved}",
                    "Only files within the home directory are accessible via MCP.");
                return json.ToJsonString(ExitCodes.InputError);
            }
        }
        catch (Exception ex)
        {
            var json = new CliJsonResponse("import");
            json.SetError("INVALID_PATH", $"Invalid file path: {ex.Message}");
            return json.ToJsonString(ExitCodes.InputError);
        }

        return null;
    }
}
