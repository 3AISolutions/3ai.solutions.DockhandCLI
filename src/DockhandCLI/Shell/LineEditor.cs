using System.Text;
using DockhandCLI.Commands;
using Spectre.Console;

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
/// Typing <c>/</c> opens a command picker below the line: Up/Down select, Tab completes, Enter runs
/// (or completes a command that still needs arguments), Esc dismisses.
/// </summary>
internal sealed class LineEditor(ShellCompleter completer, Action<string> writeMarkup) : ILineReader
{
    private const int MaxHistory = 500;
    private const int MenuRows = 8;
    private readonly List<string> _history = [];

    public IReadOnlyList<string> History => _history;

    public string? ReadLine(string prompt)
    {
        writeMarkup(prompt);
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

        // Command picker state. The menu is recomputed whenever the text changes; Esc (or recalling a
        // history entry) suppresses it until the text changes again.
        IReadOnlyList<CatalogEntry> matches = [];
        string? menuFor = null;
        string? dismissedFor = null;
        var selected = 0;
        var firstVisible = 0;
        var menuDrawn = 0;

        void Render()
        {
            var width = Math.Max(Console.BufferWidth, 1);
            var text = buffer.ToString();
            if (text != menuFor)
            {
                menuFor = text;
                matches = text == dismissedFor ? [] : completer.MatchCommands(text);
                selected = firstVisible = 0;
            }

            Console.SetCursorPosition(startLeft, startTop);
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
            DrawMenu(width);
            var position = startLeft + cursor;
            Console.SetCursorPosition(position % width, startTop + position / width);
        }

        void DrawMenu(int width)
        {
            var lines = MenuLines(matches, selected, ref firstVisible, width);
            var rows = Math.Max(lines.Count, menuDrawn);
            if (rows == 0)
            {
                return;
            }

            var endOfInput = startLeft + renderedLength;
            var inputEndTop = startTop + endOfInput / width;
            Console.SetCursorPosition(endOfInput % width, inputEndTop);
            for (var i = 0; i < rows; i++)
            {
                Console.WriteLine();
                if (i < lines.Count)
                {
                    writeMarkup(lines[i].Markup);
                    Console.Write(new string(' ', Math.Max(0, width - 1 - lines[i].Length)));
                }
                else
                {
                    Console.Write(new string(' ', width - 1));
                }
            }

            // Drawing below the last row scrolls the terminal and moves the input line up.
            if (Console.CursorTop < inputEndTop + rows)
            {
                startTop -= inputEndTop + rows - Console.CursorTop;
            }

            menuDrawn = lines.Count;
        }

        void Replace(string text, bool showMenu = true)
        {
            buffer.Clear().Append(text);
            cursor = buffer.Length;
            dismissedFor = showMenu ? null : text;
            Render();
        }

        void CloseMenu()
        {
            dismissedFor = menuFor = buffer.ToString();
            matches = [];
            Render();
        }

        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            var menuOpen = matches.Count > 0;

            if (key.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                switch (key.Key)
                {
                    case ConsoleKey.C:
                        CloseMenu();
                        Console.WriteLine("^C");
                        return "";
                    case ConsoleKey.D when buffer.Length == 0:
                        CloseMenu();
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
                        writeMarkup(prompt);
                        (startLeft, startTop) = (Console.CursorLeft, Console.CursorTop);
                        renderedLength = menuDrawn = 0;
                        Render();
                        continue;
                }
            }

            if (menuOpen)
            {
                switch (key.Key)
                {
                    case ConsoleKey.UpArrow:
                        selected = (selected - 1 + matches.Count) % matches.Count;
                        Render();
                        continue;
                    case ConsoleKey.DownArrow:
                        selected = (selected + 1) % matches.Count;
                        Render();
                        continue;
                    case ConsoleKey.Tab:
                        Replace($"/{matches[selected].Path} ");
                        continue;
                    case ConsoleKey.Escape:
                        CloseMenu();
                        continue;
                    case ConsoleKey.Enter when matches[selected].Arguments.StartsWith('<'):
                        // Still needs arguments: complete it and keep editing.
                        Replace($"/{matches[selected].Path} ");
                        continue;
                    case ConsoleKey.Enter:
                        buffer.Clear().Append('/').Append(matches[selected].Path);
                        cursor = buffer.Length;
                        break;
                }
            }

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    cursor = buffer.Length;
                    CloseMenu();
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
                    Replace(_history[--historyIndex], showMenu: false);
                    break;
                case ConsoleKey.DownArrow when historyIndex < _history.Count:
                    historyIndex++;
                    Replace(historyIndex == _history.Count ? "" : _history[historyIndex], showMenu: false);
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

    /// <summary>
    /// The picker rows: a window of <see cref="MenuRows"/> entries around the selection, the selected one
    /// highlighted, plus a position footer when not all entries fit. Each row is shorter than <paramref name="width"/>
    /// so the terminal never wraps it.
    /// </summary>
    internal static List<(string Markup, int Length)> MenuLines(
        IReadOnlyList<CatalogEntry> entries, int selected, ref int firstVisible, int width)
    {
        var lines = new List<(string, int)>();
        if (entries.Count == 0)
        {
            return lines;
        }

        if (selected < firstVisible)
        {
            firstVisible = selected;
        }
        else if (selected >= firstVisible + MenuRows)
        {
            firstVisible = selected - MenuRows + 1;
        }

        // Sized over all entries so the columns do not shift while scrolling.
        var nameWidth = entries.Max(e => Name(e).Length);
        var descriptionWidth = width - 1 - 2 - nameWidth - 2;

        for (var i = firstVisible; i < Math.Min(entries.Count, firstVisible + MenuRows); i++)
        {
            var entry = entries[i];
            var path = $"/{entry.Path}";
            var arguments = entry.Arguments.Length > 0 ? " " + entry.Arguments : "";
            var padding = new string(' ', nameWidth - Name(entry).Length);
            var description = descriptionWidth >= 10 ? Truncate(entry.Description, descriptionWidth) : "";
            var length = 2 + nameWidth + (description.Length > 0 ? 2 + description.Length : 0);

            var markup = i == selected
                ? $"[cyan]❯ {Markup.Escape(path)}[/][cyan dim]{Markup.Escape(arguments)}[/]{padding}  [cyan]{Markup.Escape(description)}[/]"
                : $"  {Markup.Escape(path)}[grey]{Markup.Escape(arguments)}[/]{padding}  [grey]{Markup.Escape(description)}[/]";
            lines.Add((markup, Math.Min(length, width - 1)));
        }

        if (entries.Count > MenuRows)
        {
            var footer = $"  {selected + 1}/{entries.Count}";
            lines.Add(($"[grey]{footer}[/]", footer.Length));
        }

        return lines;

        static string Name(CatalogEntry e) => e.Arguments.Length > 0 ? $"/{e.Path} {e.Arguments}" : $"/{e.Path}";
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";

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
            writeMarkup(prompt);
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
