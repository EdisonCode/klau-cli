using System.CommandLine;
using Klau.Cli.Commands;

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

    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand(
            "Klau CLI - Import CSV/XLSX job data into Klau and optimize dispatch. No code required.")
        {
            ApiKeyOption,
        };

        rootCommand.AddCommand(LoginCommand.Create());
        rootCommand.AddCommand(LogoutCommand.Create());
        rootCommand.AddCommand(StatusCommand.Create());
        rootCommand.AddCommand(ImportCommand.Create());

        return await rootCommand.InvokeAsync(args);
    }
}
