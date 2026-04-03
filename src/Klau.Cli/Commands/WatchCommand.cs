using System.CommandLine;
using System.CommandLine.Invocation;
using System.Threading.Channels;
using Klau.Cli.Auth;
using Klau.Cli.Domain;
using Klau.Cli.Import;
using Klau.Cli.Output;
using Klau.Sdk;

namespace Klau.Cli.Commands;

/// <summary>
/// Watches a folder for new files and automatically imports them.
/// Designed for months-long unattended operation with:
/// - Channel-based event processing (no polling waste)
/// - Lock file to prevent concurrent watchers
/// - Failed file handling with error companion files
/// - Heartbeat file for liveness monitoring
/// - Processed directory retention policy
/// - Graceful SIGTERM handling
/// </summary>
public static class WatchCommand
{
    public static Command Create()
    {
        var folderOption = new Option<DirectoryInfo>("--folder", "Folder to watch for new files.")
        { IsRequired = true };
        var patternOption = new Option<string>("--pattern", () => "*.*",
            "File pattern to match (e.g. *.csv, *.xlsx).");
        var dateOption = new Option<string?>("--date",
            "Dispatch date (YYYY-MM-DD). Defaults to today. Use 'today' for dynamic date in watch mode.");
        var optimizeOption = new Option<bool>("--optimize",
            "Run dispatch optimization after each import.");
        var retainDaysOption = new Option<int>("--retain-days", () => 30,
            "Days to keep files in processed/ before cleanup.");

        var command = new Command("watch",
            "Watch a folder for new CSV/XLSX files and import them automatically.")
        {
            folderOption, patternOption, dateOption, optimizeOption, retainDaysOption,
        };

        command.SetHandler(async (InvocationContext ctx) =>
        {
            var folder = ctx.ParseResult.GetValueForOption(folderOption)!;
            var pattern = ctx.ParseResult.GetValueForOption(patternOption)!;
            var date = ctx.ParseResult.GetValueForOption(dateOption);
            var optimize = ctx.ParseResult.GetValueForOption(optimizeOption);
            var retainDays = ctx.ParseResult.GetValueForOption(retainDaysOption);
            var apiKey = ctx.ParseResult.GetValueForOption(Program.ApiKeyOption);
            var tenantFlag = ctx.ParseResult.GetValueForOption(Program.TenantOption);
            var ct = ctx.GetCancellationToken();

            ctx.ExitCode = await RunAsync(folder, pattern, date, optimize, retainDays, apiKey, tenantFlag, ct);
        });

        return command;
    }

    private static async Task<int> RunAsync(
        DirectoryInfo folder,
        string pattern,
        string? date,
        bool optimize,
        int retainDays,
        string? apiKey,
        string? tenantFlag,
        CancellationToken ct)
    {
        // --- Validate ---
        if (!folder.Exists)
        {
            ConsoleOutput.Error($"Folder not found: {folder.FullName}");
            ConsoleOutput.Hint("Create the folder or check the path.");
            return ExitCodes.InputError;
        }

        var resolvedKey = CredentialStore.ResolveApiKey(apiKey);
        if (string.IsNullOrWhiteSpace(resolvedKey))
        {
            ConsoleOutput.Error("No API key found.");
            ConsoleOutput.Hint("Run: klau login");
            return ExitCodes.ConfigError;
        }

        // --- Acquire lock file ---
        var lockPath = Path.Combine(folder.FullName, ".klau-watcher.lock");
        FileStream lockFile;
        try
        {
            lockFile = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            ConsoleOutput.Error("Another watcher is already running on this folder.");
            ConsoleOutput.Hint($"Remove {lockPath} if the previous watcher crashed.");
            return ExitCodes.ConfigError;
        }

        using (lockFile)
        {
            var processedDir = Path.Combine(folder.FullName, "processed");
            var failedDir = Path.Combine(folder.FullName, "failed");
            var outputDir = Path.Combine(folder.FullName, "output");
            Directory.CreateDirectory(processedDir);
            Directory.CreateDirectory(failedDir);
            Directory.CreateDirectory(outputDir);

            // --- Cleanup old processed files ---
            CleanupRetention(processedDir, retainDays);

            // --- Setup ---
            using var client = new KlauClient(resolvedKey);
            var tenantId = CredentialStore.ResolveTenantId(tenantFlag);
            IKlauClient api = tenantId is not null ? client.ForTenant(tenantId) : client;
            var pipeline = new ImportPipeline(api);

            var channel = Channel.CreateUnbounded<string>(
                new UnboundedChannelOptions { SingleReader = true });
            var knownFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            ConsoleOutput.Blank();
            StatusWithTimestamp($"Watching {folder.FullName} for {pattern} files...");
            StatusWithTimestamp("Press Ctrl+C to stop.");
            ConsoleOutput.Blank();

            // --- File system watcher ---
            using var watcher = new FileSystemWatcher(folder.FullName, pattern)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
                InternalBufferSize = 64 * 1024,
                EnableRaisingEvents = true,
            };

            watcher.Created += (_, e) => TryEnqueue(channel, knownFiles, e.FullPath);
            watcher.Renamed += (_, e) => TryEnqueue(channel, knownFiles, e.FullPath);
            watcher.Error += (_, e) =>
            {
                ConsoleOutput.Error($"File watcher error: {e.GetException().Message}");
                ConsoleOutput.Warning("Some file changes may have been missed. Scanning directory...");
                foreach (var f in Directory.GetFiles(folder.FullName, pattern))
                    TryEnqueue(channel, knownFiles, f);
            };

            // --- Enqueue existing files ---
            foreach (var existingFile in Directory.GetFiles(folder.FullName, pattern))
            {
                if (FileReader.IsSupported(existingFile))
                    TryEnqueue(channel, knownFiles, existingFile);
            }

            // --- Periodic safety net scan + heartbeat ---
            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(60), ct);

                        // Heartbeat
                        var heartbeatPath = Path.Combine(folder.FullName, ".klau-heartbeat");
                        await File.WriteAllTextAsync(heartbeatPath,
                            DateTime.UtcNow.ToString("O"), ct);

                        // Safety scan for missed files
                        foreach (var f in Directory.GetFiles(folder.FullName, pattern))
                        {
                            if (FileReader.IsSupported(f))
                                TryEnqueue(channel, knownFiles, f);
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch { /* heartbeat failure is non-fatal */ }
                }
            }, ct);

            // --- Process loop ---
            var filesProcessed = 0;
            var filesErrored = 0;

            try
            {
                await foreach (var filePath in channel.Reader.ReadAllAsync(ct))
                {
                    if (!File.Exists(filePath)) continue;
                    if (!FileReader.IsSupported(filePath)) continue;

                    try
                    {
                        var stable = await WaitForStableFileAsync(filePath, ct);
                        if (!stable) continue;

                        ct.ThrowIfCancellationRequested();

                        var dispatchDate = date is null or "today"
                            ? DateTime.Today.ToString("yyyy-MM-dd")
                            : date;

                        ConsoleOutput.Blank();
                        StatusWithTimestamp($"Processing: {Path.GetFileName(filePath)}");

                        var exportPath = optimize
                            ? Path.Combine(outputDir, $"dispatch-{Path.GetFileNameWithoutExtension(filePath)}.csv")
                            : null;

                        var exitCode = await ImportCommand.RunAsync(
                            new FileInfo(filePath), dispatchDate, null,
                            optimize, exportPath, false, resolvedKey, tenantFlag, ct);

                        if (exitCode == ExitCodes.Success || exitCode == ExitCodes.PartialFailure)
                        {
                            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                            var destName = $"{Path.GetFileNameWithoutExtension(filePath)}-{timestamp}{Path.GetExtension(filePath)}";
                            var destPath = Path.Combine(processedDir, destName);
                            File.Move(filePath, destPath, overwrite: true);
                            File.SetLastWriteTimeUtc(destPath, DateTime.UtcNow);
                            StatusWithTimestamp($"Moved to processed/{destName}");
                            filesProcessed++;
                        }
                        else
                        {
                            MoveToFailed(filePath, failedDir, $"Import exited with code {exitCode}");
                            filesErrored++;
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw; // Let outer catch handle graceful shutdown
                    }
                    catch (Exception ex)
                    {
                        ConsoleOutput.Error($"Failed to process {Path.GetFileName(filePath)}: {ex.Message}");
                        MoveToFailed(filePath, failedDir, ex.ToString());
                        filesErrored++;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Graceful shutdown
            }

            channel.Writer.TryComplete();

            ConsoleOutput.Blank();
            StatusWithTimestamp($"Watch stopped. Processed: {filesProcessed}, Errors: {filesErrored}");

            // Clean up lock file
            try { File.Delete(lockPath); } catch { /* best effort */ }

            return filesErrored > 0 ? ExitCodes.PartialFailure : ExitCodes.Success;
        }
    }

    private static void MoveToFailed(string filePath, string failedDir, string errorDetails)
    {
        try
        {
            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            var destName = $"{Path.GetFileNameWithoutExtension(filePath)}-{timestamp}{Path.GetExtension(filePath)}";
            var destPath = Path.Combine(failedDir, destName);
            File.Move(filePath, destPath, overwrite: true);
            File.WriteAllText(
                Path.Combine(failedDir, $"{destName}.error"),
                $"{DateTime.UtcNow:O}\n{errorDetails}");
            ConsoleOutput.Warning($"Moved to failed/{destName} (see .error file for details)");
        }
        catch (Exception ex)
        {
            ConsoleOutput.Error($"Could not move failed file: {ex.Message}");
        }
    }

    private static async Task<bool> WaitForStableFileAsync(
        string path, CancellationToken ct, int maxWaitSeconds = 300)
    {
        var previousSize = -1L;
        var stableChecks = 0;
        var deadline = DateTime.UtcNow.AddSeconds(maxWaitSeconds);

        while (stableChecks < 3 && !ct.IsCancellationRequested)
        {
            if (DateTime.UtcNow > deadline)
            {
                ConsoleOutput.Warning($"File not stable after {maxWaitSeconds}s, skipping: {Path.GetFileName(path)}");
                return false;
            }

            try { await Task.Delay(500, ct); }
            catch (OperationCanceledException) { return false; }

            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return false;

                if (info.Length == previousSize)
                    stableChecks++;
                else
                {
                    previousSize = info.Length;
                    stableChecks = 0;
                }
            }
            catch (IOException)
            {
                stableChecks = 0; // File locked, retry
            }
        }

        return stableChecks >= 3;
    }

    private static void CleanupRetention(string processedDir, int retainDays)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-retainDays);
            foreach (var file in Directory.GetFiles(processedDir))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch { /* retention cleanup is best-effort */ }
    }

    private static void TryEnqueue(Channel<string> channel, HashSet<string> knownFiles, string filePath)
    {
        lock (knownFiles)
        {
            if (knownFiles.Add(filePath))
                channel.Writer.TryWrite(filePath);
        }
    }

    private static void StatusWithTimestamp(string message) =>
        ConsoleOutput.Status($"[{DateTime.UtcNow:HH:mm:ss}] {message}");
}
