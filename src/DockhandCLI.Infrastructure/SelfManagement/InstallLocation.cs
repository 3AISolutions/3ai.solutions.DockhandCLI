using DockhandCLI.Core.Models;

namespace DockhandCLI.Infrastructure.SelfManagement;

/// <summary>The running <c>dockhand</c> executable and the folder it lives in.</summary>
public sealed class InstallLocation(string executablePath, bool isSingleFile)
{
    public static string ExecutableName { get; } = OperatingSystem.IsWindows() ? "dockhand.exe" : "dockhand";

    public string ExecutablePath { get; } = executablePath;

    public string Directory { get; } = Path.GetDirectoryName(executablePath)!;

    /// <summary>True for a published single-file executable; false for a build output next to its DLLs.</summary>
    public bool IsSingleFile { get; } = isSingleFile;

    /// <summary>The executable of this process. Fails when it runs through <c>dotnet dockhand.dll</c>.</summary>
    public static InstallLocation Current()
    {
        var path = Environment.ProcessPath;
        if (path is null || !string.Equals(Path.GetFileName(path), ExecutableName, StringComparison.OrdinalIgnoreCase))
        {
            throw DockhandException.Usage($"This only works when running the {ExecutableName} executable, not through '{path ?? "a host"}'.");
        }

        // Assemblies bundled into a single-file executable have no location on disk.
        return new InstallLocation(path, string.IsNullOrEmpty(typeof(InstallLocation).Assembly.Location));
    }

    /// <summary>
    /// Deletes executables left behind by a self-update on Windows, where the running file can only be
    /// renamed, not replaced. Best effort: a file still in use by another dockhand process is kept for next time.
    /// </summary>
    public static void DeleteLeftovers()
    {
        if (!OperatingSystem.IsWindows() || Environment.ProcessPath is not { } path)
        {
            return;
        }

        try
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(Path.GetDirectoryName(path)!, ExecutableName + ".*.old"))
            {
                File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still running or not ours to delete.
        }
    }
}
