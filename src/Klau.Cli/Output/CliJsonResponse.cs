using System.Text.Json;
using System.Text.Json.Serialization;
using Klau.Cli.Domain;

namespace Klau.Cli.Output;

/// <summary>
/// Consistent JSON envelope for all CLI commands when --output json is active.
/// Every command emits exactly one of these to stdout.
///
/// Schema:
/// {
///   "command": "import",
///   "status": "success" | "error" | "partial_failure",
///   "exitCode": 0,
///   "data": { ... } | null,
///   "error": { "code": "...", "message": "...", "hint": "..." } | null
/// }
/// </summary>
public sealed class CliJsonResponse
{
    public string Command { get; }
    public Dictionary<string, object?> Data { get; } = new();
    public CliJsonError? Error { get; private set; }

    public CliJsonResponse(string command) => Command = command;

    public void SetError(string code, string message, string? hint = null) =>
        Error = new CliJsonError(code, message, hint);

    /// <summary>
    /// Emit the JSON envelope to stdout. Call once at the end of the CLI command.
    /// </summary>
    public void Emit(int exitCode)
    {
        if (!OutputMode.IsJson) return;
        Console.WriteLine(ToJsonString(exitCode));
    }

    /// <summary>
    /// Serialize the JSON envelope to a string. Used by MCP tool handlers
    /// to return structured results without writing to stdout.
    /// </summary>
    public string ToJsonString(int exitCode)
    {
        var status = exitCode switch
        {
            ExitCodes.Success => "success",
            ExitCodes.PartialFailure => "partial_failure",
            _ => "error",
        };

        var envelope = new Dictionary<string, object?>
        {
            ["command"] = Command,
            ["status"] = status,
            ["exitCode"] = exitCode,
            ["data"] = Data.Count > 0 ? Data : null,
            ["error"] = Error,
        };

        return JsonSerializer.Serialize(envelope, CliJsonContext.Default.DictionaryStringObject);
    }
}

public sealed record CliJsonError(string Code, string Message, string? Hint);

[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(CliJsonError))]
[JsonSerializable(typeof(List<string>))]
internal partial class CliJsonContext : JsonSerializerContext { }
