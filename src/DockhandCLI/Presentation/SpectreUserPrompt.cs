using DockhandCLI.Core.Interfaces;
using DockhandCLI.Hosting;
using Spectre.Console;

namespace DockhandCLI.Presentation;

/// <summary>Terminal prompts. Disabled when stdin/stdout are redirected or JSON output was requested.</summary>
internal sealed class SpectreUserPrompt(InvocationContext context) : IUserPrompt
{
    public bool CanPrompt => context.Console.CanPrompt && !context.IsJson;

    public Task<bool> ConfirmAsync(string message, CancellationToken cancellationToken = default) =>
        new ConfirmationPrompt(Markup.Escape(message)) { DefaultValue = false }
            .ShowAsync(context.Console.Output, cancellationToken);

    public Task<string> AskAsync(string message, CancellationToken cancellationToken = default) =>
        new TextPrompt<string>(Markup.Escape(message)).ShowAsync(context.Console.Output, cancellationToken);

    public Task<string> AskSecretAsync(string message, CancellationToken cancellationToken = default) =>
        new TextPrompt<string>(Markup.Escape(message)).Secret().ShowAsync(context.Console.Output, cancellationToken);

    public Task<string> SelectAsync(string title, IEnumerable<string> choices, CancellationToken cancellationToken = default) =>
        new SelectionPrompt<string>().Title(Markup.Escape(title)).AddChoices(choices)
            .ShowAsync(context.Console.Output, cancellationToken);
}
