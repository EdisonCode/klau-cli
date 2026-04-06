namespace Klau.Cli.Output;

/// <summary>
/// Global output mode flags. Set once at startup, read everywhere.
/// </summary>
public static class OutputMode
{
    /// <summary>True when --output json is set. Suppresses human output; commands emit JSON at end.</summary>
    public static bool IsJson { get; set; }

    /// <summary>True when --yes / -y is set. Auto-accepts all prompts.</summary>
    public static bool AutoAccept { get; set; }

    /// <summary>True when --no-color is set or NO_COLOR env var is present. Suppresses ANSI color codes.</summary>
    public static bool NoColor { get; set; }

    /// <summary>True when --verbose is set. Enables diagnostic output to stderr.</summary>
    public static bool Verbose { get; set; }

    /// <summary>True when the CLI should not prompt or use terminal features.</summary>
    public static bool IsNonInteractive =>
        IsJson || AutoAccept || Console.IsInputRedirected || Console.IsOutputRedirected;
}
