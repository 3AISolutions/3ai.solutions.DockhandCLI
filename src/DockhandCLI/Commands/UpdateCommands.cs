using System.ComponentModel;
using System.Text.Json;
using DockhandCLI.Core.Interfaces;
using DockhandCLI.Core.Models;
using DockhandCLI.Core.Services;
using DockhandCLI.Presentation;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DockhandCLI.Commands;

// ---- check updates ----------------------------------------------------------------------------

internal sealed class CheckUpdatesSettings : GlobalSettings
{
    [CommandOption("--pending")]
    [Description("List pending updates recorded by Dockhand (GET pending-updates), including held updates")]
    public bool Pending { get; init; }

    [CommandOption("--cached")]
    [Description("Show the results of the last check without running a new one (GET check-updates)")]
    public bool Cached { get; init; }

    [CommandOption("--all-envs")]
    [Description("Check every environment of the endpoint")]
    public bool AllEnvs { get; init; }

    [CommandOption("--updates-only")]
    [Description("Hide containers that are up to date")]
    public bool UpdatesOnly { get; init; }
}

internal sealed class CheckUpdatesCommand(CommandServices services) : DockhandCommand<CheckUpdatesSettings>(services)
{
    protected override bool SupportsAllEnvironments => true;

    protected override async Task<int> RunAsync(CheckUpdatesSettings settings, CancellationToken cancellationToken)
    {
        if (settings.Pending && settings.Cached)
        {
            throw DockhandException.Usage("Use either --pending or --cached, not both.");
        }

        var mode = settings.Pending ? UpdateCheckMode.Pending : settings.Cached ? UpdateCheckMode.Cached : UpdateCheckMode.Fresh;
        var api = await ConnectAsync(cancellationToken);

        IReadOnlyList<DockerEnvironment> environments = Options.AllEnvironments
            ? await Output.StatusAsync("Loading environments…", () => api.GetEnvironmentsAsync(cancellationToken))
            : [await EnvironmentResolver.ResolveAsync(api, Options.Environment, cancellationToken)];

        if (environments.Count == 0)
        {
            throw DockhandException.NotFound($"Endpoint '{api.Endpoint.Alias}' has no Docker environments you can access.");
        }

        var report = mode == UpdateCheckMode.Fresh && !Output.IsJson && Context.Console.InteractiveOutput
            ? await CheckWithProgressAsync(api, environments, cancellationToken)
            : await Output.StatusAsync(
                mode == UpdateCheckMode.Fresh ? "Checking for image updates…" : "Loading update records…",
                () => Services.Updates.CheckAsync(api, environments, mode, null, cancellationToken));

        if (Output.IsJson)
        {
            Output.Json(report);
        }
        else
        {
            Render(report, mode, settings.UpdatesOnly);
        }

        return report.ExitCode;
    }

    private Task<UpdateCheckReport> CheckWithProgressAsync(IDockhandApi api, IReadOnlyList<DockerEnvironment> environments, CancellationToken cancellationToken) =>
        Context.Console.Output.Progress()
            .AutoClear(true)
            .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new SpinnerColumn())
            .StartAsync(async progress =>
            {
                var tasks = environments.ToDictionary(
                    e => e.Id,
                    e => progress.AddTask(Markup.Escape($"{api.Endpoint.Alias} / {e.Name}"), autoStart: false, maxValue: 1));

                IProgress<JobProgress> ProgressFor(DockerEnvironment environment)
                {
                    var task = tasks[environment.Id];
                    task.StartTask();
                    return new CallbackProgress<JobProgress>(update =>
                    {
                        if (update.Event == "progress"
                            && update.Data.ValueKind == JsonValueKind.Object
                            && update.Data.TryGetProperty("checked", out var done) && done.TryGetInt32(out var checkedCount)
                            && update.Data.TryGetProperty("total", out var all) && all.TryGetInt32(out var total))
                        {
                            task.MaxValue = Math.Max(total, 1);
                            task.Value = checkedCount;
                        }
                    });
                }

                var report = await Services.Updates.CheckAsync(api, environments, UpdateCheckMode.Fresh, ProgressFor, cancellationToken);
                foreach (var task in tasks.Values)
                {
                    task.Value = task.MaxValue;
                    task.StopTask();
                }

                return report;
            });

    private void Render(UpdateCheckReport report, UpdateCheckMode mode, bool updatesOnly)
    {
        var anyUpdates = false;
        foreach (var environment in report.Environments)
        {
            Output.Write(new Rule($"[bold]{Styles.Escape(report.Endpoint)}[/] [grey]›[/] {Styles.Escape(environment.EnvironmentName)} [grey](env {environment.EnvironmentId})[/]")
                .LeftJustified().RuleStyle("grey"));

            if (environment.Error is not null)
            {
                Output.MarkupLine($"[red]✗ {Styles.Escape(environment.Error)}[/]");
                continue;
            }

            var rows = updatesOnly
                ? environment.Containers.Where(r => r.Status is not (UpdateStatus.UpToDate or UpdateStatus.LocalImage or UpdateStatus.SystemContainer or UpdateStatus.Disabled)).ToList()
                : environment.Containers.ToList();
            anyUpdates |= environment.Containers.Any(r => r.Status is UpdateStatus.UpdateAvailable or UpdateStatus.Pending);

            if (rows.Count > 0)
            {
                var table = Styles.NewTable("Container", "Image", "Status", mode == UpdateCheckMode.Fresh ? "Details" : "Checked");
                foreach (var row in rows)
                {
                    table.AddRow(
                        $"[bold]{Styles.Escape(row.ContainerName)}[/]",
                        Styles.Escape(row.Image),
                        Styles.UpdateStatus(row),
                        Details(row));
                }

                Output.Write(table);
            }

            Output.Info(mode switch
            {
                UpdateCheckMode.Fresh => $"{environment.UpdatesFound ?? 0} of {environment.Total ?? environment.Containers.Count} container(s) have an image update.",
                UpdateCheckMode.Cached => $"{environment.Containers.Count} cached update record(s) from the last check.",
                _ => $"{environment.UpdatesFound ?? 0} update(s) ready, {environment.Containers.Count} pending record(s).",
            });
        }

        if (anyUpdates)
        {
            Output.Info($"Apply with: dockhand -{report.Endpoint} --env <env> update container <container>");
        }
    }

    private static string Details(UpdateRow row)
    {
        if (row.Error is not null)
        {
            return $"[red]{Styles.Escape(row.Error)}[/]";
        }

        var parts = new List<string>();
        if (row.CurrentDigest is not null || row.NewDigest is not null)
        {
            parts.Add(row.Status == UpdateStatus.UpdateAvailable && row.NewDigest is not null
                ? $"{Styles.ShortDigest(row.CurrentDigest)} → [yellow]{Styles.ShortDigest(row.NewDigest)}[/]"
                : $"[grey]{Styles.ShortDigest(row.CurrentDigest)}[/]");
        }

        if (row.NewerVersionTag is not null)
        {
            parts.Add($"newer tag [cyan]{Styles.Escape(row.NewerVersionTag)}[/]");
        }

        if (row.CheckedAt is not null)
        {
            parts.Add($"[grey]{Styles.Escape(row.CheckedAt)}[/]");
        }

        return parts.Count == 0 ? "" : string.Join(" ", parts);
    }
}

// ---- check apply update docker / update apply ---------------------------------------------------

internal sealed class ApplyUpdateSettings : GlobalSettings
{
    [CommandArgument(0, "<CONTAINER>")]
    [Description("Container name or id")]
    public string Container { get; init; } = "";

    [CommandOption("--dry-run")]
    [Description("Show what would happen without sending any request that changes something")]
    public bool DryRun { get; init; }

    [CommandOption("-y|--yes")]
    [Description("Do not ask for confirmation")]
    public bool Yes { get; init; }
}

/// <summary>Registered as both <c>check apply update docker</c> and <c>update apply</c>.</summary>
internal sealed class ApplyUpdateCommand(CommandServices services) : DockhandCommand<ApplyUpdateSettings>(services)
{
    protected override async Task<int> RunAsync(ApplyUpdateSettings settings, CancellationToken cancellationToken)
    {
        var (api, environment) = await ConnectToEnvironmentAsync(cancellationToken);
        var plan = await Output.StatusAsync("Resolving container…",
            () => Services.Updates.PlanAsync(api, environment, settings.Container, cancellationToken));

        if (!Output.IsJson)
        {
            RenderPlan(plan, settings.DryRun);
        }

        var outcome = await Services.Updates.ApplyAsync(
            api, plan, settings.DryRun, settings.Yes, Services.Prompt,
            request => Output.StatusAsync($"Recreating {plan.Container.Name} with its latest image…", () => request),
            cancellationToken);

        if (Output.IsJson)
        {
            Output.Json(new
            {
                endpoint = plan.Endpoint,
                environmentId = plan.Environment.Id,
                environmentName = plan.Environment.Name,
                container = new { id = plan.Container.Id, name = plan.Container.Name, image = plan.Container.Image, state = plan.Container.State },
                pendingUpdate = plan.PendingUpdate,
                outcome.DryRun,
                outcome.Executed,
                outcome.Success,
                outcome.Error,
                outcome.Warnings,
            });
        }
        else if (outcome.DryRun)
        {
            Output.MarkupLine($"[yellow]Dry run:[/] no request was sent. Without --dry-run this calls POST /api/containers/batch-update?env={plan.Environment.Id} with container id {Styles.Escape(ContainerResolver.ShortId(plan.Container.Id))}.");
        }
        else
        {
            foreach (var warning in outcome.Warnings)
            {
                Output.Warning($"{warning.Status}: {warning.Message}");
            }

            if (outcome.Success)
            {
                Output.Success($"Dockhand reports that '{plan.Container.Name}' was recreated with its latest image.");
            }
        }

        if (!outcome.Success)
        {
            throw new DockhandException(ExitCodes.ApiError, $"Updating '{plan.Container.Name}' failed: {outcome.Error}");
        }

        return ExitCodes.Success;
    }

    private void RenderPlan(UpdatePlan plan, bool dryRun)
    {
        var pending = plan.PendingUpdate switch
        {
            null when !plan.PendingStateKnown => "[grey]unknown (pending updates could not be read)[/]",
            null => "[grey]none recorded; the image is still re-pulled and the container recreated[/]",
            { HasImageUpdate: true } p => $"[yellow]yes[/]{(p.CheckedAt is null ? "" : $" [grey](checked {Styles.Escape(p.CheckedAt)})[/]")}",
            { ReleaseAgeRemainingHours: > 0 } p => $"[blue]held[/] [grey]({p.ReleaseAgeRemainingHours} h of minimum release age left)[/]",
            { NewerVersion.Tag: { } tag } => $"[cyan]newer tag {Styles.Escape(tag)} available[/] [grey](tags are not changed by this update)[/]",
            _ => "[grey]recorded, without a new image for the current tag[/]",
        };

        var grid = new Grid().AddColumn(new GridColumn().NoWrap()).AddColumn();
        grid.AddRow("[grey]Endpoint[/]", $"[bold]{Styles.Escape(plan.Endpoint)}[/]");
        grid.AddRow("[grey]Environment[/]", $"{Styles.Escape(plan.Environment.Name)} [grey]({plan.Environment.Id})[/]");
        grid.AddRow("[grey]Container[/]", $"[bold]{Styles.Escape(plan.Container.Name)}[/] [grey]({Styles.Escape(ContainerResolver.ShortId(plan.Container.Id))})[/]");
        grid.AddRow("[grey]Image[/]", Styles.Escape(plan.Container.Image));
        grid.AddRow("[grey]State[/]", Styles.ContainerState(plan.Container.State));
        grid.AddRow("[grey]Pending update[/]", pending);
        grid.AddRow("[grey]Action[/]", "Pull the latest image and recreate the container (POST /api/containers/batch-update). Dockhand states that container settings are preserved.");

        Output.Write(new Panel(grid)
            .Header(dryRun ? "Update plan [yellow](dry run)[/]" : "Update plan")
            .BorderColor(dryRun ? Color.Yellow : Color.Grey));
    }
}

// ---- update container: check one container, then update it when a new image exists ---------------

internal sealed class UpdateContainerSettings : GlobalSettings
{
    [CommandArgument(0, "<CONTAINER>")]
    [Description("Container name or id")]
    public string Container { get; init; } = "";

    [CommandOption("--check-only")]
    [Description("Only check for a new image; never update")]
    public bool CheckOnly { get; init; }

    [CommandOption("--force")]
    [Description("Update even when the check finds no new image (re-pulls the tag and recreates the container)")]
    public bool Force { get; init; }

    [CommandOption("-y|--yes")]
    [Description("Do not ask for confirmation")]
    public bool Yes { get; init; }
}

/// <summary>
/// The scripted form of "open the environment in Dockhand, check for updates, update": a fresh check of the
/// container's environment, then <c>batch-update</c> for this container when Dockhand reports a new image.
/// </summary>
internal sealed class UpdateContainerCommand(CommandServices services) : DockhandCommand<UpdateContainerSettings>(services)
{
    protected override async Task<int> RunAsync(UpdateContainerSettings settings, CancellationToken cancellationToken)
    {
        if (settings.CheckOnly && settings.Force)
        {
            throw DockhandException.Usage("Use either --check-only or --force, not both.");
        }

        var api = await ConnectAsync(cancellationToken);
        var environments = await api.GetEnvironmentsAsync(cancellationToken);
        var environment = EnvironmentResolver.Select(environments, Options.Environment, api.Endpoint);
        var plan = await Output.StatusAsync("Resolving container…",
            () => Services.Updates.PlanAsync(api, environment, settings.Container, cancellationToken));
        var command = CommandLines.UpdateContainer(api.Endpoint.Alias, CommandLines.EnvironmentSelector(environment, environments), plan.Container);

        var check = await Output.StatusAsync($"Checking {environment.Name} for image updates…",
            () => Services.Updates.CheckContainerAsync(api, plan, null, cancellationToken));
        var updateAvailable = check?.Status == UpdateStatus.UpdateAvailable;

        if (!Output.IsJson)
        {
            RenderCheck(plan, check);
        }

        UpdateOutcome? outcome = null;
        if (!settings.CheckOnly && (updateAvailable || settings.Force))
        {
            outcome = await Services.Updates.ApplyAsync(
                api, plan, dryRun: false, settings.Yes, Services.Prompt,
                request => Output.StatusAsync($"Recreating {plan.Container.Name} with its latest image…", () => request),
                cancellationToken);
        }

        if (Output.IsJson)
        {
            Output.Json(new
            {
                endpoint = plan.Endpoint,
                environmentId = plan.Environment.Id,
                environmentName = plan.Environment.Name,
                container = new { id = plan.Container.Id, name = plan.Container.Name, image = plan.Container.Image, state = plan.Container.State },
                check,
                updateAvailable,
                updated = outcome?.Success == true,
                outcome?.Error,
                warnings = outcome?.Warnings,
                command,
            });
        }
        else
        {
            RenderResult(plan, check, outcome, settings.CheckOnly, command);
        }

        if (outcome is { Success: false })
        {
            throw new DockhandException(ExitCodes.ApiError, $"Updating '{plan.Container.Name}' failed: {outcome.Error}");
        }

        if (outcome is null && check?.Status == UpdateStatus.Error)
        {
            throw new DockhandException(ExitCodes.ApiError,
                $"Dockhand could not check '{plan.Container.Name}' for updates: {check.Error}. Use --force to update anyway.");
        }

        return ExitCodes.Success;
    }

    private void RenderCheck(UpdatePlan plan, UpdateRow? check)
    {
        var grid = new Grid().AddColumn(new GridColumn().NoWrap()).AddColumn();
        grid.AddRow("[grey]Endpoint[/]", $"[bold]{Styles.Escape(plan.Endpoint)}[/]");
        grid.AddRow("[grey]Environment[/]", $"{Styles.Escape(plan.Environment.Name)} [grey]({plan.Environment.Id})[/]");
        grid.AddRow("[grey]Container[/]", $"[bold]{Styles.Escape(plan.Container.Name)}[/] [grey]({Styles.Escape(ContainerResolver.ShortId(plan.Container.Id))})[/]");
        grid.AddRow("[grey]Image[/]", Styles.Escape(plan.Container.Image));
        grid.AddRow("[grey]State[/]", Styles.ContainerState(plan.Container.State));
        grid.AddRow("[grey]Update check[/]", check is null ? "[grey]not checked by Dockhand (hidden or excluded container)[/]" : Styles.UpdateStatus(check));

        if (check?.CurrentDigest is not null || check?.NewDigest is not null)
        {
            grid.AddRow("[grey]Digest[/]", check.NewDigest is not null && check.Status == UpdateStatus.UpdateAvailable
                ? $"{Styles.ShortDigest(check.CurrentDigest)} → [yellow]{Styles.ShortDigest(check.NewDigest)}[/]"
                : Styles.ShortDigest(check.CurrentDigest));
        }

        if (check?.NewerVersionTag is not null)
        {
            grid.AddRow("[grey]Newer tag[/]", $"[cyan]{Styles.Escape(check.NewerVersionTag)}[/] [grey](advisory; this update keeps the current tag)[/]");
        }

        if (check?.Error is not null)
        {
            grid.AddRow("[grey]Error[/]", $"[red]{Styles.Escape(check.Error)}[/]");
        }

        Output.Write(new Panel(grid).Header("Update check").BorderColor(Color.Grey));
    }

    private void RenderResult(UpdatePlan plan, UpdateRow? check, UpdateOutcome? outcome, bool checkOnly, string command)
    {
        if (outcome is not null)
        {
            foreach (var warning in outcome.Warnings)
            {
                Output.Warning($"{warning.Status}: {warning.Message}");
            }

            if (outcome.Success)
            {
                Output.Success($"Dockhand reports that '{plan.Container.Name}' was recreated with its latest image.");
            }
        }
        else if (check?.Status == UpdateStatus.UpdateAvailable)
        {
            Output.MarkupLine($"[yellow]A new image is available for '{Styles.Escape(plan.Container.Name)}'.[/] Update with:");
            Output.Text($"  {command} --yes");
            return;
        }
        else if (check?.Status != UpdateStatus.Error)
        {
            Output.Success($"'{plan.Container.Name}' is already running the latest image for its tag; nothing to update.");
            if (!checkOnly)
            {
                Output.Info("Pushed a new image but nothing was found? Add --force to re-pull and recreate anyway.");
            }
        }

        Output.Info($"Command line for scripts: {command} --yes");
    }
}
