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
    /// Global --output option for machine-readable output.
    /// </summary>
    public static readonly Option<string?> OutputOption = new(
        "--output",
        "Output format: 'json' for machine-readable JSON, omit for human-readable.");

    public static async Task<int> Main(string[] args)
    {
        // Non-blocking update check — runs in background, shows result at end
        var updateCheck = UpdateChecker.StartAsync();

        var rootCommand = new RootCommand(
            "Klau CLI - Import CSV/XLSX job data into Klau and optimize dispatch. No code required.")
        {
            ApiKeyOption,
            OutputOption,
        };

        // Set output mode before any command runs
        rootCommand.AddValidator(result =>
        {
            var output = result.GetValueForOption(OutputOption);
            if (output is not null)
            {
                if (!string.Equals(output, "json", StringComparison.OrdinalIgnoreCase))
                    result.ErrorMessage = $"Unsupported output format: '{output}'. Supported: json";
                else
                    OutputMode.IsJson = true;
            }
        });

        rootCommand.AddCommand(LoginCommand.Create());
        rootCommand.AddCommand(LogoutCommand.Create());
        rootCommand.AddCommand(StatusCommand.Create());
        rootCommand.AddCommand(DoctorCommand.Create());
        rootCommand.AddCommand(ImportCommand.Create());

        var exitCode = await rootCommand.InvokeAsync(args);

        // Show update notification if available (non-blocking, best effort)
        try { await updateCheck.WaitAsync(TimeSpan.FromMilliseconds(500)); }
        catch { /* timeout or failure — don't delay exit */ }

        return exitCode;
    }
}
