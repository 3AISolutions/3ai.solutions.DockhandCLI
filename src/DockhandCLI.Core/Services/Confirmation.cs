using DockhandCLI.Core.Interfaces;
using DockhandCLI.Core.Models;

namespace DockhandCLI.Core.Services;

/// <summary>Guards mutating operations: interactive confirmation unless <c>--yes</c> was given.</summary>
public static class Confirmation
{
    public static async Task EnsureAsync(IUserPrompt prompt, bool assumeYes, string question, CancellationToken cancellationToken = default)
    {
        if (assumeYes)
        {
            return;
        }

        if (!prompt.CanPrompt)
        {
            throw DockhandException.Usage("This operation needs confirmation, but no interactive terminal is available. Pass --yes to confirm.");
        }

        if (!await prompt.ConfirmAsync(question, cancellationToken).ConfigureAwait(false))
        {
            throw DockhandException.Aborted("Cancelled; nothing was changed.");
        }
    }
}
