using System.CommandLine;
using System.Reflection;
using Klau.Cli.Commands;
using Klau.Cli.Output;

namespace Klau.Cli;

public static class Program
{
    /// <summary>
    /// Global --api-key option, shared across all commands.
    /// </summary>
    public static readonly Option<string?> ApiKeyOption = new(
        "--api-key",
        "Klau API key (overrides KLAU_API_KEY env var). Must start with kl_live_.");

    private static string Version =>
        typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.0.0";

    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand(
            "Klau CLI - Import CSV/XLSX job data into Klau and optimize dispatch. No code required.")
        {
            ApiKeyOption,
        };

        var versionOption = new Option<bool>("--version", "Show version information.");
        rootCommand.AddGlobalOption(versionOption);

        rootCommand.SetHandler((bool showVersion) =>
        {
            if (showVersion)
                Console.WriteLine($"klau {Version}");
        }, versionOption);

        rootCommand.AddCommand(ImportCommand.Create());
        rootCommand.AddCommand(CreateConfigCommand());

        return await rootCommand.InvokeAsync(args);
    }

    private static Command CreateConfigCommand()
    {
        var configCommand = new Command("config", "Manage Klau CLI configuration.");

        var initCommand = new Command("init", "Interactive first-time setup for API key.");
        initCommand.SetHandler(() =>
        {
            ConsoleOutput.Blank();
            Console.Write("  Enter your Klau API key (kl_live_...): ");
            var key = Console.ReadLine()?.Trim();

            if (string.IsNullOrWhiteSpace(key))
            {
                ConsoleOutput.Error("No key provided. Aborting.");
                return;
            }

            if (!key.StartsWith("kl_live_"))
            {
                ConsoleOutput.Warning("API key should start with kl_live_");
            }

            // Mask the key in output — never echo secrets in full
            var masked = key.Length > 16
                ? $"{key[..12]}...{key[^4..]}"
                : "kl_live_****";

            ConsoleOutput.Blank();
            ConsoleOutput.Success($"Key validated: {masked}");
            ConsoleOutput.Blank();
            ConsoleOutput.Status("To persist your API key, add this to your shell profile:");
            ConsoleOutput.Hint($"export KLAU_API_KEY={masked}");
            ConsoleOutput.Status("Replace the masked portion with your full key.");
            ConsoleOutput.Blank();
            ConsoleOutput.Status("Then run: klau import <file.csv>");
            ConsoleOutput.Blank();
        });

        configCommand.AddCommand(initCommand);
        return configCommand;
    }
}
