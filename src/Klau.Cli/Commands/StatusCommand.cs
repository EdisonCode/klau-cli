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
            ConsoleOutput.Blank();
            ConsoleOutput.Header("Klau CLI status:");

            // API key resolution
            var cliKey = ctx.ParseResult.GetValueForOption(Program.ApiKeyOption);
            var envKey = Environment.GetEnvironmentVariable("KLAU_API_KEY");
            var stored = CredentialStore.Load();

            if (!string.IsNullOrWhiteSpace(cliKey))
            {
                ConsoleOutput.Success($"API key: {CredentialStore.Mask(cliKey)} (from --api-key flag)");
            }
            else if (!string.IsNullOrWhiteSpace(envKey))
            {
                ConsoleOutput.Success($"API key: {CredentialStore.Mask(envKey)} (from KLAU_API_KEY env var)");
            }
            else if (stored is not null)
            {
                ConsoleOutput.Success($"API key: {CredentialStore.Mask(stored.ApiKey)} (from {CredentialStore.GetCredentialsPath()})");
                ConsoleOutput.Status($"Stored at: {stored.StoredAt:yyyy-MM-dd HH:mm:ss} UTC");
            }
            else
            {
                ConsoleOutput.Error("Not authenticated.");
                ConsoleOutput.Hint("Run: klau login");
            }

            // Tenant
            var tenantFlag = ctx.ParseResult.GetValueForOption(Program.TenantOption);
            var resolvedTenant = CredentialStore.ResolveTenantId(tenantFlag);
            if (resolvedTenant is not null)
            {
                var tenantSource = !string.IsNullOrWhiteSpace(tenantFlag) ? "--tenant flag" : "stored credentials";
                ConsoleOutput.Success($"Tenant: {resolvedTenant} (from {tenantSource})");
            }
            else
            {
                ConsoleOutput.Status("Tenant: none");
            }

            // Config file location
            ConsoleOutput.Status($"Config: {CredentialStore.GetConfigDirectory()}");

            ConsoleOutput.Blank();
            ctx.ExitCode = ExitCodes.Success;
        });

        return command;
    }
}
