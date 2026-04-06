using System.CommandLine;
using System.CommandLine.Invocation;
using Klau.Cli.Domain;

namespace Klau.Cli.Commands;

/// <summary>
/// klau completion — emit shell completion scripts.
/// Uses System.CommandLine's built-in [suggest] directive for dynamic completions.
/// </summary>
public static class CompletionCommand
{
    public static Command Create()
    {
        var shellArg = new Argument<string>(
            "shell",
            "Shell to generate completions for: bash, zsh, fish, or powershell.");

        var command = new Command("completion",
            "Generate shell completion scripts. Usage: eval \"$(klau completion zsh)\"")
        {
            shellArg,
        };

        command.SetHandler((InvocationContext ctx) =>
        {
            var shell = ctx.ParseResult.GetValueForArgument(shellArg).ToLowerInvariant();

            var script = shell switch
            {
                "bash" => BashScript,
                "zsh" => ZshScript,
                "fish" => FishScript,
                "powershell" or "pwsh" => PowerShellScript,
                _ => null,
            };

            if (script is null)
            {
                Console.Error.WriteLine($"Unknown shell: {shell}. Supported: bash, zsh, fish, powershell");
                ctx.ExitCode = ExitCodes.InputError;
                return;
            }

            Console.Write(script);
            ctx.ExitCode = ExitCodes.Success;
        });

        return command;
    }

    // All scripts call `klau [suggest:N] "klau <partial>"` for dynamic completions.
    // This uses System.CommandLine's built-in suggest directive.

    private const string BashScript = """
        _klau_completions() {
            local cur="${COMP_WORDS[*]}"
            local pos=$COMP_POINT
            local suggestions
            suggestions=$(klau "[suggest:$pos]" "$cur" 2>/dev/null)
            COMPREPLY=($(compgen -W "$suggestions" -- "${COMP_WORDS[$COMP_CWORD]}"))
        }
        complete -F _klau_completions klau
        """;

    private const string ZshScript = """
        _klau() {
            local -a completions
            local cur="${words[*]}"
            local pos=$CURSOR
            completions=("${(@f)$(klau "[suggest:$pos]" "$cur" 2>/dev/null)}")
            compadd -a completions
        }
        compdef _klau klau
        """;

    private const string FishScript = """
        complete -c klau -f -a '(commandline -cp | xargs klau "[suggest:(commandline -C)]" 2>/dev/null)'
        """;

    private const string PowerShellScript = """
        Register-ArgumentCompleter -CommandName klau -ScriptBlock {
            param($wordToComplete, $commandAst, $cursorPosition)
            $suggestions = klau "[suggest:$cursorPosition]" "$commandAst" 2>$null
            $suggestions -split "`n" | Where-Object { $_ -like "$wordToComplete*" } | ForEach-Object {
                [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)
            }
        }
        """;
}
