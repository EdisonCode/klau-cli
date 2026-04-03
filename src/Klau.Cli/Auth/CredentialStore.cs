using System.Text.Json;
using System.Text.Json.Serialization;

namespace Klau.Cli.Auth;

/// <summary>
/// Stored credentials for the Klau CLI.
/// Persisted to ~/.config/klau/credentials.json (XDG-compliant).
/// </summary>
public sealed record StoredCredentials
{
    [JsonPropertyName("apiKey")]
    public required string ApiKey { get; init; }

    [JsonPropertyName("tenantId")]
    public string? TenantId { get; init; }

    [JsonPropertyName("storedAt")]
    public DateTime StoredAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Manages credential storage at ~/.config/klau/credentials.json.
///
/// Resolution order (highest priority first):
///   1. --api-key CLI flag
///   2. KLAU_API_KEY environment variable
///   3. ~/.config/klau/credentials.json
/// </summary>
public static class CredentialStore
{
    private const string DirectoryName = "klau";
    private const string FileName = "credentials.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Resolve the API key from the priority chain.
    /// Returns null if no key is found anywhere.
    /// </summary>
    public static string? ResolveApiKey(string? cliFlag)
    {
        // 1. Explicit CLI flag
        if (!string.IsNullOrWhiteSpace(cliFlag))
            return cliFlag;

        // 2. Environment variable
        var envKey = Environment.GetEnvironmentVariable("KLAU_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey))
            return envKey;

        // 3. Stored credentials
        var creds = Load();
        return creds?.ApiKey;
    }

    /// <summary>
    /// Resolve the tenant ID from the priority chain.
    /// Priority: CLI --tenant flag > stored credentials.
    /// Returns null if no tenant is configured anywhere.
    /// </summary>
    public static string? ResolveTenantId(string? cliFlag)
    {
        if (!string.IsNullOrWhiteSpace(cliFlag))
            return cliFlag;

        return Load()?.TenantId;
    }

    /// <summary>
    /// Store credentials to disk.
    /// </summary>
    public static void Save(StoredCredentials credentials)
    {
        var dir = GetConfigDirectory();
        Directory.CreateDirectory(dir);

        var path = GetCredentialsPath();
        var tmpPath = path + ".tmp";
        var json = JsonSerializer.Serialize(credentials, JsonOptions);
        File.WriteAllText(tmpPath, json);

        // Restrict file permissions on Unix-like systems before moving into place
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(tmpPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(tmpPath, path, overwrite: true);
    }

    /// <summary>
    /// Load stored credentials, or null if none exist.
    /// </summary>
    public static StoredCredentials? Load()
    {
        var path = GetCredentialsPath();
        if (!File.Exists(path)) return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<StoredCredentials>(json);
        }
        catch
        {
            return null; // Corrupted file — treat as no credentials
        }
    }

    /// <summary>
    /// Delete stored credentials.
    /// </summary>
    public static bool Delete()
    {
        var path = GetCredentialsPath();
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    /// <summary>
    /// Check whether credentials are stored.
    /// </summary>
    public static bool Exists() => File.Exists(GetCredentialsPath());

    /// <summary>
    /// Mask an API key for safe display (e.g. "kl_live_abc1...x7z9").
    /// </summary>
    public static string Mask(string apiKey) =>
        apiKey.Length > 16
            ? $"{apiKey[..12]}...{apiKey[^4..]}"
            : "kl_live_****";

    internal static string GetConfigDirectory()
    {
        // XDG on Linux/macOS, AppData on Windows
        var configBase = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData);

        if (string.IsNullOrEmpty(configBase))
            configBase = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config");

        return Path.Combine(configBase, DirectoryName);
    }

    internal static string GetCredentialsPath() =>
        Path.Combine(GetConfigDirectory(), FileName);
}
