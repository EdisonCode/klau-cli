using System.CommandLine;
using System.CommandLine.Invocation;
using Klau.Cli.Auth;
using Klau.Cli.Domain;
using Klau.Cli.Output;

namespace Klau.Cli.Commands;

public static class LogoutCommand
{
    public static Command Create()
    {
        var command = new Command("logout", "Remove stored credentials.");

        command.SetHandler((InvocationContext ctx) =>
        {
            var json = new CliJsonResponse("logout");
            if (CredentialStore.Delete())
            {
                ConsoleOutput.Success("Credentials removed.");
            }
            else
            {
                ConsoleOutput.Status("No stored credentials found.");
            }
            ctx.ExitCode = ExitCodes.Success;
            json.Emit(ctx.ExitCode);
        });

        return command;
    }
}
