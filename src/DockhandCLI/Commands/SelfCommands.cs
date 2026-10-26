using System.ComponentModel;
using DockhandCLI.Core.Models;
using DockhandCLI.Hosting;
using DockhandCLI.Infrastructure.SelfManagement;
using DockhandCLI.Presentation;
using Spectre.Console.Cli;

namespace DockhandCLI.Commands;

// ---- self path --------------------------------------------------------------------------------

internal sealed class SelfPathSettings : GlobalSettings
{
    [CommandOption("--remove")]
    [Description("Remove the folder from PATH instead of adding it")]
    public bool Remove { get; init; }
}

internal sealed class SelfPathCommand(CommandServices services, PathInstaller installer) : DockhandCommand<SelfPathSettings>(services)
{
    protected override Task<int> RunAsync(SelfPathSettings settings, CancellationToken cancellationToken)
    {
        var location = InstallLocation.Current();
        var change = settings.Remove ? installer.Remove(location.Directory) : installer.Add(location.Directory);

        if (Output.IsJson)
        {
            Output.Json(change);
            return Task.FromResult(ExitCodes.Success);
        }

        if (!change.Changed)
        {
            Output.Success(settings.Remove ? $"{change.Directory} is not on your PATH." : $"{change.Directory} is already on your PATH.");
        }
        else
        {
            Output.Success(settings.Remove ? $"Removed {change.Directory} from your PATH." : $"Added {change.Directory} to your PATH.");
            foreach (var entry in change.RemovedEntries)
            {
                Output.Info($"Removed older entry: {entry}");
            }

            foreach (var file in change.Updated)
            {
                Output.Info($"Updated {file}");
            }

            Output.Info("Open a new terminal for the change to take effect.");
        }

        foreach (var warning in change.Warnings)
        {
            Output.Warning(warning);
        }

        return Task.FromResult(ExitCodes.Success);
    }
}

// ---- self update ------------------------------------------------------------------------------

internal sealed class SelfUpdateSettings : GlobalSettings
{
    [CommandOption("--check")]
    [Description("Only report whether a newer version exists")]
    public bool Check { get; init; }

    [CommandOption("--force")]
    [Description("Reinstall the latest release even when already up to date")]
    public bool Force { get; init; }
}

internal sealed class SelfUpdateCommand(CommandServices services, SelfUpdater updater) : DockhandCommand<SelfUpdateSettings>(services)
{
    protected override async Task<int> RunAsync(SelfUpdateSettings settings, CancellationToken cancellationToken)
    {
        // Fail before any download when this executable cannot be replaced.
        InstallLocation? location = null;
        if (!settings.Check)
        {
            location = InstallLocation.Current();
            if (!location.IsSingleFile)
            {
                throw DockhandException.Usage(
                    $"{location.ExecutablePath} is a build output, not a released executable. Update the source and rebuild, or install a release.");
            }
        }

        var current = SelfUpdater.ParseVersion(CliApplication.Version) ?? new Version(0, 0, 0);
        var check = await Output.StatusAsync("Checking for a new version…", () => updater.CheckAsync(current, cancellationToken));
        var install = location is not null && (check.UpdateAvailable || settings.Force);

        if (install)
        {
            await Output.StatusAsync($"Installing dockhand {check.Latest.Version}…", async () =>
            {
                await updater.InstallAsync(check, location!, cancellationToken);
                return true;
            });
        }

        if (Output.IsJson)
        {
            Output.Json(new
            {
                currentVersion = check.Current.ToString(),
                latestVersion = check.Latest.Version.ToString(),
                updateAvailable = check.UpdateAvailable,
                updated = install,
                path = location?.ExecutablePath,
                releaseUrl = check.Latest.PageUrl,
            });
            return ExitCodes.Success;
        }

        if (install)
        {
            Output.Success($"Updated dockhand {check.Current} → {check.Latest.Version} ({location!.ExecutablePath}).");
            if (Context.InShell)
            {
                Output.Info("Restart the shell to use the new version.");
            }
        }
        else if (check.UpdateAvailable)
        {
            Output.MarkupLine($"[yellow]●[/] dockhand {check.Latest.Version} is available (you have {check.Current}).");
            Output.Info($"Install it with: dockhand self update   Release notes: {check.Latest.PageUrl}");
        }
        else
        {
            Output.Success($"dockhand {check.Current} is up to date.");
        }

        return ExitCodes.Success;
    }
}
