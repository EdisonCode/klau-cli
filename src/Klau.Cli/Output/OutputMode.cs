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

    /// <summary>True when the CLI should not prompt or use terminal features.</summary>
    public static bool IsNonInteractive =>
        IsJson || AutoAccept || Console.IsInputRedirected || Console.IsOutputRedirected;
}
