using System.Text;

namespace DockhandCLI.Shell;

/// <summary>
/// Splits a shell line into arguments with POSIX-like rules: whitespace separates arguments,
/// '...' is literal, "..." allows \" and \\ escapes, and a backslash outside quotes escapes the next character.
/// </summary>
internal static class CommandLineTokenizer
{
    public static IReadOnlyList<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inToken = false;
        var quote = '\0';

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (quote == '\'')
            {
                if (c == '\'')
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(c);
                }

                continue;
            }

            if (quote == '"')
            {
                if (c == '"')
                {
                    quote = '\0';
                }
                else if (c == '\\' && i + 1 < line.Length && line[i + 1] is '"' or '\\')
                {
                    current.Append(line[++i]);
                }
                else
                {
                    current.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '\'' or '"':
                    quote = c;
                    inToken = true;
                    break;
                case '\\':
                    inToken = true;
                    current.Append(i + 1 < line.Length ? line[++i] : '\\');
                    break;
                case var _ when char.IsWhiteSpace(c):
                    if (inToken)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                        inToken = false;
                    }

                    break;
                default:
                    inToken = true;
                    current.Append(c);
                    break;
            }
        }

        if (quote != '\0')
        {
            throw new FormatException($"Unterminated {(quote == '"' ? "double" : "single")} quote.");
        }

        if (inToken)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}
