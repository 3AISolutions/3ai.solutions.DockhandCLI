using System.Globalization;
using DockhandCLI.Core.Models;

namespace DockhandCLI.Core.Services;

/// <summary>
/// Extracts global options (<c>--server</c>, <c>-alias</c>, <c>--env</c>, ...) from anywhere in the
/// argument list and returns the remaining arguments for the command parser. Shared by direct
/// execution and the interactive shell.
/// </summary>
public static class GlobalOptionParser
{
    public static ParsedArguments Parse(IReadOnlyList<string> args, Func<string, bool> isKnownAlias)
    {
        var options = new GlobalOptions();
        var remaining = new List<string>(args.Count);

        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];

            if (token == "--")
            {
                remaining.AddRange(args.Skip(i));
                break;
            }

            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                var eq = token.IndexOf('=', StringComparison.Ordinal);
                var name = eq < 0 ? token : token[..eq];
                var inline = eq < 0 ? null : token[(eq + 1)..];

                switch (name.ToLowerInvariant())
                {
                    case "--server":
                        options = WithServer(options, TakeValue(args, ref i, name, inline));
                        continue;
                    case "--env":
                        options = options with { Environment = TakeValue(args, ref i, name, inline) };
                        continue;
                    case "--output":
                        options = options with { Output = ParseOutput(TakeValue(args, ref i, name, inline)) };
                        continue;
                    case "--timeout":
                        options = options with { Timeout = ParseTimeout(TakeValue(args, ref i, name, inline)) };
                        continue;
                    case "--all-envs":
                        options = options with { AllEnvironments = Flag(name, inline) };
                        continue;
                    case "--verbose":
                        options = options with { Verbose = Flag(name, inline) };
                        continue;
                    case "--no-color":
                        options = options with { NoColor = Flag(name, inline) };
                        continue;
                    default:
                        remaining.Add(token);
                        continue;
                }
            }

            if (token == "-o")
            {
                options = options with { Output = ParseOutput(TakeValue(args, ref i, token, null)) };
                continue;
            }

            // -srv01 shorthand: a single dash followed by at least two characters. One-letter
            // options such as -h, -v or -y are never treated as aliases.
            if (token.Length > 2 && token[0] == '-' && token[1] != '-')
            {
                var alias = token[1..];
                if (double.TryParse(alias, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    remaining.Add(token);
                    continue;
                }

                if (!isKnownAlias(alias))
                {
                    throw DockhandException.Usage(
                        $"Unknown endpoint alias '{alias}' (from '{token}'). Run 'dockhand endpoint list' to see registered aliases.");
                }

                options = WithServer(options, alias);
                continue;
            }

            remaining.Add(token);
        }

        if (options.AllEnvironments && options.Environment is not null)
        {
            throw DockhandException.Usage("--env and --all-envs cannot be combined.");
        }

        return new ParsedArguments(options, remaining);
    }

    public static TimeSpan ParseTimeout(string value)
    {
        var text = value.Trim().ToLowerInvariant();
        var multiplier = 1d;
        if (text.EndsWith("ms", StringComparison.Ordinal))
        {
            multiplier = 0.001;
            text = text[..^2];
        }
        else if (text.EndsWith('s'))
        {
            text = text[..^1];
        }
        else if (text.EndsWith('m'))
        {
            multiplier = 60;
            text = text[..^1];
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || number <= 0)
        {
            throw DockhandException.Usage($"Invalid timeout '{value}'. Use seconds (30), or a suffix: 30s, 5m, 500ms.");
        }

        return TimeSpan.FromSeconds(number * multiplier);
    }

    private static GlobalOptions WithServer(GlobalOptions options, string alias)
    {
        if (options.Server is not null && !string.Equals(options.Server, alias, StringComparison.OrdinalIgnoreCase))
        {
            throw DockhandException.Usage($"Conflicting endpoint selections: '{options.Server}' and '{alias}'. Specify one endpoint.");
        }

        return options with { Server = alias };
    }

    private static string TakeValue(IReadOnlyList<string> args, ref int index, string name, string? inline)
    {
        if (inline is not null)
        {
            if (inline.Length == 0)
            {
                throw DockhandException.Usage($"Option {name} requires a value.");
            }

            return inline;
        }

        if (index + 1 >= args.Count || args[index + 1].StartsWith('-'))
        {
            throw DockhandException.Usage($"Option {name} requires a value.");
        }

        index++;
        return args[index];
    }

    private static bool Flag(string name, string? inline) =>
        inline is null ? true : throw DockhandException.Usage($"Option {name} does not take a value.");

    private static OutputFormat ParseOutput(string value) => value.ToLowerInvariant() switch
    {
        "json" => OutputFormat.Json,
        "table" => OutputFormat.Table,
        _ => throw DockhandException.Usage($"Unknown output format '{value}'. Use 'table' or 'json'."),
    };
}
