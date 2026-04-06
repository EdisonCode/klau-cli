using System.CommandLine;
using System.CommandLine.Invocation;
using Klau.Cli.Auth;
using Klau.Cli.Domain;
using Klau.Cli.Output;

namespace Klau.Cli.Commands;

public static class StatusCommand
{
    public static Command Create()
    {
        var command = new Command("status", "Show current authentication and configuration.");

        command.SetHandler((InvocationContext ctx) =>
        {
            var apiKey = ctx.ParseResult.GetValueForOption(Program.ApiKeyOption);
            var tenantFlag = ctx.ParseResult.GetValueForOption(Program.TenantOption);
            var json = new CliJsonResponse("status");

            RunSync(apiKey, tenantFlag, json);

            ctx.ExitCode = ExitCodes.Success;
            json.Emit(ctx.ExitCode);
        });

        return command;
    }

    internal static void RunSync(string? apiKey, string? tenantFlag, CliJsonResponse json)
    {
        ConsoleOutput.Blank();
        ConsoleOutput.Header("Klau CLI status:");

        // API key resolution
        var envKey = Environment.GetEnvironmentVariable("KLAU_API_KEY");
        var stored = CredentialStore.Load();

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            ConsoleOutput.Success($"API key: {CredentialStore.Mask(apiKey)} (from --api-key flag)");
            json.Data["authenticated"] = true;
            json.Data["apiKeySource"] = "--api-key flag";
            json.Data["maskedKey"] = CredentialStore.Mask(apiKey);
        }
        else if (!string.IsNullOrWhiteSpace(envKey))
        {
            ConsoleOutput.Success($"API key: {CredentialStore.Mask(envKey)} (from KLAU_API_KEY env var)");
            json.Data["authenticated"] = true;
            json.Data["apiKeySource"] = "KLAU_API_KEY env var";
            json.Data["maskedKey"] = CredentialStore.Mask(envKey);
        }
        else if (stored is not null)
        {
            ConsoleOutput.Success($"API key: {CredentialStore.Mask(stored.ApiKey)} (from {CredentialStore.GetCredentialsPath()})");
            ConsoleOutput.Status($"Stored at: {stored.StoredAt:yyyy-MM-dd HH:mm:ss} UTC");
            json.Data["authenticated"] = true;
            json.Data["apiKeySource"] = CredentialStore.GetCredentialsPath();
            json.Data["maskedKey"] = CredentialStore.Mask(stored.ApiKey);
        }
        else
        {
            ConsoleOutput.Error("Not authenticated.");
            ConsoleOutput.Hint("Run: klau login");
            json.Data["authenticated"] = false;
        }

        // Tenant
        var resolvedTenant = CredentialStore.ResolveTenantId(tenantFlag);
        if (resolvedTenant is not null)
        {
            var tenantSource = !string.IsNullOrWhiteSpace(tenantFlag) ? "--tenant flag" : "stored credentials";
            ConsoleOutput.Success($"Tenant: {resolvedTenant} (from {tenantSource})");
            json.Data["tenant"] = resolvedTenant;
        }
        else
        {
            ConsoleOutput.Status("Tenant: none");
            json.Data["tenant"] = null;
        }

        // Config file location
        var configDir = CredentialStore.GetConfigDirectory();
        ConsoleOutput.Status($"Config: {configDir}");
        json.Data["configDir"] = configDir;

        ConsoleOutput.Blank();
    }
}
