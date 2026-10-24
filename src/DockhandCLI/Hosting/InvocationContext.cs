using DockhandCLI.Core.Models;
using DockhandCLI.Presentation;

namespace DockhandCLI.Hosting;

/// <summary>State of the command being executed. Set before each direct or shell invocation.</summary>
internal sealed class InvocationContext
{
    public GlobalOptions Options { get; set; } = GlobalOptions.Default;

    public CliConsole Console { get; set; } = CliConsole.Null;

    /// <summary>Endpoint chosen with <c>use</c> in the interactive shell. Never written to the configuration.</summary>
    public string? SessionEndpoint { get; set; }

    public bool InShell { get; set; }

    public bool IsJson => Options.EffectiveOutput == OutputFormat.Json;
}
