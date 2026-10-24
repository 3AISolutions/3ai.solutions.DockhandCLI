namespace DockhandCLI.Core.Models;

public enum OutputFormat
{
    Table,
    Json,
}

/// <summary>Options accepted anywhere on the command line, before or after the command name.</summary>
public sealed record GlobalOptions
{
    public static readonly GlobalOptions Default = new();

    /// <summary>Endpoint alias from <c>--server</c> or the <c>-alias</c> shorthand.</summary>
    public string? Server { get; init; }

    /// <summary>Environment id or name from <c>--env</c>.</summary>
    public string? Environment { get; init; }

    public bool AllEnvironments { get; init; }

    /// <summary>Explicit output format; null means "table" for humans.</summary>
    public OutputFormat? Output { get; init; }

    public TimeSpan? Timeout { get; init; }

    public bool Verbose { get; init; }

    public bool NoColor { get; init; }

    public OutputFormat EffectiveOutput => Output ?? OutputFormat.Table;
}

/// <summary>Result of separating global options from the command arguments.</summary>
public sealed record ParsedArguments(GlobalOptions Options, IReadOnlyList<string> Remaining);
