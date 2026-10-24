using DockhandCLI.Commands;
using DockhandCLI.Core.Interfaces;
using DockhandCLI.Core.Models;
using DockhandCLI.Core.Services;
using DockhandCLI.Hosting;
using DockhandCLI.Presentation;
using Spectre.Console;

namespace DockhandCLI.Shell;

internal interface ILineReaderFactory
{
    ILineReader Create(CliConsole console, ShellCompleter completer, Action<string> writePrompt);
}

internal sealed class ConsoleLineReaderFactory : ILineReaderFactory
{
    public ILineReader Create(CliConsole console, ShellCompleter completer, Action<string> writePrompt) =>
        console.CanPrompt ? new LineEditor(completer, writePrompt) : new StreamLineReader(console.Input);
}

/// <summary>
/// The interactive shell started when <c>dockhand</c> runs without a command. Lines are dispatched
/// through <see cref="CliApplication.ExecuteAsync"/>, the same path as direct execution.
/// </summary>
internal sealed class InteractiveShell(
    CliApplication app,
    InvocationContext context,
    IConsoleFactory consoleFactory,
    IConfigStore store,
    ILineReaderFactory readerFactory)
{
    private readonly List<string> _history = [];
    private CliConsole _console = CliConsole.Null;
    private CliConfiguration _config = new();

    public async Task<int> RunAsync(GlobalOptions initialOptions, CancellationToken cancellationToken)
    {
        context.InShell = true;
        context.SessionEndpoint = initialOptions.Server;
        _console = consoleFactory.Create(initialOptions);
        context.Console = _console;
        await ReloadConfigAsync(cancellationToken);

        PrintBanner();

        var completer = new ShellCompleter(() => _config.Endpoints.Select(e => e.Alias).ToList());
        var reader = readerFactory.Create(_console, completer, prompt => _console.Output.Markup(prompt));
        var exitCode = ExitCodes.Success;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await ReloadConfigAsync(cancellationToken);
                var line = reader.ReadLine(Prompt());
                if (line is null)
                {
                    break;
                }

                IReadOnlyList<string> tokens;
                try
                {
                    tokens = CommandLineTokenizer.Tokenize(line);
                }
                catch (FormatException ex)
                {
                    ErrorRenderer.WriteError(_console, ex.Message, inShell: true);
                    exitCode = ExitCodes.Usage;
                    continue;
                }

                if (tokens.Count == 0)
                {
                    continue;
                }

                _history.Add(line);
                var args = tokens.ToList();
                args[0] = args[0].TrimStart('/');
                if (args[0].Length == 0)
                {
                    args.RemoveAt(0);
                    if (args.Count == 0)
                    {
                        ShowCommands();
                        continue;
                    }
                }

                switch (args[0].ToLowerInvariant())
                {
                    case "exit" or "quit":
                        return exitCode;
                    case "command" or "commands" or "help" or "?":
                        exitCode = args.Count == 1 ? ShowCommands() : await RunCommandAsync([.. args.Skip(1), "--help"], cancellationToken);
                        break;
                    case "use":
                        exitCode = Use(args);
                        break;
                    case "history":
                        for (var i = 0; i < _history.Count; i++)
                        {
                            _console.Output.MarkupLine($"[grey]{i + 1,4}[/]  {Markup.Escape(_history[i])}");
                        }

                        exitCode = ExitCodes.Success;
                        break;
                    case "clear" or "cls":
                        _console.Output.Clear();
                        break;
                    default:
                        exitCode = await RunCommandAsync(args, cancellationToken);
                        break;
                }
            }
        }
        finally
        {
            context.InShell = false;
            context.SessionEndpoint = null;
        }

        return exitCode;
    }

    /// <summary>Runs one command; Ctrl+C cancels only this command, not the shell.</summary>
    private async Task<int> RunCommandAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        using var commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            e.Cancel = true;
            commandCancellation.Cancel();
        };

        Console.CancelKeyPress += handler;
        try
        {
            var result = await app.ExecuteAsync(args, commandCancellation.Token);
            context.Console = _console;
            return result;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    private int Use(List<string> args)
    {
        if (args.Count == 1)
        {
            _console.Output.MarkupLine($"Active endpoint: [yellow]{Markup.Escape(ActiveAlias() ?? "(none)")}[/]");
            return ExitCodes.Success;
        }

        if (args.Count != 2)
        {
            ErrorRenderer.WriteError(_console, "Usage: use <alias>");
            return ExitCodes.Usage;
        }

        var alias = args[1].TrimStart('-');
        var endpoint = _config.FindEndpoint(alias);
        if (endpoint is null)
        {
            ErrorRenderer.WriteError(_console, EndpointService.UnknownAlias(_config, alias).Message, inShell: true);
            return ExitCodes.NotFound;
        }

        context.SessionEndpoint = endpoint.Alias;
        _console.Output.MarkupLine($"Active endpoint: [yellow]{Markup.Escape(endpoint.Alias)}[/] [grey]({Markup.Escape(endpoint.BaseUrl)}; the saved default is unchanged)[/]");
        return ExitCodes.Success;
    }

    private int ShowCommands()
    {
        var table = new Table().Border(TableBorder.Simple).BorderColor(Color.Grey).HideHeaders()
            .AddColumn("Command").AddColumn("Description");

        foreach (var group in CommandCatalog.Entries.GroupBy(e => e.Words[0]))
        {
            foreach (var entry in group)
            {
                table.AddRow($"[green]/{Markup.Escape(entry.Path)}[/] [grey]{Markup.Escape(entry.Arguments)}[/]", Markup.Escape(entry.Description));
            }

            table.AddEmptyRow();
        }

        foreach (var entry in CommandCatalog.ShellCommands)
        {
            table.AddRow($"[blue]/{Markup.Escape(entry.Path)}[/] [grey]{Markup.Escape(entry.Arguments)}[/]", Markup.Escape(entry.Description));
        }

        _console.Output.Write(table);
        _console.Output.MarkupLine("[grey]The leading / is optional. Add -<alias> or --server <alias> to target another endpoint for one command,[/]");
        _console.Output.MarkupLine("[grey]--env <id> for another environment, and --output json for JSON. /command <name> shows details, e.g. /command check updates.[/]");
        return ExitCodes.Success;
    }

    private void PrintBanner()
    {
        var lines = new List<string> { $"[cyan]✻[/] [bold]Dockhand CLI[/] [grey]v{Markup.Escape(CliApplication.Version)}[/]", "" };
        if (_config.Endpoints.Count == 0)
        {
            lines.Add("No endpoints yet. Add one with [green]/endpoint add <alias> <url>[/]");
        }
        else
        {
            lines.Add($"[grey]endpoint:[/] [yellow]{Markup.Escape(ActiveAlias() ?? "(none)")}[/]");
            if (context.SessionEndpoint is { } active && !string.Equals(active, _config.DefaultEndpoint, StringComparison.OrdinalIgnoreCase))
            {
                lines.Add($"[grey]default:[/]  {Markup.Escape(_config.DefaultEndpoint ?? "(none)")}");
            }
        }

        lines.Add("");
        lines.Add("[grey]/ for commands · ↑↓ history · Tab completes · Ctrl+D exits[/]");

        _console.Output.Write(new Panel(new Markup(string.Join('\n', lines)))
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey)
            .Padding(1, 0));
        _console.Output.WriteLine();
    }

    private string Prompt()
    {
        var alias = ActiveAlias();
        return alias is null
            ? "[blue]dockhand[/] > "
            : $"[blue]dockhand[/] [[[yellow]{Markup.Escape(alias)}[/]]] > ";
    }

    private string? ActiveAlias()
    {
        if (context.SessionEndpoint is { } session && _config.FindEndpoint(session) is { } endpoint)
        {
            return endpoint.Alias;
        }

        return _config.DefaultEndpoint ?? (_config.Endpoints.Count == 1 ? _config.Endpoints[0].Alias : null);
    }

    private async Task ReloadConfigAsync(CancellationToken cancellationToken)
    {
        try
        {
            _config = await store.LoadAsync(cancellationToken);
        }
        catch (DockhandException ex)
        {
            ErrorRenderer.WriteError(_console, ex.Message, inShell: true);
        }
    }
}
