using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Klau.Cli.Domain;
using Klau.Cli.Output;

namespace Klau.Cli.Auth;

/// <summary>
/// Shared interactive authentication flows used by both LoginCommand and
/// first-run detection in ImportCommand.
///
/// Returns (exitCode, apiKey) — callers check exitCode for success before using the key.
/// </summary>
public static class InteractiveAuth
{
    private const string DefaultBaseUrl = "https://api.getklau.com";

    /// <summary>
    /// Prompt the user to paste an API key, validate it, and store it.
    /// Returns the resolved API key on success, null on failure.
    /// </summary>
    public static string? PromptForApiKey()
    {
        Console.Write("  API key: ");
        var apiKey = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            ConsoleOutput.Error("No key entered.");
            return null;
        }

        if (!apiKey.StartsWith("kl_live_"))
        {
            ConsoleOutput.Error("Invalid API key format. Keys must start with kl_live_");
            ConsoleOutput.Hint("Generate a key at Settings > Developer in your Klau dashboard.");
            return null;
        }

        CredentialStore.Save(new StoredCredentials { ApiKey = apiKey });
        ConsoleOutput.Success($"Authenticated: {CredentialStore.Mask(apiKey)}");
        ConsoleOutput.Status($"Credentials stored at {CredentialStore.GetCredentialsPath()}");
        return apiKey;
    }

    /// <summary>
    /// Run the interactive email/password login flow, create an API key, and store it.
    /// Returns the resolved API key on success, null on failure.
    /// </summary>
    public static async Task<string?> InteractiveLoginAsync()
    {
        ConsoleOutput.Status("Log in with your Klau account to auto-create an API key.");
        ConsoleOutput.Blank();

        Console.Write("  Email: ");
        var email = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(email))
        {
            ConsoleOutput.Error("Email is required.");
            return null;
        }

        Console.Write("  Password: ");
        var password = ReadPassword();
        Console.WriteLine();
        if (string.IsNullOrWhiteSpace(password))
        {
            ConsoleOutput.Error("Password is required.");
            return null;
        }

        ConsoleOutput.Blank();

        using var http = new HttpClient { BaseAddress = new Uri(DefaultBaseUrl) };

        // Step 1: Authenticate -> JWT
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
                ConsoleOutput.Error("Authentication failed.");

                if (loginResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    ConsoleOutput.Hint("Check your email and password.");
                else
                    ConsoleOutput.Hint($"Server responded: {loginResponse.StatusCode}");

                return null;
            }

            var loginResult = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>();
            jwt = loginResult?.Token
                ?? throw new InvalidOperationException("No token in login response.");
        }
        catch (HttpRequestException ex)
        {
            ConsoleOutput.Error($"Could not reach Klau API: {ex.Message}");
            ConsoleOutput.Hint("Check your internet connection and try again.");
            return null;
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
                    ConsoleOutput.Hint("Ask your admin, or paste a key from Settings > Developer.");
                }
                else
                {
                    ConsoleOutput.Hint($"Server responded: {status}");
                }

                return null;
            }

            var keyResult = await keyResponse.Content.ReadFromJsonAsync<CreateKeyResponse>();
            var apiKey = keyResult?.Key
                ?? throw new InvalidOperationException("No key in API response.");

            // Step 3: Store
            CredentialStore.Save(new StoredCredentials { ApiKey = apiKey });

            ConsoleOutput.Success($"API key created: {CredentialStore.Mask(apiKey)}");
            ConsoleOutput.Status($"Credentials stored at {CredentialStore.GetCredentialsPath()}");
            return apiKey;
        }
        catch (HttpRequestException ex)
        {
            ConsoleOutput.Error($"Could not reach Klau API: {ex.Message}");
            ConsoleOutput.Hint("Check your internet connection and try again.");
            return null;
        }
    }

    /// <summary>
    /// Read password from console without echoing characters.
    /// </summary>
    internal static string ReadPassword()
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
