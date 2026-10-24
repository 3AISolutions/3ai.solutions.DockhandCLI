using System.ComponentModel;
using System.Globalization;
using DockhandCLI.Core.Models;
using DockhandCLI.Core.Services;
using DockhandCLI.Presentation;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DockhandCLI.Commands;

internal sealed class EnvListCommand(CommandServices services) : DockhandCommand<GlobalSettings>(services)
{
    protected override async Task<int> RunAsync(GlobalSettings settings, CancellationToken cancellationToken)
    {
        var api = await ConnectAsync(cancellationToken);
        var environments = await Output.StatusAsync("Loading environments…", () => api.GetEnvironmentsAsync(cancellationToken));

        if (Output.IsJson)
        {
            Output.Json(environments);
            return ExitCodes.Success;
        }

        var table = Styles.NewTable("", "Id", "Name", "Connection", "Host", "Public IP", "Timezone");
        foreach (var environment in environments.OrderBy(e => e.Id))
        {
            var host = environment.Host is null ? "" : $"{environment.Host}{(environment.Port is { } port ? $":{port}" : "")}";
            table.AddRow(
                api.Endpoint.DefaultEnvironment == environment.Id ? "[yellow]★[/]" : "",
                environment.Id.ToString(CultureInfo.InvariantCulture),
                $"[bold]{Styles.Escape(environment.Name)}[/]",
                Styles.Escape(environment.ConnectionType),
                Styles.Dim(host),
                Styles.Dim(environment.PublicIp),
                Styles.Dim(environment.Timezone));
        }

        Output.MarkupLine($"[grey]Environments on[/] [bold]{Styles.Escape(api.Endpoint.Alias)}[/]");
        Output.Write(table);
        if (api.Endpoint.DefaultEnvironment is null && environments.Count > 1)
        {
            Output.Info($"No default environment saved. Set one with: dockhand endpoint set-env {api.Endpoint.Alias} <id>");
        }

        return ExitCodes.Success;
    }
}

internal sealed class EnvShowSettings : GlobalSettings
{
    [CommandArgument(0, "<ENVIRONMENT>")]
    [Description("Environment id or name")]
    public string Environment { get; init; } = "";
}

internal sealed class EnvShowCommand(CommandServices services) : DockhandCommand<EnvShowSettings>(services)
{
    protected override async Task<int> RunAsync(EnvShowSettings settings, CancellationToken cancellationToken)
    {
        var api = await ConnectAsync(cancellationToken);
        var environments = await Output.StatusAsync("Loading environment…", () => api.GetEnvironmentsAsync(cancellationToken));
        var match = EnvironmentResolver.Match(environments, settings.Environment, api.Endpoint.Alias);
        var environment = await api.GetEnvironmentAsync(match.Id, cancellationToken);

        if (Output.IsJson)
        {
            Output.Json(environment);
            return ExitCodes.Success;
        }

        var grid = new Grid().AddColumn(new GridColumn().NoWrap()).AddColumn();
        grid.AddRow("[grey]Id[/]", environment.Id.ToString(CultureInfo.InvariantCulture));
        grid.AddRow("[grey]Name[/]", $"[bold]{Styles.Escape(environment.Name)}[/]");
        grid.AddRow("[grey]Connection[/]", Styles.Escape(environment.ConnectionType));
        grid.AddRow("[grey]Host[/]", Styles.Dim(environment.Host));
        grid.AddRow("[grey]Port[/]", Styles.Dim(environment.Port?.ToString(CultureInfo.InvariantCulture)));
        grid.AddRow("[grey]Protocol[/]", Styles.Dim(environment.Protocol));
        grid.AddRow("[grey]Public IP[/]", Styles.Dim(environment.PublicIp));
        grid.AddRow("[grey]Timezone[/]", Styles.Dim(environment.Timezone));
        grid.AddRow("[grey]TLS key stored[/]", environment.HasTlsKey == true ? "yes" : "no");
        grid.AddRow("[grey]Hawser token stored[/]", environment.HasHawserToken == true ? "yes" : "no");
        if (environment.AdditionalProperties is { Count: > 0 } extra)
        {
            foreach (var (key, value) in extra.Where(p => p.Value.ValueKind is not (System.Text.Json.JsonValueKind.Object or System.Text.Json.JsonValueKind.Array)))
            {
                grid.AddRow($"[grey]{Styles.Escape(key)}[/]", Styles.Escape(value.ToString()));
            }
        }

        Output.Write(new Panel(grid).Header($"{Styles.Escape(api.Endpoint.Alias)} / environment {environment.Id}").BorderColor(Color.Grey));
        return ExitCodes.Success;
    }
}
