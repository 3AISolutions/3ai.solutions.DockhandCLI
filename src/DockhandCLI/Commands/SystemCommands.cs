using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using DockhandCLI.Core.Models;
using DockhandCLI.Core.Services;
using DockhandCLI.Presentation;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DockhandCLI.Commands;

internal sealed class JobSettings : GlobalSettings
{
    [CommandArgument(0, "<JOB_ID>")]
    [Description("Job id returned by a long-running operation")]
    public string JobId { get; init; } = "";
}

internal sealed class JobShowCommand(CommandServices services) : DockhandCommand<JobSettings>(services)
{
    protected override async Task<int> RunAsync(JobSettings settings, CancellationToken cancellationToken)
    {
        var api = await ConnectAsync(cancellationToken);
        var job = await api.GetJobAsync(settings.JobId, cancellationToken);

        if (Output.IsJson)
        {
            Output.Json(job);
            return ExitCodes.Success;
        }

        var status = job.Status switch
        {
            "running" => "[blue]running[/]",
            "done" => "[green]done[/]",
            "error" => "[red]error[/]",
            _ => Styles.Escape(job.Status),
        };

        var grid = new Grid().AddColumn(new GridColumn().NoWrap()).AddColumn();
        grid.AddRow("[grey]Job[/]", Styles.Escape(job.Id));
        grid.AddRow("[grey]Status[/]", status);
        grid.AddRow("[grey]Events[/]", job.Lines.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Output.Write(new Panel(grid).Header("Job").BorderColor(Color.Grey));

        if (job.Result is { } result)
        {
            Output.Write(new Panel(new Text(JsonSerializer.Serialize(result, CliJson.Options))).Header("result").BorderColor(Color.Grey));
        }

        return ExitCodes.Success;
    }
}

internal sealed class JobCancelCommand(CommandServices services) : DockhandCommand<JobSettings>(services)
{
    protected override async Task<int> RunAsync(JobSettings settings, CancellationToken cancellationToken)
    {
        var api = await ConnectAsync(cancellationToken);
        var cancelled = await api.CancelJobAsync(settings.JobId, cancellationToken);

        if (Output.IsJson)
        {
            Output.Json(new { jobId = settings.JobId, cancelled });
        }
        else if (cancelled)
        {
            Output.Success($"Cancellation requested for job {settings.JobId}.");
        }
        else
        {
            Output.Warning($"Job {settings.JobId} is not running, so it was not cancelled.");
        }

        return ExitCodes.Success;
    }
}

internal sealed class HealthCommand(CommandServices services) : DockhandCommand<GlobalSettings>(services)
{
    protected override async Task<int> RunAsync(GlobalSettings settings, CancellationToken cancellationToken)
    {
        var endpoint = await ResolveEndpointAsync(cancellationToken);
        var api = await Services.ApiFactory.CreateAsync(endpoint, cancellationToken);

        var stopwatch = Stopwatch.StartNew();
        var health = await api.GetHealthAsync(cancellationToken);
        var latency = stopwatch.ElapsedMilliseconds;

        DatabaseHealth? database = null;
        string? databaseError = null;
        try
        {
            database = await api.GetDatabaseHealthAsync(cancellationToken);
        }
        catch (DockhandException ex)
        {
            databaseError = ex.Message;
        }

        var healthy = string.Equals(health.Status, "ok", StringComparison.OrdinalIgnoreCase) && database?.Healthy != false;

        if (Output.IsJson)
        {
            Output.Json(new { endpoint = endpoint.Alias, baseUrl = endpoint.BaseUrl, healthy, latencyMs = latency, health, database, databaseError });
        }
        else
        {
            var table = Styles.NewTable("", "Check", "Result");
            table.AddRow(Styles.Check(health.Status == "ok"), "Service", $"{Styles.Escape(health.Status)} [grey]({latency} ms)[/]");
            if (database is not null)
            {
                table.AddRow(Styles.Check(database.Healthy), "Database",
                    $"{Styles.Escape(database.Database ?? "")} [grey]{database.AppliedMigrations} migrations applied, {database.PendingMigrations ?? 0} pending[/]");
            }
            else
            {
                table.AddRow("[grey]?[/]", "Database", Styles.Dim(databaseError));
            }

            Output.MarkupLine($"[bold]{Styles.Escape(endpoint.Alias)}[/] [grey]{Styles.Escape(endpoint.BaseUrl)}[/]");
            Output.Write(table);
        }

        return healthy ? ExitCodes.Success : ExitCodes.ApiError;
    }
}

internal sealed class SystemCommand(CommandServices services) : DockhandCommand<GlobalSettings>(services)
{
    protected override async Task<int> RunAsync(GlobalSettings settings, CancellationToken cancellationToken)
    {
        var api = await ConnectAsync(cancellationToken);

        // Docker details need an environment; without one the API still returns host and runtime info.
        int? environmentId = null;
        if (Options.Environment is not null || api.Endpoint.DefaultEnvironment is not null)
        {
            environmentId = (await EnvironmentResolver.ResolveAsync(api, Options.Environment, cancellationToken)).Id;
        }

        var info = await Output.StatusAsync("Loading system information…", () => api.GetSystemInfoAsync(environmentId, cancellationToken));

        if (Output.IsJson)
        {
            Output.Json(info);
            return ExitCodes.Success;
        }

        if (info.ValueKind != JsonValueKind.Object)
        {
            Output.Text(info.ToString());
            return ExitCodes.Success;
        }

        foreach (var section in info.EnumerateObject())
        {
            if (section.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var table = Styles.NewTable("Key", "Value");
            foreach (var property in section.Value.EnumerateObject())
            {
                var value = property.Value.ValueKind switch
                {
                    JsonValueKind.Object or JsonValueKind.Array => JsonSerializer.Serialize(property.Value),
                    JsonValueKind.Null => "-",
                    _ => property.Value.ToString(),
                };
                table.AddRow(Styles.Escape(property.Name), Styles.Escape(value.Length > 120 ? value[..120] + "…" : value));
            }

            Output.Write(new Rule($"[bold]{Styles.Escape(section.Name)}[/]").LeftJustified().RuleStyle("grey"));
            Output.Write(table);
        }

        return ExitCodes.Success;
    }
}
