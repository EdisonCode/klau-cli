namespace Klau.Cli.Mcp;

/// <summary>
/// Immutable credentials and shared resources for the MCP server session.
/// Registered as a singleton in DI at startup, injected into tool handlers.
/// </summary>
public sealed record McpCredentials(string ApiKey, string? TenantId, HttpClient HttpClient);
