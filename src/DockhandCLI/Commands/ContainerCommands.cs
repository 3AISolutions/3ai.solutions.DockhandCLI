using System.ComponentModel;
using DockhandCLI.Core.Interfaces;
using DockhandCLI.Core.Models;
using DockhandCLI.Core.Services;
using DockhandCLI.Presentation;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DockhandCLI.Commands;

internal sealed class ContainerListSettings : GlobalSettings
{
    [CommandOption("--running")]
    [Description("Only running containers")]
    public bool Running { get; init; }
}

internal sealed class ContainerListCommand(CommandServices services) : DockhandCommand<ContainerListSettings>(services)
{
    protected override async Task<int> RunAsync(ContainerListSettings settings, CancellationToken cancellationToken)
    {
        var (api, environment) = await ConnectToEnvironmentAsync(cancellationToken);
        var containers = await Output.StatusAsync("Loading containers…",
            () => api.GetContainersAsync(environment.Id, includeStopped: !settings.Running, cancellationToken));

        if (Output.IsJson)
        {
            Output.Json(containers);
            return ExitCodes.Success;
        }

        var table = Styles.NewTable("Name", "Id", "Image", "State", "Status");
        foreach (var container in containers.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            table.AddRow(
                $"[bold]{Styles.Escape(container.Name)}[/]",
                Styles.Dim(ContainerResolver.ShortId(container.Id)),
                Styles.Escape(container.Image),
                Styles.ContainerState(container.State),
                Styles.Escape(container.Status));
        }

        Output.MarkupLine($"[grey]Containers on[/] [bold]{Styles.Escape(Where(api, environment))}[/]");
        Output.Write(table);
        Output.Info($"{containers.Count} container(s), {containers.Count(c => c.State == "running")} running.");
        return ExitCodes.Success;
    }
}

internal class ContainerSettings : GlobalSettings
{
    [CommandArgument(0, "<CONTAINER>")]
    [Description("Container name or id")]
    public string Container { get; init; } = "";
}

internal sealed class ContainerInspectCommand(CommandServices services) : DockhandCommand<ContainerSettings>(services)
{
    protected override async Task<int> RunAsync(ContainerSettings settings, CancellationToken cancellationToken)
    {
        var (api, environment) = await ConnectToEnvironmentAsync(cancellationToken);
        var containers = await api.GetContainersAsync(environment.Id, includeStopped: true, cancellationToken);
        var container = ContainerResolver.Resolve(containers, settings.Container, Where(api, environment));
        var inspect = await api.InspectContainerAsync(environment.Id, container.Id, cancellationToken);
        Output.Json(inspect);
        return ExitCodes.Success;
    }
}

internal sealed class ContainerLogsSettings : ContainerSettings
{
    [CommandOption("-n|--tail <LINES>")]
    [Description("Number of trailing lines (server default 100)")]
    public int? Tail { get; init; }

    [CommandOption("--since <TIME>")]
    [Description("Only logs since a Unix timestamp or duration, e.g. 10m")]
    public string? Since { get; init; }

    [CommandOption("--until <TIME>")]
    [Description("Only logs before a Unix timestamp or duration")]
    public string? Until { get; init; }
}

internal sealed class ContainerLogsCommand(CommandServices services) : DockhandCommand<ContainerLogsSettings>(services)
{
    protected override async Task<int> RunAsync(ContainerLogsSettings settings, CancellationToken cancellationToken)
    {
        var (api, environment) = await ConnectToEnvironmentAsync(cancellationToken);
        var containers = await api.GetContainersAsync(environment.Id, includeStopped: true, cancellationToken);
        var container = ContainerResolver.Resolve(containers, settings.Container, Where(api, environment));
        var logs = await api.GetContainerLogsAsync(environment.Id, container.Id, settings.Tail, settings.Since, settings.Until, cancellationToken);

        if (Output.IsJson)
        {
            Output.Json(new { container = container.Name, containerId = container.Id, logs = logs.Logs });
        }
        else
        {
            Context.Console.RawOutput.Write(logs.Logs);
            if (!logs.Logs.EndsWith('\n'))
            {
                Context.Console.RawOutput.WriteLine();
            }
        }

        return ExitCodes.Success;
    }
}

internal sealed class ContainerActionSettings : ContainerSettings
{
    [CommandOption("-y|--yes")]
    [Description("Do not ask for confirmation (stop, restart, pause)")]
    public bool Yes { get; init; }
}

/// <summary>start/stop/restart/pause/unpause. Disruptive actions ask for confirmation.</summary>
internal abstract class ContainerActionCommand(CommandServices services, ContainerAction action) : DockhandCommand<ContainerActionSettings>(services)
{
    protected override async Task<int> RunAsync(ContainerActionSettings settings, CancellationToken cancellationToken)
    {
        var (api, environment) = await ConnectToEnvironmentAsync(cancellationToken);
        var containers = await api.GetContainersAsync(environment.Id, includeStopped: true, cancellationToken);
        var container = ContainerResolver.Resolve(containers, settings.Container, Where(api, environment));
        var verb = action.ToString().ToLowerInvariant();

        if (action is ContainerAction.Stop or ContainerAction.Restart or ContainerAction.Pause)
        {
            await Confirmation.EnsureAsync(Services.Prompt, settings.Yes,
                $"{action} container '{container.Name}' on {Where(api, environment)}?", cancellationToken);
        }

        var result = await Output.StatusAsync($"{action} {container.Name}…",
            () => api.RunContainerActionAsync(environment.Id, container.Id, action, cancellationToken));
        var success = result.Success != false;

        if (Output.IsJson)
        {
            Output.Json(new { container = container.Name, containerId = container.Id, action = verb, success, error = result.Error });
        }
        else if (success)
        {
            Output.Success($"{action} '{container.Name}' on {Where(api, environment)}: done.");
        }

        if (!success)
        {
            throw new DockhandException(ExitCodes.ApiError, $"Could not {verb} '{container.Name}': {result.Error ?? "the API reported a failure"}.");
        }

        return ExitCodes.Success;
    }
}

internal sealed class ContainerStartCommand(CommandServices services) : ContainerActionCommand(services, ContainerAction.Start);

internal sealed class ContainerStopCommand(CommandServices services) : ContainerActionCommand(services, ContainerAction.Stop);

internal sealed class ContainerRestartCommand(CommandServices services) : ContainerActionCommand(services, ContainerAction.Restart);

internal sealed class ContainerPauseCommand(CommandServices services) : ContainerActionCommand(services, ContainerAction.Pause);

internal sealed class ContainerUnpauseCommand(CommandServices services) : ContainerActionCommand(services, ContainerAction.Unpause);
