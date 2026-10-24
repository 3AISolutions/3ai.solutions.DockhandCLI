using System.Text.RegularExpressions;

namespace DockhandCLI.Core.Services;

/// <summary>Rules for endpoint aliases, which double as <c>-alias</c> command-line shorthands.</summary>
public static partial class AliasRules
{
    /// <summary>Names that would collide with global options when written as <c>-name</c>.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "help", "version", "verbose", "server", "env", "all-envs", "output", "timeout", "no-color",
    };

    public static bool IsValid(string alias, out string? error)
    {
        if (!AliasPattern().IsMatch(alias))
        {
            error = $"Invalid alias '{alias}'. Use 2-32 characters: a letter first, then letters, digits, '-', '_' or '.'.";
            return false;
        }

        if (Reserved.Contains(alias))
        {
            error = $"Alias '{alias}' is reserved because '-{alias}' would clash with a command-line option.";
            return false;
        }

        error = null;
        return true;
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.-]{1,31}$")]
    private static partial Regex AliasPattern();
}
