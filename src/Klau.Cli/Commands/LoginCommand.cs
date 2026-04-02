using System.CommandLine;
using System.CommandLine.Invocation;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private const string DefaultBaseUrl = "https://api.getklau.com";

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
        return await InteractiveLoginAsync();
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

    private static async Task<int> InteractiveLoginAsync()
    {
        ConsoleOutput.Status("Log in with your Klau account to auto-create an API key.");
        ConsoleOutput.Blank();

        Console.Write("  Email: ");
        var email = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(email))
        {
            ConsoleOutput.Error("Email is required.");
            return ExitCodes.ConfigError;
        }

        Console.Write("  Password: ");
        var password = ReadPassword();
        Console.WriteLine();
        if (string.IsNullOrWhiteSpace(password))
        {
            ConsoleOutput.Error("Password is required.");
            return ExitCodes.ConfigError;
        }

        ConsoleOutput.Blank();

        using var http = new HttpClient { BaseAddress = new Uri(DefaultBaseUrl) };

        // Step 1: Authenticate → JWT
        ConsoleOutput.Status("Authenticating...");
        string jwt;
        try
        {
            var loginResponse = await http.PostAsJsonAsync("api/v1/auth/login", new
            {
                email,
                password,
            });

            if (!loginResponse.IsSuccessStatusCode)
            {
                var errorBody = await loginResponse.Content.ReadAsStringAsync();
                ConsoleOutput.Error("Authentication failed.");

                if (loginResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    ConsoleOutput.Hint("Check your email and password.");
                else
                    ConsoleOutput.Hint($"Server responded: {loginResponse.StatusCode}");

                return ExitCodes.ApiError;
            }

            var loginResult = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>();
            jwt = loginResult?.Token
                ?? throw new InvalidOperationException("No token in login response.");
        }
        catch (HttpRequestException ex)
        {
            ConsoleOutput.Error($"Could not reach Klau API: {ex.Message}");
            ConsoleOutput.Hint("Check your internet connection and try again.");
            return ExitCodes.ApiError;
        }

        ConsoleOutput.Success("Authenticated.");

        // Step 2: Create API key
        ConsoleOutput.Status("Creating API key...");
        try
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

            var hostname = Environment.MachineName;
            var keyResponse = await http.PostAsJsonAsync("api/v1/settings/developer/api-keys", new
            {
                name = $"klau-cli-{hostname}",
            });

            if (!keyResponse.IsSuccessStatusCode)
            {
                var status = keyResponse.StatusCode;
                ConsoleOutput.Error("Could not create API key.");

                if (status == System.Net.HttpStatusCode.Forbidden)
                {
                    ConsoleOutput.Hint("Your account needs admin access to create API keys.");
                    ConsoleOutput.Hint("Ask your admin, or use: klau login --api-key kl_live_...");
                }
                else
                {
                    ConsoleOutput.Hint($"Server responded: {status}");
                }

                return ExitCodes.ApiError;
            }

            var keyResult = await keyResponse.Content.ReadFromJsonAsync<CreateKeyResponse>();
            var apiKey = keyResult?.Key
                ?? throw new InvalidOperationException("No key in API response.");

            // Step 3: Store
            CredentialStore.Save(new StoredCredentials { ApiKey = apiKey });

            ConsoleOutput.Success($"API key created: {CredentialStore.Mask(apiKey)}");
            ConsoleOutput.Status($"Credentials stored at {CredentialStore.GetCredentialsPath()}");
            ConsoleOutput.Blank();
            ConsoleOutput.Status("You're ready to go. Try: klau import <file.csv> --dry-run");
            ConsoleOutput.Blank();
            return ExitCodes.Success;
        }
        catch (HttpRequestException ex)
        {
            ConsoleOutput.Error($"Could not reach Klau API: {ex.Message}");
            ConsoleOutput.Hint("Check your internet connection and try again.");
            return ExitCodes.ApiError;
        }
    }

    /// <summary>
    /// Read password from console without echoing characters.
    /// </summary>
    private static string ReadPassword()
    {
        var password = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace && password.Length > 0)
            {
                password.Length--;
                Console.Write("\b \b");
            }
            else if (!char.IsControl(key.KeyChar))
            {
                password.Append(key.KeyChar);
                Console.Write('*');
            }
        }
        return password.ToString();
    }

    private sealed record LoginResponse
    {
        [JsonPropertyName("token")]
        public string? Token { get; init; }
    }

    private sealed record CreateKeyResponse
    {
        [JsonPropertyName("key")]
        public string? Key { get; init; }
    }
}
