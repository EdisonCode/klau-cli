using System.CommandLine;
using System.CommandLine.Invocation;
using Klau.Cli.Auth;
using Klau.Cli.Domain;
using Klau.Cli.Output;

namespace Klau.Cli.Commands;

/// <summary>
/// klau login — authenticate with Klau and store credentials.
///
/// Two modes:
///   klau login --api-key kl_live_...   → store an existing key directly
///   klau login                          → email/password → auto-create API key
/// </summary>
public static class LoginCommand
{
    public static Command Create()
    {
        var apiKeyOption = new Option<string?>("--api-key",
            "Store an existing API key directly (skips interactive login).");

        var command = new Command("login",
            "Authenticate with Klau. Stores credentials in ~/.config/klau/credentials.json")
        {
            apiKeyOption,
        };

        command.SetHandler(async (InvocationContext ctx) =>
        {
            var explicitKey = ctx.ParseResult.GetValueForOption(apiKeyOption);
            ctx.ExitCode = await RunAsync(explicitKey);
        });

        return command;
    }

    private static async Task<int> RunAsync(string? explicitKey)
    {
        ConsoleOutput.Blank();

        // --- Mode 1: Explicit API key ---
        if (!string.IsNullOrWhiteSpace(explicitKey))
            return StoreExplicitKey(explicitKey);

        // --- Mode 2: Interactive email/password login ---
        var apiKey = await InteractiveAuth.InteractiveLoginAsync();
        if (apiKey is null)
            return ExitCodes.ApiError;

        ConsoleOutput.Blank();
        ConsoleOutput.Status("You're ready to go. Try: klau import <file.csv> --dry-run");
        ConsoleOutput.Blank();
        return ExitCodes.Success;
    }

    private static int StoreExplicitKey(string apiKey)
    {
        if (!apiKey.StartsWith("kl_live_"))
        {
            ConsoleOutput.Error("Invalid API key format. Keys must start with kl_live_");
            ConsoleOutput.Hint("Generate a key at Settings > Developer in your Klau dashboard.");
            return ExitCodes.ConfigError;
        }

        CredentialStore.Save(new StoredCredentials { ApiKey = apiKey });

        ConsoleOutput.Success($"Authenticated: {CredentialStore.Mask(apiKey)}");
        ConsoleOutput.Status($"Credentials stored at {CredentialStore.GetCredentialsPath()}");
        ConsoleOutput.Blank();
        ConsoleOutput.Status("You're ready to go. Try: klau import <file.csv> --dry-run");
        ConsoleOutput.Blank();
        return ExitCodes.Success;
    }
}
