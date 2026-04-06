using System.CommandLine;
using Klau.Cli.Auth;
using Klau.Cli.Commands;
using Klau.Cli.Output;

namespace Klau.Cli;

public static class Program
{
    /// <summary>
    /// Global --api-key option, shared across all commands.
    /// Highest priority in the key resolution chain.
    /// </summary>
    public static readonly Option<string?> ApiKeyOption = new(
        "--api-key",
        "Klau API key (overrides KLAU_API_KEY env var and stored credentials).");

    /// <summary>
    /// Global --tenant option for multi-tenant (division) scoping.
    /// Overrides the stored tenant in credentials.
    /// </summary>
    public static readonly Option<string?> TenantOption = new(
        "--tenant",
        "Tenant/division ID for multi-tenant operations.");

    /// <summary>
    /// Global --output option. Restricted to valid values by System.CommandLine.
    /// </summary>
    public static readonly Option<string?> OutputOption = new Option<string?>(
        "--output",
        "Output format: text (default) or json.").FromAmong("json", "text");

    /// <summary>
    /// Global --yes option — skips all interactive prompts (for CI/agent/automation use).
    /// </summary>
    public static readonly Option<bool> YesOption = new(
        ["--yes", "-y"],
        "Skip all interactive prompts (non-interactive mode for CI/agents).");

    public static async Task<int> Main(string[] args)
    {
        // Set output mode flags early — before any command runs.
        // Handles both "--output json" and "--output=json" by scanning for the value
        // after the flag in any supported System.CommandLine format.
        OutputMode.IsJson = ResolveOutputMode(args);
        OutputMode.AutoAccept = args.Any(a =>
            a is "--yes" or "-y");

        // Non-blocking update check — skip in JSON mode (no human to read it)
        var updateCheck = OutputMode.IsJson ? Task.CompletedTask : UpdateChecker.StartAsync();

        var rootCommand = new RootCommand(
            "Klau CLI - Import CSV/XLSX job data into Klau and optimize dispatch. No code required.")
        {
            ApiKeyOption,
            TenantOption,
            OutputOption,
            YesOption,
        };

        rootCommand.AddCommand(LoginCommand.Create());
        rootCommand.AddCommand(LogoutCommand.Create());
        rootCommand.AddCommand(StatusCommand.Create());
        rootCommand.AddCommand(DoctorCommand.Create());
        rootCommand.AddCommand(ImportCommand.Create());
        rootCommand.AddCommand(OptimizeCommand.Create());
        rootCommand.AddCommand(SetupCommand.Create());
        rootCommand.AddCommand(TenantCommands.Create());

        var exitCode = await rootCommand.InvokeAsync(args);

        // Show update notification if available (non-blocking, best effort)
        if (!OutputMode.IsJson)
        {
            try { await updateCheck.WaitAsync(TimeSpan.FromMilliseconds(500)); }
            catch { /* timeout or failure — don't delay exit */ }
        }

        return exitCode;
    }

    /// <summary>
    /// Resolve whether JSON output mode was requested. Handles:
    ///   --output json, --output=json, --output JSON (case-insensitive)
    /// </summary>
    private static bool ResolveOutputMode(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            // --output=json
            if (arg.StartsWith("--output=", StringComparison.OrdinalIgnoreCase))
                return arg[9..].Equals("json", StringComparison.OrdinalIgnoreCase);

            // --output json
            if (arg.Equals("--output", StringComparison.OrdinalIgnoreCase) &&
                i + 1 < args.Length)
                return args[i + 1].Equals("json", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
