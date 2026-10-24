using DockhandCLI.Core.Models;
using Spectre.Console;

namespace DockhandCLI.Presentation;

/// <summary>
/// Output channels for one invocation. Human output goes to <see cref="Output"/> (stdout), diagnostics
/// to <see cref="Error"/> (stderr), and JSON is written verbatim to <see cref="RawOutput"/> so it is
/// never wrapped or decorated.
/// </summary>
internal sealed class CliConsole(IAnsiConsole output, IAnsiConsole error, TextWriter rawOutput, TextReader input, bool canPrompt, bool interactiveOutput)
{
    public static readonly CliConsole Null = new(
        AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(TextWriter.Null) }),
        AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(TextWriter.Null) }),
        TextWriter.Null,
        TextReader.Null,
        canPrompt: false,
        interactiveOutput: false);

    public IAnsiConsole Output { get; } = output;

    public IAnsiConsole Error { get; } = error;

    public TextWriter RawOutput { get; } = rawOutput;

    /// <summary>Standard input, for --token-stdin and --password-stdin.</summary>
    public TextReader Input { get; } = input;

    /// <summary>True when stdin is a terminal, so questions can be asked.</summary>
    public bool CanPrompt { get; } = canPrompt;

    /// <summary>True when stdout is a terminal, so spinners and progress bars make sense.</summary>
    public bool InteractiveOutput { get; } = interactiveOutput;
}

internal interface IConsoleFactory
{
    CliConsole Create(GlobalOptions options);
}

internal sealed class SystemConsoleFactory : IConsoleFactory
{
    public CliConsole Create(GlobalOptions options)
    {
        var noColor = options.NoColor || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));
        var outputRedirected = Console.IsOutputRedirected;

        var output = Create(Console.Out, noColor, outputRedirected);
        var error = Create(Console.Error, noColor, Console.IsErrorRedirected);

        return new CliConsole(
            output,
            error,
            Console.Out,
            Console.In,
            canPrompt: !Console.IsInputRedirected && !outputRedirected,
            interactiveOutput: !outputRedirected);
    }

    private static IAnsiConsole Create(TextWriter writer, bool noColor, bool redirected)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(writer),
            Ansi = noColor || redirected ? AnsiSupport.No : AnsiSupport.Detect,
            ColorSystem = noColor || redirected ? ColorSystemSupport.NoColors : ColorSystemSupport.Detect,
            Interactive = redirected ? InteractionSupport.No : InteractionSupport.Detect,
        });

        if (redirected)
        {
            // Avoid wrapping tables at 80 columns when output goes to a file or pipe.
            console.Profile.Width = 200;
        }

        return console;
    }
}
