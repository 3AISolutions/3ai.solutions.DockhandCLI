using DockhandCLI.Core.Models;
using DockhandCLI.Core.Services;
using DockhandCLI.Presentation;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DockhandCLI.Hosting;

/// <summary>Turns exceptions into a stderr message and an exit code.</summary>
internal static class ErrorRenderer
{
    public static int Render(CliConsole console, Exception exception, bool verbose)
    {
        switch (exception)
        {
            case DockhandException ex:
                WriteError(console, ex.Message);
                if (verbose && ex.InnerException is { } inner)
                {
                    console.Error.MarkupLine($"[grey]{Markup.Escape(SecretRedactor.Redact(inner.ToString()))}[/]");
                }

                return ex.ExitCode;

            case CommandAppException ex:
                if (ex.Pretty is { } pretty)
                {
                    console.Error.Write(pretty);
                }
                else
                {
                    WriteError(console, ex.Message);
                }

                return ExitCodes.Usage;

            case OperationCanceledException:
                console.Error.MarkupLine("[yellow]Cancelled.[/]");
                return ExitCodes.Cancelled;

            default:
                WriteError(console, $"Unexpected {exception.GetType().Name}: {exception.Message}");
                if (verbose)
                {
                    console.Error.MarkupLine($"[grey]{Markup.Escape(SecretRedactor.Redact(exception.ToString()))}[/]");
                }
                else
                {
                    console.Error.MarkupLine("[grey]Run with --verbose for details.[/]");
                }

                return ExitCodes.Error;
        }
    }

    private static void WriteError(CliConsole console, string message) =>
        console.Error.MarkupLine($"[red]Error:[/] {Markup.Escape(SecretRedactor.Redact(message))}");
}
