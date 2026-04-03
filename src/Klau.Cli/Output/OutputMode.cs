namespace Klau.Cli.Output;

/// <summary>
/// Global output mode. When JSON, all structured output goes to stdout as JSON.
/// Human-readable formatting is suppressed.
/// </summary>
public static class OutputMode
{
    public static bool IsJson { get; set; }

    /// <summary>
    /// Whether interactive prompts are available (not piped, not JSON mode).
    /// </summary>
    public static bool IsInteractive =>
        !IsJson && !Console.IsInputRedirected && !Console.IsOutputRedirected;
}
