using System.CommandLine;
using Klau.Cli.Commands;

namespace Klau.Cli;

public static class Program
{
    /// <summary>
    /// Global --api-key option, shared across all commands.
    /// </summary>
    public static readonly Option<string?> ApiKeyOption = new(
        "--api-key",
        "Klau API key (overrides KLAU_API_KEY env var). Must start with kl_live_.");

    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("Klau CLI - Import CSV job data into Klau and optimize dispatch. No code required.")
        {
            ApiKeyOption,
        };

        // klau import <file> [options]
        rootCommand.AddCommand(ImportCommand.Create());

        // klau config init (interactive setup placeholder)
        rootCommand.AddCommand(CreateConfigCommand());

        return await rootCommand.InvokeAsync(args);
    }

    private static Command CreateConfigCommand()
    {
        var configCommand = new Command("config", "Manage Klau CLI configuration.");

        var initCommand = new Command("init", "Interactive first-time setup for API key and default options.");
        initCommand.SetHandler(() =>
        {
            Console.WriteLine();
            Console.Write("  Enter your Klau API key (kl_live_...): ");
            var key = Console.ReadLine()?.Trim();

            if (string.IsNullOrWhiteSpace(key))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("  No key provided. Aborting.");
                Console.ResetColor();
                return;
            }

            if (!key.StartsWith("kl_live_"))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("  Warning: API key should start with kl_live_");
                Console.ResetColor();
            }

            // Store hint for the user
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  To persist your API key, add this to your shell profile:");
            Console.ResetColor();
            Console.WriteLine($"    export KLAU_API_KEY={key}");
            Console.WriteLine();
            Console.WriteLine("  Then run: klau import <file.csv>");
            Console.WriteLine();
        });

        configCommand.AddCommand(initCommand);
        return configCommand;
    }
}
