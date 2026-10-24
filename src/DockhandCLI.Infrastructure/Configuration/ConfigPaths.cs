namespace DockhandCLI.Infrastructure.Configuration;

/// <summary>Where DockhandCLI keeps its per-user files.</summary>
public sealed class ConfigPaths(string configDirectory)
{
    public const string DirectoryOverrideVariable = "DOCKHAND_CONFIG_DIR";

    public string ConfigDirectory { get; } = configDirectory;

    public string ConfigFile => Path.Combine(ConfigDirectory, "config.json");

    public string SecretsFile => Path.Combine(ConfigDirectory, "secrets.json");

    /// <summary>
    /// <c>DOCKHAND_CONFIG_DIR</c> if set; otherwise <c>%APPDATA%\DockhandCLI</c> on Windows,
    /// <c>~/Library/Application Support/DockhandCLI</c> on macOS and <c>$XDG_CONFIG_HOME/dockhand</c>
    /// (default <c>~/.config/dockhand</c>) elsewhere.
    /// </summary>
    public static ConfigPaths FromEnvironment()
    {
        var overrideDirectory = Environment.GetEnvironmentVariable(DirectoryOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
        {
            return new ConfigPaths(Path.GetFullPath(overrideDirectory));
        }

        if (OperatingSystem.IsWindows())
        {
            return new ConfigPaths(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DockhandCLI"));
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
        {
            return new ConfigPaths(Path.Combine(home, "Library", "Application Support", "DockhandCLI"));
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var baseDirectory = string.IsNullOrWhiteSpace(xdg) ? Path.Combine(home, ".config") : xdg;
        return new ConfigPaths(Path.Combine(baseDirectory, "dockhand"));
    }
}
