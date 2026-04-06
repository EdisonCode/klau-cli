namespace Klau.Cli.Output;

/// <summary>
/// Structured console output helpers for clean, readable CLI feedback.
/// </summary>
public static class ConsoleOutput
{
    // Visible for Spinner/ProgressBar to coordinate writes
    internal static readonly object Lock = new();

    /// <summary>
    /// Print a status/progress message in default color.
    /// </summary>
    public static void Status(string message)
    {
        if (OutputMode.IsJson) return;
        lock (Lock)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write("  ");
            Console.ResetColor();
            Console.WriteLine(message);
        }
    }

    /// <summary>
    /// Print a success message with a green checkmark.
    /// </summary>
    public static void Success(string message)
    {
        if (OutputMode.IsJson) return;
        lock (Lock)
        {
            Console.Write("    ");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("\u2713 ");
            Console.ResetColor();
            Console.WriteLine(message);
        }
    }

    /// <summary>
    /// Print a warning message with a yellow exclamation mark.
    /// </summary>
    public static void Warning(string message)
    {
        if (OutputMode.IsJson) return;
        lock (Lock)
        {
            Console.Write("    ");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("! ");
            Console.ResetColor();
            Console.WriteLine(message);
        }
    }

    /// <summary>
    /// Print an error message with a red X.
    /// </summary>
    public static void Error(string message)
    {
        if (OutputMode.IsJson) return;
        lock (Lock)
        {
            Console.Write("    ");
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Write("\u2717 ");
            Console.ResetColor();
            Console.WriteLine(message);
        }
    }

    /// <summary>
    /// Print an actionable hint to help the user fix the problem.
    /// </summary>
    public static void Hint(string message)
    {
        if (OutputMode.IsJson) return;
        lock (Lock)
        {
            Console.Write("    ");
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.Write("\u2192 ");
            Console.ResetColor();
            Console.WriteLine(message);
        }
    }

    /// <summary>
    /// Print a blank line for visual separation.
    /// </summary>
    public static void Blank()
    {
        if (OutputMode.IsJson) return;
        lock (Lock)
        {
            Console.WriteLine();
        }
    }

    /// <summary>
    /// Print a section header.
    /// </summary>
    public static void Header(string message)
    {
        if (OutputMode.IsJson) return;
        lock (Lock)
        {
            Console.WriteLine();
            Console.Write("  ");
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine(message);
            Console.ResetColor();
        }
    }

    /// <summary>
    /// Display a column mapping line (from -> to).
    /// </summary>
    public static void Mapping(string from, string to, int padFrom = 20)
    {
        if (OutputMode.IsJson) return;
        lock (Lock)
        {
            Console.Write("    ");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write(from.PadRight(padFrom));
            Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(" \u2192  ");
            Console.ResetColor();
            Console.WriteLine(to);
        }
    }

    /// <summary>
    /// Display a formatted table with headers and rows.
    /// </summary>
    public static void Table(string[] headers, List<string[]> rows)
    {
        if (OutputMode.IsJson) return;
        if (headers.Length == 0) return;

        // Calculate column widths
        var widths = new int[headers.Length];
        for (var i = 0; i < headers.Length; i++)
            widths[i] = headers[i].Length;

        foreach (var row in rows)
        {
            for (var i = 0; i < Math.Min(row.Length, headers.Length); i++)
            {
                var len = (row[i] ?? "").Length;
                if (len > widths[i])
                    widths[i] = len;
            }
        }

        // Cap column widths at 40 characters
        for (var i = 0; i < widths.Length; i++)
            widths[i] = Math.Min(widths[i], 40);

        lock (Lock)
        {
            // Header row
            Console.Write("    ");
            Console.ForegroundColor = ConsoleColor.White;
            for (var i = 0; i < headers.Length; i++)
            {
                Console.Write(Truncate(headers[i], widths[i]).PadRight(widths[i]));
                if (i < headers.Length - 1) Console.Write("  ");
            }
            Console.ResetColor();
            Console.WriteLine();

            // Separator
            Console.Write("    ");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            for (var i = 0; i < headers.Length; i++)
            {
                Console.Write(new string('-', widths[i]));
                if (i < headers.Length - 1) Console.Write("  ");
            }
            Console.ResetColor();
            Console.WriteLine();

            // Data rows
            foreach (var row in rows)
            {
                Console.Write("    ");
                for (var i = 0; i < headers.Length; i++)
                {
                    var val = i < row.Length ? (row[i] ?? "") : "";
                    Console.Write(Truncate(val, widths[i]).PadRight(widths[i]));
                    if (i < headers.Length - 1) Console.Write("  ");
                }
                Console.WriteLine();
            }
        }
    }

    /// <summary>
    /// Print a summary line (e.g., "Done in 12.3s").
    /// </summary>
    public static void Summary(string message)
    {
        if (OutputMode.IsJson) return;
        lock (Lock)
        {
            Console.WriteLine();
            Console.Write("  ");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(message);
            Console.ResetColor();
            Console.WriteLine();
        }
    }

    /// <summary>
    /// Start an indeterminate spinner for operations with no known total.
    /// Dispose the returned handle to stop and print a success line.
    /// </summary>
    public static Spinner StartSpinner(string label)
    {
        return new Spinner(label);
    }

    /// <summary>
    /// Start a progress bar for operations with a known total.
    /// Dispose the returned handle to auto-complete and print a success line.
    /// </summary>
    public static ProgressBar StartProgress(string label, int total, string unit = "items")
    {
        return new ProgressBar(label, total, unit);
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength) return value;
        return maxLength > 3 ? value[..(maxLength - 3)] + "..." : value[..maxLength];
    }
}

/// <summary>
/// Indeterminate spinner for long-running operations with no known step count.
/// Thread-safe. Degrades gracefully when output is redirected.
/// </summary>
public sealed class Spinner : IDisposable
{
    private static readonly char[] Frames = ['\u28FE', '\u28FD', '\u28FB', '\u28BF', '\u287F', '\u289F', '\u28AF', '\u28F7'];

    private string _label;
    private int _maxLabelLength;
    private readonly Timer? _timer;
    private int _frameIndex;
    private volatile bool _disposed;

    internal Spinner(string label)
    {
        _label = label;
        _maxLabelLength = label.Length;

        // MCP/JSON mode: no terminal output — stdout is the protocol channel
        if (OutputMode.IsJson) return;

        if (Console.IsOutputRedirected)
        {
            // Non-interactive: print the label once and return
            lock (ConsoleOutput.Lock)
            {
                Console.Write("  ");
                Console.WriteLine(label);
            }
            return;
        }

        // Print initial frame
        lock (ConsoleOutput.Lock)
        {
            Console.Write($"  {_label} {Frames[0]}");
        }

        _timer = new Timer(Tick, null, 100, 100);
    }

    /// <summary>
    /// Update the spinner label while it's running (e.g. for progress).
    /// </summary>
    public void Update(string label)
    {
        if (OutputMode.IsJson) return;

        lock (ConsoleOutput.Lock)
        {
            _label = label;
            if (label.Length > _maxLabelLength)
                _maxLabelLength = label.Length;
        }
    }

    private void Tick(object? state)
    {
        if (_disposed) return;

        _frameIndex = (_frameIndex + 1) % Frames.Length;

        lock (ConsoleOutput.Lock)
        {
            if (_disposed) return;
            // Pad to max length to clear any previous longer text
            var padded = _label.PadRight(_maxLabelLength);
            Console.Write($"\r  {padded} {Frames[_frameIndex]}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _timer?.Dispose();

        if (OutputMode.IsJson || Console.IsOutputRedirected) return;

        lock (ConsoleOutput.Lock)
        {
            // Clear the spinner line
            var clearWidth = _maxLabelLength + 6; // "  " + label + " " + spinner char + margin
            Console.Write($"\r{new string(' ', clearWidth)}\r");

            // Print success line
            Console.Write("  ");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("\u2713 ");
            Console.ResetColor();
            Console.WriteLine(_label);
        }
    }
}

/// <summary>
/// Determinate progress bar for operations with a known step count.
/// Thread-safe. Degrades gracefully when output is redirected.
/// </summary>
public sealed class ProgressBar : IDisposable
{
    private const int BarWidth = 20;

    private readonly string _label;
    private readonly int _total;
    private readonly string _unit;
    private int _current;
    private bool _disposed;

    internal ProgressBar(string label, int total, string unit)
    {
        _label = label;
        _total = Math.Max(total, 1); // Prevent divide-by-zero
        _unit = unit;

        // MCP/JSON mode: no terminal output — stdout is the protocol channel
        if (OutputMode.IsJson) return;

        if (Console.IsOutputRedirected)
        {
            Console.Write("  ");
            Console.WriteLine($"{label} (0/{_total} {unit})");
            return;
        }

        Render();
    }

    /// <summary>Advance the progress bar by the given count (default 1).</summary>
    public void Advance(int count = 1)
    {
        if (_disposed || OutputMode.IsJson) return;

        var newValue = Interlocked.Add(ref _current, count);
        if (newValue > _total) Interlocked.Exchange(ref _current, _total);

        if (!Console.IsOutputRedirected)
            Render();
    }

    private void Render()
    {
        lock (ConsoleOutput.Lock)
        {
            if (_disposed) return;

            var current = _current;
            var filled = (int)((double)current / _total * BarWidth);
            filled = Math.Clamp(filled, 0, BarWidth);
            var empty = BarWidth - filled;

            var bar = new string('\u2588', filled) + new string('\u2591', empty);
            var line = $"\r  {_label} [{bar}] {current}/{_total} {_unit}";

            Console.Write(line);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (OutputMode.IsJson || Console.IsOutputRedirected) return;

        lock (ConsoleOutput.Lock)
        {
            // Ensure bar shows as fully complete
            var bar = new string('\u2588', BarWidth);
            var completeLine = $"\r  {_label} [{bar}] {_total}/{_total} {_unit}";
            var clearWidth = completeLine.Length + 5;
            Console.Write($"\r{new string(' ', clearWidth)}\r");

            // Print success line
            Console.Write("  ");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("\u2713 ");
            Console.ResetColor();
            Console.WriteLine($"{_label} ({_total} {_unit})");
        }
    }
}
