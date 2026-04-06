using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Reflection;
using Klau.Cli.Domain;
using Klau.Cli.Output;

namespace Klau.Cli.Commands;

/// <summary>
/// klau upgrade — self-update to the latest version via dotnet tool update.
/// </summary>
public static class UpgradeCommand
{
    private static string CurrentVersion =>
        typeof(UpgradeCommand).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion?.Split('+')[0] ?? "0.0.0";

    public static Command Create()
    {
        var command = new Command("upgrade",
            "Update klau to the latest version.");

        command.SetHandler(async (InvocationContext ctx) =>
        {
            var ct = ctx.GetCancellationToken();

            ConsoleOutput.Blank();
            ConsoleOutput.Status($"Current version: {CurrentVersion}");

            using var spinner = ConsoleOutput.StartSpinner("Checking for updates");
            var psi = new ProcessStartInfo("dotnet", "tool update -g Klau.Cli")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            try
            {
                using var process = Process.Start(psi);
                if (process is null)
                {
                    ConsoleOutput.Error("Failed to start dotnet tool update.");
                    ctx.ExitCode = ExitCodes.ConfigError;
                    return;
                }

                var stdout = await process.StandardOutput.ReadToEndAsync(ct);
                var stderr = await process.StandardError.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);

                spinner.Dispose();

                if (process.ExitCode == 0)
                {
                    // Parse version from dotnet output
                    // Typical: "Tool 'klau.cli' was successfully updated from version '0.2.2' to version '0.2.3'."
                    // Or: "Tool 'klau.cli' was reinstalled with the latest stable version (version '0.2.3')."
                    if (stdout.Contains("was successfully updated", StringComparison.OrdinalIgnoreCase))
                    {
                        ConsoleOutput.Success(stdout.Trim());
                    }
                    else if (stdout.Contains("already installed", StringComparison.OrdinalIgnoreCase)
                        || stdout.Contains("reinstalled", StringComparison.OrdinalIgnoreCase))
                    {
                        ConsoleOutput.Success($"Already up to date ({CurrentVersion}).");
                    }
                    else
                    {
                        ConsoleOutput.Success(stdout.Trim());
                    }
                    ctx.ExitCode = ExitCodes.Success;
                }
                else
                {
                    ConsoleOutput.Error("Upgrade failed.");
                    if (!string.IsNullOrWhiteSpace(stderr))
                        ConsoleOutput.Hint(stderr.Trim());
                    else if (!string.IsNullOrWhiteSpace(stdout))
                        ConsoleOutput.Hint(stdout.Trim());
                    ctx.ExitCode = ExitCodes.ConfigError;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ConsoleOutput.Error($"Upgrade failed: {ex.Message}");
                ConsoleOutput.Hint("Run manually: dotnet tool update -g Klau.Cli");
                ctx.ExitCode = ExitCodes.ConfigError;
            }

            ConsoleOutput.Blank();
        });

        return command;
    }
}
