using System.Text;
using System.Text.RegularExpressions;
using DockhandCLI.Core.Models;
using DockhandCLI.Core.Services;
using DockhandCLI.Presentation;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DockhandCLI.Hosting;

/// <summary>
/// Turns exceptions into a stderr message and an exit code. A message is shown as a one-line headline
/// (its first sentence) followed by any further sentences as dimmed hint lines.
/// </summary>
internal static partial class ErrorRenderer
{
    public static int Render(CliConsole console, Exception exception, bool verbose, bool inShell = false)
    {
        switch (exception)
        {
            case DockhandException ex:
                WriteError(console, ex.Message, inShell);
                if (verbose && ex.InnerException is { } inner)
                {
                    WriteDetail(console, SecretRedactor.Redact(inner.ToString()));
                }

                return ex.ExitCode;

            case CommandAppException ex:
                if (ex.Pretty is { } pretty)
                {
                    WritePretty(console, pretty, ex.Message);
                }
                else
                {
                    WriteError(console, ex.Message, inShell);
                }

                return ExitCodes.Usage;

            case OperationCanceledException:
                console.Error.MarkupLine("[yellow]Cancelled[/]");
                return ExitCodes.Cancelled;

            default:
                WriteError(console, $"Unexpected {exception.GetType().Name}: {exception.Message}", inShell);
                WriteDetail(console, verbose ? SecretRedactor.Redact(exception.ToString()) : "Run with --verbose for details.");
                return ExitCodes.Error;
        }
    }

    /// <summary>Writes an error message: the first sentence as the headline, the rest as hints.</summary>
    public static void WriteError(CliConsole console, string message, bool inShell = false)
    {
        message = SecretRedactor.Redact(message);
        if (inShell)
        {
            // Hints name direct-mode commands; in the shell the same command is typed as /command.
            message = DirectCommandHint().Replace(message, "'/");
        }

        var sentences = SplitSentences(message);
        console.Error.MarkupLine($"{Marker(console)}[red]{Markup.Escape(sentences[0].TrimEnd('.'))}[/]");
        foreach (var hint in sentences.Skip(1))
        {
            WriteDetail(console, hint);
        }
    }

    private static void WriteDetail(CliConsole console, string text)
    {
        foreach (var line in text.Split('\n'))
        {
            console.Error.MarkupLine($"  [grey]{Markup.Escape(line.TrimEnd('\r'))}[/]");
        }
    }

    /// <summary>
    /// Spectre's parse errors come with blank lines and deep indentation around the caret marker.
    /// Renders them compactly: headline, the offending command line, and the marker with its suggestion.
    /// </summary>
    private static void WritePretty(CliConsole console, Spectre.Console.Rendering.IRenderable pretty, string fallback)
    {
        var writer = new StringWriter();
        var capture = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(writer),
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
        });
        capture.Profile.Width = 500;
        capture.Write(pretty);

        var lines = writer.ToString().Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0)
        {
            WriteError(console, fallback);
            return;
        }

        var headline = lines[0].StartsWith("Error:", StringComparison.Ordinal) ? lines[0][6..].Trim() : lines[0];
        console.Error.MarkupLine($"{Marker(console)}[red]{Markup.Escape(headline.TrimEnd('.'))}[/]");

        var body = lines.Skip(1).ToList();
        var indent = body.Count == 0 ? 0 : body.Min(l => l.Length - l.TrimStart().Length);
        foreach (var line in body)
        {
            var text = Markup.Escape(line[indent..]);
            console.Error.MarkupLine(line.TrimStart().StartsWith('^') ? $"  [yellow]{text}[/]" : $"  [grey]{text}[/]");
        }
    }

    private static string Marker(CliConsole console) =>
        console.Error.Profile.Capabilities.Ansi ? "[red]✗[/] " : "[red]Error:[/] ";

    /// <summary>Splits at sentence ends: ". " before a capital letter, outside parentheses and quotes.</summary>
    internal static List<string> SplitSentences(string message)
    {
        var sentences = new List<string>();
        var current = new StringBuilder();
        var depth = 0;
        var quoted = false;

        for (var i = 0; i < message.Length; i++)
        {
            var c = message[i];
            current.Append(c);
            switch (c)
            {
                case '(':
                    depth++;
                    break;
                case ')' when depth > 0:
                    depth--;
                    break;
                case '\'' when IsQuoteBoundary(message, i):
                    quoted = !quoted;
                    break;
                case '.' when depth == 0 && !quoted && i + 2 < message.Length && message[i + 1] == ' ' && char.IsUpper(message[i + 2]) && !IsAbbreviation(current):
                    sentences.Add(current.ToString().Trim());
                    current.Clear();
                    break;
            }
        }

        if (current.ToString().Trim() is { Length: > 0 } rest)
        {
            sentences.Add(rest);
        }

        return sentences.Count == 0 ? [message] : sentences;
    }

    private static bool IsAbbreviation(StringBuilder text) =>
        text.Length >= 4 && text.ToString(text.Length - 4, 4) is "e.g." or "i.e.";

    /// <summary>An apostrophe inside a word (don't) is not a quote.</summary>
    private static bool IsQuoteBoundary(string text, int index) =>
        index == 0 || index == text.Length - 1 || !char.IsLetter(text[index - 1]) || !char.IsLetter(text[index + 1]);

    [GeneratedRegex(@"'dockhand (?=[a-z])")]
    private static partial Regex DirectCommandHint();
}
