using System.CommandLine;
using System.CommandLine.Invocation;
using Klau.Cli.Auth;
using Klau.Cli.Domain;
using Klau.Cli.Output;
using Klau.Sdk;

namespace Klau.Cli.Commands;

/// <summary>
/// klau tenants — manage multi-tenant (division) context.
///
///   klau tenants list    — list all divisions
///   klau tenants use id  — set default tenant in stored credentials
///   klau tenants clear   — clear stored tenant
/// </summary>
public static class TenantCommands
{
    public static Command Create()
    {
        var tenantsCommand = new Command("tenants",
            "Manage multi-tenant (division) context for enterprise accounts.");

        tenantsCommand.AddCommand(CreateListCommand());
        tenantsCommand.AddCommand(CreateUseCommand());
        tenantsCommand.AddCommand(CreateClearCommand());

        return tenantsCommand;
    }

    private static Command CreateListCommand()
    {
        var command = new Command("list", "List all divisions under this account.");

        command.SetHandler(async (InvocationContext ctx) =>
        {
            var apiKey = ctx.ParseResult.GetValueForOption(Program.ApiKeyOption);
            ctx.ExitCode = await ListAsync(apiKey);
        });

        return command;
    }

    private static Command CreateUseCommand()
    {
        var idArg = new Argument<string>("id", "Division/tenant ID to set as default.");

        var command = new Command("use", "Set the default tenant for subsequent commands.")
        {
            idArg,
        };

        command.SetHandler((InvocationContext ctx) =>
        {
            var id = ctx.ParseResult.GetValueForArgument(idArg);
            ctx.ExitCode = Use(id);
        });

        return command;
    }

    private static Command CreateClearCommand()
    {
        var command = new Command("clear", "Clear the stored default tenant.");

        command.SetHandler((InvocationContext ctx) =>
        {
            ctx.ExitCode = Clear();
        });

        return command;
    }

    private static async Task<int> ListAsync(string? apiKeyFlag)
    {
        var apiKey = CredentialStore.ResolveApiKey(apiKeyFlag);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            ConsoleOutput.Error("No API key found.");
            ConsoleOutput.Hint("Run: klau login");
            return ExitCodes.ConfigError;
        }

        try
        {
            using var client = new KlauClient(apiKey);
            var divisions = await client.Divisions.ListAsync();

            if (divisions.Count == 0)
            {
                ConsoleOutput.Blank();
                ConsoleOutput.Status("No divisions found.");
                ConsoleOutput.Hint("This account may not be an enterprise/parent account.");
                ConsoleOutput.Blank();
                return ExitCodes.Success;
            }

            ConsoleOutput.Blank();
            ConsoleOutput.Header($"Divisions ({divisions.Count}):");

            var headers = new[] { "ID", "Name", "Drivers", "Jobs" };
            var rows = divisions.Select(d => new[]
            {
                d.Id,
                d.Name,
                d.DriverCount.ToString(),
                d.JobCount.ToString(),
            }).ToList();

            ConsoleOutput.Table(headers, rows);
            ConsoleOutput.Blank();
            ConsoleOutput.Hint("Set a default tenant: klau tenants use <id>");
            ConsoleOutput.Blank();
            return ExitCodes.Success;
        }
        catch (ArgumentException)
        {
            ConsoleOutput.Error("Invalid API key format.");
            ConsoleOutput.Hint("API keys must start with kl_live_");
            return ExitCodes.ConfigError;
        }
        catch (Exception ex)
        {
            ConsoleOutput.Error($"Failed to list divisions: {ex.Message}");
            return ExitCodes.ApiError;
        }
    }

    private static int Use(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            ConsoleOutput.Error("Tenant ID is required.");
            ConsoleOutput.Hint("Usage: klau tenants use <id>");
            return ExitCodes.InputError;
        }

        var existing = CredentialStore.Load();
        if (existing is null)
        {
            ConsoleOutput.Error("No stored credentials found.");
            ConsoleOutput.Hint("Run: klau login");
            return ExitCodes.ConfigError;
        }

        CredentialStore.Save(existing with { TenantId = id, StoredAt = DateTime.UtcNow });

        ConsoleOutput.Blank();
        ConsoleOutput.Success($"Default tenant set to: {id}");
        ConsoleOutput.Hint("All commands will now target this tenant unless --tenant is used.");
        ConsoleOutput.Blank();
        return ExitCodes.Success;
    }

    private static int Clear()
    {
        var existing = CredentialStore.Load();
        if (existing is null)
        {
            ConsoleOutput.Error("No stored credentials found.");
            ConsoleOutput.Hint("Run: klau login");
            return ExitCodes.ConfigError;
        }

        if (existing.TenantId is null)
        {
            ConsoleOutput.Blank();
            ConsoleOutput.Status("No default tenant is set.");
            ConsoleOutput.Blank();
            return ExitCodes.Success;
        }

        CredentialStore.Save(existing with { TenantId = null, StoredAt = DateTime.UtcNow });

        ConsoleOutput.Blank();
        ConsoleOutput.Success("Default tenant cleared.");
        ConsoleOutput.Blank();
        return ExitCodes.Success;
    }
}
