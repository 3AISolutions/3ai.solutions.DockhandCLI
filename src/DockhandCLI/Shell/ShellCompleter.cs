using DockhandCLI.Commands;

namespace DockhandCLI.Shell;

internal sealed record Completion(int ReplaceFrom, IReadOnlyList<string> Candidates);

/// <summary>Tab completion for command words, endpoint aliases and global options.</summary>
internal sealed class ShellCompleter(Func<IReadOnlyList<string>> aliases)
{
    private static readonly string[] GlobalOptions =
        ["--server", "--env", "--all-envs", "--output", "--timeout", "--verbose", "--no-color", "--help", "--yes", "--dry-run", "--pending", "--cached"];

    private static readonly HashSet<string> OptionsWithValue = new(StringComparer.OrdinalIgnoreCase)
    {
        "--server", "--env", "--output", "-o", "--timeout",
    };

    public Completion Complete(string textBeforeCursor)
    {
        var wordStart = textBeforeCursor.LastIndexOf(' ') + 1;
        var word = textBeforeCursor[wordStart..];
        var previous = textBeforeCursor[..wordStart].Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        var slash = previous.Count == 0 && word.StartsWith('/');
        if (slash)
        {
            word = word[1..];
            wordStart++;
        }

        if (previous.Count > 0)
        {
            previous[0] = previous[0].TrimStart('/');
        }

        var last = previous.Count > 0 ? previous[^1] : null;
        IEnumerable<string> candidates;

        if (string.Equals(last, "--server", StringComparison.OrdinalIgnoreCase) || (previous.Count == 1 && string.Equals(last, "use", StringComparison.OrdinalIgnoreCase)))
        {
            candidates = aliases();
        }
        else if (string.Equals(last, "--output", StringComparison.OrdinalIgnoreCase) || last == "-o")
        {
            candidates = ["table", "json"];
        }
        else if (word.StartsWith("--", StringComparison.Ordinal))
        {
            candidates = GlobalOptions;
        }
        else if (word.StartsWith('-'))
        {
            candidates = aliases().Select(a => "-" + a);
        }
        else
        {
            var path = CommandPath(previous);
            candidates = CommandCatalog.NextWords(path);
            if (path.Count == 0)
            {
                candidates = candidates.Concat(CommandCatalog.ShellWords);
            }
        }

        var matches = candidates
            .Where(c => c.StartsWith(word, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new Completion(wordStart, matches);
    }

    /// <summary>The command words typed so far, without options and option values.</summary>
    private static List<string> CommandPath(List<string> previous)
    {
        var path = new List<string>();
        for (var i = 0; i < previous.Count; i++)
        {
            var token = previous[i];
            if (token.StartsWith('-'))
            {
                if (OptionsWithValue.Contains(token))
                {
                    i++;
                }

                continue;
            }

            path.Add(token);
        }

        return path;
    }
}
