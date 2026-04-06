using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;
using Klau.Cli.Output;

namespace Klau.Cli.Auth;

/// <summary>
/// Async, non-blocking version check against NuGet.
/// Runs in the background on every invocation — never delays the command.
/// </summary>
public static class UpdateChecker
{
    private const string PackageId = "Klau.Cli";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private static string CurrentVersion =>
        typeof(UpdateChecker).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.0.0";

    private static string CachePath =>
        Path.Combine(CredentialStore.GetConfigDirectory(), "update-check.json");

    /// <summary>
    /// Start a background version check. Call at startup, await at end of command
    /// to show the result (if any). Never throws.
    /// </summary>
    public static Task StartAsync() => Task.Run(async () =>
    {
        try
        {
            // Skip if checked recently
            if (File.Exists(CachePath))
            {
                var cacheAge = DateTime.UtcNow - File.GetLastWriteTimeUtc(CachePath);
                if (cacheAge < CheckInterval) return;
            }

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var url = $"https://api.nuget.org/v3-flatcontainer/{PackageId.ToLowerInvariant()}/index.json";
            var response = await http.GetFromJsonAsync<NuGetIndex>(url);

            if (response?.Versions is not { Count: > 0 }) return;

            var latest = response.Versions
                .Where(v => !v.Contains('-')) // skip pre-release
                .LastOrDefault();

            if (latest is null) return;

            // Cache the result
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            await File.WriteAllTextAsync(CachePath, latest);

            if (IsNewer(latest, CurrentVersion))
            {
                ConsoleOutput.Blank();
                ConsoleOutput.Status(
                    $"Update available: {CurrentVersion} \u2192 {latest}. " +
                    "Run: klau upgrade");
            }
        }
        catch
        {
            // Never fail the main command for an update check
        }
    });

    private static bool IsNewer(string latest, string current)
    {
        if (Version.TryParse(latest, out var latestVer) &&
            Version.TryParse(current, out var currentVer))
            return latestVer > currentVer;
        return false;
    }

    private sealed record NuGetIndex
    {
        [JsonPropertyName("versions")]
        public List<string> Versions { get; init; } = [];
    }
}
