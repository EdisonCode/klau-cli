using Klau.Cli.Output;

namespace Klau.Cli.Domain;

/// <summary>
/// Creates a cancellation token that only fires on real OS signals (SIGINT/SIGTERM),
/// not on pipe close or parent process exit. Fixes the issue where System.CommandLine's
/// token fires spuriously in non-TTY sessions, killing HTTP calls mid-flight.
///
/// Intentionally single-use-per-process: the CancellationTokenSource is not disposed
/// because the event handlers (CancelKeyPress, ProcessExit) hold references to it
/// for the lifetime of the process. This is fine for a CLI that runs one command and exits.
/// </summary>
public static class SafeCancellation
{
    private static CancellationTokenSource? _cts;

    public static CancellationToken Create(CancellationToken systemCommandLineToken)
    {
        // Interactive terminal: System.CommandLine's token is fine (fires on Ctrl+C)
        if (!OutputMode.IsNonInteractive)
            return systemCommandLineToken;

        // Non-interactive: reuse a single CTS for the process lifetime
        if (_cts is not null)
            return _cts.Token;

        _cts = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // Prevent immediate process termination
            _cts.Cancel();
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) => _cts.Cancel();

        return _cts.Token;
    }
}
