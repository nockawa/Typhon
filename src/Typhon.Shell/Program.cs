using System;
using System.IO;
using System.Threading;
using PrettyPrompt;
using PrettyPrompt.Highlighting;
using Spectre.Console;
using Spectre.Console.Cli;
using Typhon.Shell.Commands;
using Typhon.Shell.Parsing;
using Typhon.Shell.Session;

namespace Typhon.Shell;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            var app = new CommandApp<TSHCommand>();
            app.Configure(config =>
            {
                config.SetApplicationName("tsh");
                config.SetApplicationVersion("0.1.0");
            });

            return app.Run(args);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Fatal: {Markup.Escape(ex.Message)}[/]");
            return 10;
        }
    }
}

/// <summary>
/// The single Spectre.Console.Cli command that handles all tsh invocation modes:
/// interactive REPL, single-command (-c), script (--exec), and pipe mode.
/// </summary>
internal sealed class TSHCommand : Command<TSHCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[database]")]
        public string Database { get; set; }

        [CommandOption("-s|--schema")]
        public string[] Schema { get; set; }

        [CommandOption("-e|--exec")]
        public string ExecScript { get; set; }

        [CommandOption("-c")]
        public string SingleCommand { get; set; }

        [CommandOption("-f|--format")]
        public string Format { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        using var session = new ShellSession();

        // Apply format if specified
        if (!string.IsNullOrEmpty(settings.Format))
        {
            session.Format = settings.Format;
        }

        var executor = new CommandExecutor(session);

        // Pre-load schema assemblies
        if (settings.Schema != null)
        {
            foreach (var schemaPath in settings.Schema)
            {
                // Escape backslashes so the tokenizer doesn't treat them as C-style escapes
                var escaped = schemaPath.Replace("\\", "\\\\");
                var result = executor.Execute($"load-schema \"{escaped}\"");
                WriteResult(result);

                if (!result.Success)
                {
                    return 1;
                }
            }
        }

        // Pre-open database
        if (!string.IsNullOrEmpty(settings.Database))
        {
            var escaped = settings.Database.Replace("\\", "\\\\");
            var result = executor.Execute($"open \"{escaped}\"");
            WriteResult(result);

            if (!result.Success)
            {
                return 2;
            }
        }

        // Execute .tshrc files: global (~/.tshrc) first, then local (./.tshrc)
        var rcFiles = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tshrc"),
            Path.Combine(Directory.GetCurrentDirectory(), ".tshrc"),
        };

        foreach (var rcFile in rcFiles)
        {
            if (File.Exists(rcFile))
            {
                AnsiConsole.MarkupLine($"[grey]Loading {Markup.Escape(rcFile)}[/]");
                var parser = new ScriptParser(session, executor);
                var (success, error) = parser.ExecuteFile(rcFile);

                if (!success)
                {
                    AnsiConsole.MarkupLine($"[yellow]Warning: .tshrc failed: {Markup.Escape(error)}[/]");
                    AnsiConsole.MarkupLine("[yellow](continuing anyway)[/]");
                }
            }
        }

        // Mode precedence: -c → --exec → pipe → interactive
        if (!string.IsNullOrEmpty(settings.SingleCommand))
        {
            return ExecuteSingleCommand(session, executor, settings.SingleCommand);
        }

        if (!string.IsNullOrEmpty(settings.ExecScript))
        {
            return ExecuteScript(session, executor, settings.ExecScript);
        }

        if (!Console.IsInputRedirected)
        {
            return RunInteractive(session, executor);
        }

        return ExecutePipeMode(session, executor);
    }

    private static int ExecuteSingleCommand(ShellSession session, CommandExecutor executor, string command)
    {
        var result = executor.Execute(command);
        WriteResult(result);
        return result.Success ? 0 : 1;
    }

    private static int ExecuteScript(ShellSession session, CommandExecutor executor, string scriptPath)
    {
        var parser = new ScriptParser(session, executor);
        var (success, error) = parser.ExecuteFile(scriptPath);

        if (!success)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(error)}[/]");
            AnsiConsole.MarkupLine("[red](script halted)[/]");
            return 1;
        }

        return 0;
    }

    private static int ExecutePipeMode(ShellSession session, CommandExecutor executor)
    {
        var lines = Console.In.ReadToEnd().Split('\n');
        var parser = new ScriptParser(session, executor);
        var (success, error) = parser.ExecuteLines(lines);

        if (!success)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(error)}[/]");
            return 1;
        }

        return 0;
    }

    private static int RunInteractive(ShellSession session, CommandExecutor executor)
    {
        AnsiConsole.MarkupLine("[grey]Typhon Shell v0.1.0[/]");
        AnsiConsole.MarkupLine("[grey]Type 'help' for commands, 'exit' to quit.[/]");
        Console.WriteLine();

        var callbacks = new TshPromptCallbacks(session);

        var historyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".tsh_history");

        var config = new PromptConfiguration(
            prompt: new FormattedString("tsh> ",
                new FormatSpan(0, 3, AnsiColor.BrightCyan))
        );

        var prompt = new Prompt(
            persistentHistoryFilepath: historyPath,
            callbacks: callbacks,
            configuration: config
        );

        try
        {
            while (true)
            {
                // Update prompt to reflect current session state (db name, transaction, dirty flag)
                var promptText = PromptBuilder.Build(session);
                config.Prompt = new FormattedString(promptText,
                    new FormatSpan(0, 3, AnsiColor.BrightCyan));

                var response = prompt.ReadLineAsync().GetAwaiter().GetResult();
                if (!response.IsSuccess)
                {
                    // Ctrl+C on empty line or Ctrl+D
                    continue;
                }

                var input = response.Text.Trim();
                if (string.IsNullOrEmpty(input))
                {
                    continue;
                }

                var result = executor.Execute(input);
                WriteResult(result);

                if (result.ShouldExit)
                {
                    if (session.HasTransaction && session.IsDirty)
                    {
                        AnsiConsole.MarkupLine("[yellow]Warning: Active transaction with uncommitted changes.[/]");
                    }

                    break;
                }
            }
        }
        finally
        {
            prompt.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        return 0;
    }

    private static void WriteResult(CommandResult result)
    {
        if (string.IsNullOrEmpty(result.Output))
        {
            return;
        }

        if (!result.Success)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(result.Output)}[/]");
        }
        else if (result.UseMarkup)
        {
            AnsiConsole.MarkupLine(result.Output);
        }
        else
        {
            Console.WriteLine(result.Output);
        }
    }
}
