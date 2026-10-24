using System.Text;

namespace DockhandCLI.Shell;

internal interface ILineReader
{
    /// <summary>Reads one line; returns null at end of input.</summary>
    string? ReadLine(string prompt);
}

/// <summary>Plain reader for redirected input (scripts piped into the shell). No prompt is echoed.</summary>
internal sealed class StreamLineReader(TextReader reader) : ILineReader
{
    public string? ReadLine(string prompt) => reader.ReadLine();
}

/// <summary>
/// Minimal terminal line editor: cursor movement, history (Up/Down), Tab completion, Ctrl+C to discard
/// the line and Ctrl+D on an empty line to exit. Restores the console mode it changes.
/// </summary>
internal sealed class LineEditor(ShellCompleter completer, Action<string> writePrompt) : ILineReader
{
    private const int MaxHistory = 500;
    private readonly List<string> _history = [];

    public IReadOnlyList<string> History => _history;

    public string? ReadLine(string prompt)
    {
        writePrompt(prompt);
        var previousTreatControlC = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;
        try
        {
            return ReadInput(prompt);
        }
        finally
        {
            Console.TreatControlCAsInput = previousTreatControlC;
        }
    }

    private string? ReadInput(string prompt)
    {
        var buffer = new StringBuilder();
        var cursor = 0;
        var historyIndex = _history.Count;
        var startLeft = Console.CursorLeft;
        var startTop = Console.CursorTop;
        var renderedLength = 0;

        void Render()
        {
            var width = Math.Max(Console.BufferWidth, 1);
            Console.SetCursorPosition(startLeft, startTop);
            var text = buffer.ToString();
            Console.Write(text);
            if (renderedLength > text.Length)
            {
                Console.Write(new string(' ', renderedLength - text.Length));
            }

            // The terminal may have scrolled when the line wrapped on the last row.
            var expectedEndTop = startTop + (startLeft + Math.Max(text.Length, renderedLength)) / width;
            if (Console.CursorTop < expectedEndTop)
            {
                startTop -= expectedEndTop - Console.CursorTop;
            }

            renderedLength = text.Length;
            var position = startLeft + cursor;
            Console.SetCursorPosition(position % width, startTop + position / width);
        }

        void Replace(string text)
        {
            buffer.Clear().Append(text);
            cursor = buffer.Length;
            Render();
        }

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                switch (key.Key)
                {
                    case ConsoleKey.C:
                        Console.WriteLine("^C");
                        return "";
                    case ConsoleKey.D when buffer.Length == 0:
                        Console.WriteLine();
                        return null;
                    case ConsoleKey.A:
                        cursor = 0;
                        Render();
                        continue;
                    case ConsoleKey.E:
                        cursor = buffer.Length;
                        Render();
                        continue;
                    case ConsoleKey.U:
                        buffer.Remove(0, cursor);
                        cursor = 0;
                        Render();
                        continue;
                    case ConsoleKey.L:
                        Console.Clear();
                        writePrompt(prompt);
                        (startLeft, startTop) = (Console.CursorLeft, Console.CursorTop);
                        renderedLength = 0;
                        Render();
                        continue;
                }
            }

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    cursor = buffer.Length;
                    Render();
                    Console.WriteLine();
                    var line = buffer.ToString();
                    AddHistory(line);
                    return line;
                case ConsoleKey.Escape:
                    Replace("");
                    break;
                case ConsoleKey.Backspace when cursor > 0:
                    buffer.Remove(--cursor, 1);
                    Render();
                    break;
                case ConsoleKey.Delete when cursor < buffer.Length:
                    buffer.Remove(cursor, 1);
                    Render();
                    break;
                case ConsoleKey.LeftArrow when cursor > 0:
                    cursor--;
                    Render();
                    break;
                case ConsoleKey.RightArrow when cursor < buffer.Length:
                    cursor++;
                    Render();
                    break;
                case ConsoleKey.Home:
                    cursor = 0;
                    Render();
                    break;
                case ConsoleKey.End:
                    cursor = buffer.Length;
                    Render();
                    break;
                case ConsoleKey.UpArrow when historyIndex > 0:
                    Replace(_history[--historyIndex]);
                    break;
                case ConsoleKey.DownArrow when historyIndex < _history.Count:
                    historyIndex++;
                    Replace(historyIndex == _history.Count ? "" : _history[historyIndex]);
                    break;
                case ConsoleKey.Tab:
                    if (Complete(buffer, ref cursor, prompt))
                    {
                        (startLeft, startTop) = (Console.CursorLeft, Console.CursorTop);
                        renderedLength = 0;
                    }

                    Render();
                    break;
                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        buffer.Insert(cursor++, key.KeyChar);
                        Render();
                    }

                    break;
            }
        }
    }

    /// <summary>Completes the word before the cursor. Returns true when candidates were listed and the prompt redrawn.</summary>
    private bool Complete(StringBuilder buffer, ref int cursor, string prompt)
    {
        var before = buffer.ToString(0, cursor);
        var completion = completer.Complete(before);
        if (completion.Candidates.Count == 0)
        {
            return false;
        }

        var word = before[completion.ReplaceFrom..];
        var replacement = completion.Candidates.Count == 1
            ? completion.Candidates[0] + " "
            : CommonPrefix(completion.Candidates);

        if (replacement.Length > word.Length)
        {
            buffer.Remove(completion.ReplaceFrom, word.Length).Insert(completion.ReplaceFrom, replacement);
            cursor = completion.ReplaceFrom + replacement.Length;
            return false;
        }

        if (completion.Candidates.Count > 1)
        {
            Console.WriteLine();
            Console.WriteLine(string.Join("  ", completion.Candidates));
            writePrompt(prompt);
            return true;
        }

        return false;
    }

    private void AddHistory(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || (_history.Count > 0 && _history[^1] == line))
        {
            return;
        }

        _history.Add(line);
        if (_history.Count > MaxHistory)
        {
            _history.RemoveAt(0);
        }
    }

    private static string CommonPrefix(IReadOnlyList<string> values)
    {
        var prefix = values[0];
        foreach (var value in values.Skip(1))
        {
            var length = 0;
            while (length < prefix.Length && length < value.Length && char.ToLowerInvariant(prefix[length]) == char.ToLowerInvariant(value[length]))
            {
                length++;
            }

            prefix = prefix[..length];
        }

        return prefix;
    }
}
