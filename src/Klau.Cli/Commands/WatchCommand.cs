using System.CommandLine;
using System.CommandLine.Invocation;
using Klau.Cli.Output;

namespace Klau.Cli.Commands;

/// <summary>
/// Watches a folder for new CSV files and automatically imports them.
/// Processed files are moved to a processed/ subfolder.
/// </summary>
public static class WatchCommand
{
    public static Command Create()
    {
        var folderOption = new Option<DirectoryInfo>("--folder", "Folder to watch for new CSV files.")
        { IsRequired = true };
        var patternOption = new Option<string>("--pattern", () => "*.csv", "File pattern to match.");
        var dateOption = new Option<string?>("--date", "Dispatch date (YYYY-MM-DD). Defaults to today.");
        var optimizeOption = new Option<bool>("--optimize", "Run dispatch optimization after each import.");

        var command = new Command("watch", "Watch a folder for new CSV files and import them automatically.")
        {
            folderOption,
            patternOption,
            dateOption,
            optimizeOption,
        };

        command.SetHandler(async (InvocationContext ctx) =>
        {
            var folder = ctx.ParseResult.GetValueForOption(folderOption)!;
            var pattern = ctx.ParseResult.GetValueForOption(patternOption)!;
            var date = ctx.ParseResult.GetValueForOption(dateOption);
            var optimize = ctx.ParseResult.GetValueForOption(optimizeOption);
            var apiKey = ctx.ParseResult.GetValueForOption(Program.ApiKeyOption);

            var ct = ctx.GetCancellationToken();

            await RunAsync(folder, pattern, date, optimize, apiKey, ct);
        });

        return command;
    }

    private static async Task RunAsync(
        DirectoryInfo folder,
        string pattern,
        string? date,
        bool optimize,
        string? apiKey,
        CancellationToken ct)
    {
        if (!folder.Exists)
        {
            ConsoleOutput.Error($"Folder not found: {folder.FullName}");
            return;
        }

        var processedDir = Path.Combine(folder.FullName, "processed");
        var outputDir = Path.Combine(folder.FullName, "output");
        Directory.CreateDirectory(processedDir);
        Directory.CreateDirectory(outputDir);

        ConsoleOutput.Blank();
        ConsoleOutput.Status($"Watching {folder.FullName} for {pattern} files...");
        ConsoleOutput.Status("Press Ctrl+C to stop.");
        ConsoleOutput.Blank();

        using var watcher = new FileSystemWatcher(folder.FullName, pattern)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };

        var fileQueue = new Queue<string>();

        // Process existing files first
        foreach (var existingFile in Directory.GetFiles(folder.FullName, pattern))
            fileQueue.Enqueue(existingFile);

        watcher.Created += (_, e) =>
        {
            lock (fileQueue)
            {
                fileQueue.Enqueue(e.FullPath);
            }
        };

        while (!ct.IsCancellationRequested)
        {
            string? filePath = null;

            lock (fileQueue)
            {
                if (fileQueue.Count > 0)
                    filePath = fileQueue.Dequeue();
            }

            if (filePath is not null)
            {
                // Wait for file to finish writing (stable size check)
                await WaitForStableFileAsync(filePath, ct);

                if (ct.IsCancellationRequested) break;

                ConsoleOutput.Header($"New file detected: {Path.GetFileName(filePath)}");

                var exportPath = Path.Combine(outputDir, $"dispatch-{Path.GetFileNameWithoutExtension(filePath)}.csv");

                await ImportCommand.RunAsync(
                    new FileInfo(filePath),
                    date ?? DateTime.Today.ToString("yyyy-MM-dd"),
                    mappingPath: null,
                    optimize,
                    exportPath: optimize ? exportPath : null,
                    apiKey,
                    ct);

                // Move processed file
                var destPath = Path.Combine(processedDir, Path.GetFileName(filePath));
                try
                {
                    File.Move(filePath, destPath, overwrite: true);
                    ConsoleOutput.Success($"Moved to processed/{Path.GetFileName(filePath)}");
                }
                catch (Exception ex)
                {
                    ConsoleOutput.Warning($"Could not move file: {ex.Message}");
                }
            }
            else
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }

        ConsoleOutput.Blank();
        ConsoleOutput.Status("Watch stopped.");
    }

    /// <summary>
    /// Wait until the file size stabilizes (i.e., no other process is writing to it).
    /// </summary>
    private static async Task WaitForStableFileAsync(string path, CancellationToken ct)
    {
        var previousSize = -1L;
        var stableChecks = 0;

        while (stableChecks < 3 && !ct.IsCancellationRequested)
        {
            await Task.Delay(500, ct).ConfigureAwait(false);

            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return;

                if (info.Length == previousSize)
                    stableChecks++;
                else
                {
                    previousSize = info.Length;
                    stableChecks = 0;
                }
            }
            catch
            {
                // File may be locked; retry
                stableChecks = 0;
            }
        }
    }
}
