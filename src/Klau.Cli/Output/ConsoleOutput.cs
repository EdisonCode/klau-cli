namespace Klau.Cli.Output;

/// <summary>
/// Structured console output helpers for clean, readable CLI feedback.
/// </summary>
public static class ConsoleOutput
{
    private static readonly object Lock = new();

    /// <summary>
    /// Print a status/progress message in default color.
    /// </summary>
    public static void Status(string message)
    {
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
    /// Print a blank line for visual separation.
    /// </summary>
    public static void Blank()
    {
        Console.WriteLine();
    }

    /// <summary>
    /// Print a section header.
    /// </summary>
    public static void Header(string message)
    {
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

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength) return value;
        return maxLength > 3 ? value[..(maxLength - 3)] + "..." : value[..maxLength];
    }
}
