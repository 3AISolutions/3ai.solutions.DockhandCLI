using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace DockhandCLI.Infrastructure.SelfManagement;

/// <summary>Result of adding a folder to, or removing it from, the user's PATH.</summary>
/// <param name="Directory">The folder that was added or removed.</param>
/// <param name="Changed">False when PATH already was as requested.</param>
/// <param name="Updated">What was written: the registry value on Windows, shell profile files elsewhere.</param>
/// <param name="RemovedEntries">Older dockhand folders dropped from PATH.</param>
/// <param name="Warnings">Things the user may need to fix by hand.</param>
public sealed record PathChange(
    string Directory,
    bool Changed,
    IReadOnlyList<string> Updated,
    IReadOnlyList<string> RemovedEntries,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Puts a folder on the current user's PATH so <c>dockhand</c> runs from any terminal. On Windows it edits the
/// user PATH in the registry; on Linux and macOS it maintains a marked block in the shell profile files.
/// </summary>
public sealed class PathInstaller
{
    internal const string BlockStart = "# >>> dockhand >>>";
    internal const string BlockEnd = "# <<< dockhand <<<";

    private static readonly string[] Profiles = [".profile", ".bash_profile", ".bashrc", ".zshrc"];

    private readonly string _home;
    private readonly string? _shell;

    public PathInstaller()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetEnvironmentVariable("SHELL"))
    {
    }

    internal PathInstaller(string home, string? shell)
    {
        _home = home;
        _shell = shell;
    }

    public PathChange Add(string directory) =>
        OperatingSystem.IsWindows() ? UpdateWindowsPath(directory, add: true) : UpdateProfiles(directory, add: true);

    public PathChange Remove(string directory) =>
        OperatingSystem.IsWindows() ? UpdateWindowsPath(directory, add: false) : UpdateProfiles(directory, add: false);

    // ---- Windows --------------------------------------------------------------------------------

    [SupportedOSPlatform("windows")]
    private static PathChange UpdateWindowsPath(string directory, bool add)
    {
        using var key = Registry.CurrentUser.CreateSubKey("Environment", writable: true);
        var exists = key.GetValueNames().Contains("Path", StringComparer.OrdinalIgnoreCase);
        // Read unexpanded and write back with the same kind, so entries such as %USERPROFILE%\bin survive.
        var current = exists ? key.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "" : "";
        var kind = exists ? key.GetValueKind("Path") : RegistryValueKind.ExpandString;

        var result = add
            ? PathEntries.Add(current, directory, IsDedicatedDockhandFolder, Environment.ExpandEnvironmentVariables)
            : PathEntries.Remove(current, directory, Environment.ExpandEnvironmentVariables);

        var warnings = new List<string>();
        if (add)
        {
            foreach (var other in PathEntries.Split(current).Concat(PathEntries.Split(MachinePath())))
            {
                var expanded = Environment.ExpandEnvironmentVariables(other);
                if (!PathEntries.Same(expanded, directory, Environment.ExpandEnvironmentVariables)
                    && !result.Removed.Contains(other)
                    && File.Exists(Path.Combine(expanded, InstallLocation.ExecutableName)))
                {
                    warnings.Add($"Another dockhand is on PATH in {expanded}; remove it if it is an old copy.");
                }
            }
        }

        if (result.Value == current)
        {
            return new PathChange(directory, false, [], [], warnings);
        }

        key.SetValue("Path", result.Value, kind == RegistryValueKind.String ? RegistryValueKind.String : RegistryValueKind.ExpandString);
        BroadcastEnvironmentChange();
        return new PathChange(directory, true, [@"HKCU\Environment\Path"], result.Removed, warnings);
    }

    [SupportedOSPlatform("windows")]
    private static string MachinePath()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment");
        return key?.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
    }

    /// <summary>A folder holding nothing but dockhand files: an earlier install that is safe to drop from PATH.</summary>
    private static bool IsDedicatedDockhandFolder(string directory)
    {
        try
        {
            return File.Exists(Path.Combine(directory, InstallLocation.ExecutableName))
                && System.IO.Directory.EnumerateFiles(directory)
                    .All(file => Path.GetFileName(file).StartsWith("dockhand", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Tells Explorer, and terminals it starts from now on, that the environment changed.</summary>
    [SupportedOSPlatform("windows")]
    private static void BroadcastEnvironmentChange() =>
        NativeMethods.SendMessageTimeout(new IntPtr(0xFFFF), 0x001A, IntPtr.Zero, "Environment", 0x0002, 5000, out _);

    private static class NativeMethods
    {
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
    }

    // ---- Linux / macOS --------------------------------------------------------------------------

    internal PathChange UpdateProfiles(string directory, bool add)
    {
        var shell = Path.GetFileName(_shell ?? "");
        var targets = Profiles.Select(name => Path.Combine(_home, name)).Where(File.Exists).ToList();

        if (add)
        {
            // Make sure the user's own shell reads the block, creating its startup file if needed.
            var own = shell switch
            {
                "zsh" => ".zshrc",
                "bash" when OperatingSystem.IsMacOS() => ".bash_profile",
                "bash" => ".bashrc",
                _ => targets.Count == 0 ? ".profile" : null,
            };
            if (own is not null && !targets.Contains(Path.Combine(_home, own)))
            {
                targets.Add(Path.Combine(_home, own));
            }
        }

        var updated = new List<string>();
        foreach (var file in targets)
        {
            var content = File.Exists(file) ? File.ReadAllText(file) : "";
            var next = ShellProfile.Apply(content, add ? ShellProfile.PosixBlock(directory) : null);
            if (next != content)
            {
                File.WriteAllText(file, next);
                updated.Add(file);
            }
        }

        var fishConfig = Path.Combine(_home, ".config", "fish");
        var fishFile = Path.Combine(fishConfig, "conf.d", "dockhand.fish");
        if (add && (shell == "fish" || System.IO.Directory.Exists(fishConfig)))
        {
            var content = ShellProfile.FishBlock(directory);
            if (!File.Exists(fishFile) || File.ReadAllText(fishFile) != content)
            {
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(fishFile)!);
                File.WriteAllText(fishFile, content);
                updated.Add(fishFile);
            }
        }
        else if (!add && File.Exists(fishFile))
        {
            File.Delete(fishFile);
            updated.Add(fishFile);
        }

        return new PathChange(directory, updated.Count > 0, updated, [], []);
    }
}

/// <summary>Editing a <c>;</c>-separated Windows PATH value.</summary>
internal static class PathEntries
{
    public static IEnumerable<string> Split(string value) =>
        value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Appends <paramref name="directory"/> unless present, and drops other entries <paramref name="isStale"/> accepts.</summary>
    public static (string Value, IReadOnlyList<string> Removed) Add(string value, string directory, Func<string, bool> isStale, Func<string, string> expand)
    {
        var entries = Split(value).ToList();
        var removed = entries.Where(e => !Same(e, directory, expand) && isStale(expand(e))).ToList();
        entries.RemoveAll(removed.Contains);
        if (!entries.Any(e => Same(e, directory, expand)))
        {
            entries.Add(directory);
        }

        return (Join(value, entries), removed);
    }

    public static (string Value, IReadOnlyList<string> Removed) Remove(string value, string directory, Func<string, string> expand)
    {
        var entries = Split(value).ToList();
        var removed = entries.Where(e => Same(e, directory, expand)).ToList();
        entries.RemoveAll(removed.Contains);
        return (Join(value, entries), removed);
    }

    public static bool Same(string entry, string directory, Func<string, string> expand) =>
        string.Equals(Normalize(expand(entry)), Normalize(directory), StringComparison.OrdinalIgnoreCase);

    // Keep the value byte-for-byte when nothing changed, so no registry write happens.
    private static string Join(string original, List<string> entries) =>
        entries.SequenceEqual(Split(original)) ? original : string.Join(';', entries);

    private static string Normalize(string path)
    {
        try
        {
            // Windows separators, trimmed explicitly so this behaves the same when tests run on Linux.
            return Path.GetFullPath(path.Trim('"')).TrimEnd('\\', '/');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}

/// <summary>The marked PATH block in shell startup files.</summary>
internal static class ShellProfile
{
    public static string PosixBlock(string directory)
    {
        var quoted = "'" + directory.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        return $"""
            {PathInstaller.BlockStart}
            # Added by 'dockhand self path'. Remove with 'dockhand self path --remove'.
            case ":$PATH:" in *:{quoted}:*) ;; *) export PATH={quoted}:"$PATH" ;; esac
            {PathInstaller.BlockEnd}
            """;
    }

    public static string FishBlock(string directory)
    {
        var quoted = "'" + directory.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("'", @"\'", StringComparison.Ordinal) + "'";
        return $"""
            # Added by 'dockhand self path'. Remove with 'dockhand self path --remove'.
            fish_add_path --path {quoted}

            """;
    }

    /// <summary>Replaces the marked block in <paramref name="content"/>, appends it when missing, or removes it when <paramref name="block"/> is null.</summary>
    public static string Apply(string content, string? block)
    {
        var lines = content.ReplaceLineEndings("\n").Split('\n').ToList();
        var start = lines.FindIndex(l => l.Trim() == PathInstaller.BlockStart);
        var end = start < 0 ? -1 : lines.FindIndex(start, l => l.Trim() == PathInstaller.BlockEnd);

        if (start >= 0 && end >= 0)
        {
            lines.RemoveRange(start, end - start + 1);
            if (block is not null)
            {
                lines.InsertRange(start, block.Split('\n'));
            }
            else if (start > 0 && start < lines.Count && lines[start - 1].Length == 0 && lines[start].Length == 0)
            {
                // Drop the blank line that separated the block from what came before.
                lines.RemoveAt(start - 1);
            }

            return string.Join('\n', lines);
        }

        if (block is null)
        {
            return content;
        }

        var builder = new StringBuilder(content);
        if (content.Length > 0)
        {
            builder.Append(content.EndsWith('\n') ? "\n" : "\n\n");
        }

        return builder.Append(block).Append('\n').ToString();
    }
}
