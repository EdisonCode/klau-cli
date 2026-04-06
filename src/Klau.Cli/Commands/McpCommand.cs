using System.CommandLine;
using System.CommandLine.Invocation;
using Klau.Cli.Auth;
using Klau.Cli.Domain;
using Klau.Cli.Mcp;
using Klau.Cli.Output;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Klau.Cli.Commands;

/// <summary>
/// klau mcp — run as an MCP server over stdio.
/// Exposes import, optimize, doctor, and status as MCP tools
/// that AI agents can call directly without shell parsing.
/// </summary>
public static class McpCommand
{
    public static Command Create()
    {
        var command = new Command("mcp",
            "Run as an MCP server over stdio. Exposes CLI tools for AI agents.");

        command.SetHandler(async (InvocationContext ctx) =>
        {
            var apiKey = ctx.ParseResult.GetValueForOption(Program.ApiKeyOption);
            var tenantFlag = ctx.ParseResult.GetValueForOption(Program.TenantOption);

            // Force JSON mode — MCP tools return structured data, stdout is the protocol channel
            OutputMode.IsJson = true;

            // Resolve credentials for the session
            var resolvedKey = CredentialStore.ResolveApiKey(apiKey);
            if (string.IsNullOrWhiteSpace(resolvedKey))
            {
                ConsoleOutput.Error("No API key found. MCP server requires authentication.");
                ConsoleOutput.Hint("Run: klau login, or pass --api-key");
                ctx.ExitCode = ExitCodes.ConfigError;
                return;
            }

            var tenantId = CredentialStore.ResolveTenantId(tenantFlag);

            // Shared HttpClient for the lifetime of the MCP server — avoids socket
            // exhaustion from creating/disposing HttpClient per tool call.
            var sharedHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };

            var credentials = new McpCredentials(resolvedKey, tenantId, sharedHttpClient);

            var builder = Host.CreateApplicationBuilder([]);

            builder.Services.AddSingleton(credentials);

            builder.Services.AddMcpServer(options =>
            {
                options.ServerInfo = new()
                {
                    Name = "klau",
                    Version = typeof(McpCommand).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                };
            })
            .WithStdioServerTransport()
            .WithTools<KlauMcpTools>();

            // Log to stderr only — stdout is the MCP protocol channel
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Warning);

            await builder.Build().RunAsync();

            sharedHttpClient.Dispose();
            ctx.ExitCode = ExitCodes.Success;
        });

        return command;
    }
}
