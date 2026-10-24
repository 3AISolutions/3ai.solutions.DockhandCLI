using System.ComponentModel;
using System.Text.Json;
using DockhandCLI.Core.Interfaces;
using DockhandCLI.Core.Models;
using DockhandCLI.Core.Services;
using DockhandCLI.Presentation;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DockhandCLI.Commands;

internal sealed class ImageListCommand(CommandServices services) : DockhandCommand<GlobalSettings>(services)
{
    protected override async Task<int> RunAsync(GlobalSettings settings, CancellationToken cancellationToken)
    {
        var (api, environment) = await ConnectToEnvironmentAsync(cancellationToken);
        var images = await Output.StatusAsync("Loading images…", () => api.GetImagesAsync(environment.Id, cancellationToken));

        if (Output.IsJson)
        {
            Output.Json(images);
            return ExitCodes.Success;
        }

        var table = Styles.NewTable("Tags", "Id", "Size", "Created");
        foreach (var image in images.OrderBy(i => i.RepoTags?.FirstOrDefault() ?? "~", StringComparer.OrdinalIgnoreCase))
        {
            var tags = image.RepoTags is { Count: > 0 } list ? string.Join("\n", list.Select(Styles.Escape)) : "[grey]<none>[/]";
            table.AddRow(tags, Styles.Dim(Styles.ShortDigest(image.Id)), Styles.Bytes(image.Size), Styles.UnixTime(image.Created));
        }

        Output.MarkupLine($"[grey]Images on[/] [bold]{Styles.Escape(Where(api, environment))}[/]");
        Output.Write(table);
        Output.Info($"{images.Count} image(s), {Styles.Bytes(images.Sum(i => i.Size ?? 0))} total.");
        return ExitCodes.Success;
    }
}

internal sealed class StackListCommand(CommandServices services) : DockhandCommand<GlobalSettings>(services)
{
    protected override async Task<int> RunAsync(GlobalSettings settings, CancellationToken cancellationToken)
    {
        var (api, environment) = await ConnectToEnvironmentAsync(cancellationToken);
        var stacks = await Output.StatusAsync("Loading stacks…", () => api.GetStacksAsync(environment.Id, cancellationToken));

        if (Output.IsJson)
        {
            Output.Json(stacks);
            return ExitCodes.Success;
        }

        var table = Styles.NewTable("Name", "Status", "Containers", "Source");
        foreach (var stack in stacks.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            table.AddRow(
                $"[bold]{Styles.Escape(stack.Name)}[/]",
                Styles.StackStatus(stack.Status),
                (stack.Containers?.Count ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
                Styles.Dim(stack.SourceType));
        }

        Output.MarkupLine($"[grey]Stacks on[/] [bold]{Styles.Escape(Where(api, environment))}[/]");
        Output.Write(table);
        return ExitCodes.Success;
    }
}

internal class StackSettings : GlobalSettings
{
    [CommandArgument(0, "<STACK>")]
    [Description("Stack name (from 'dockhand stacks list')")]
    public string Stack { get; init; } = "";
}

internal sealed class StackShowCommand(CommandServices services) : DockhandCommand<StackSettings>(services)
{
    protected override async Task<int> RunAsync(StackSettings settings, CancellationToken cancellationToken)
    {
        var (api, environment) = await ConnectToEnvironmentAsync(cancellationToken);
        var stacks = await api.GetStacksAsync(environment.Id, cancellationToken);
        var stack = stacks.FirstOrDefault(s => s.Name == settings.Stack)
            ?? throw DockhandException.NotFound($"No stack named '{settings.Stack}' on {Where(api, environment)}.");

        StackCompose? compose = null;
        string? composeError = null;
        try
        {
            compose = await api.GetStackComposeAsync(environment.Id, stack.Name, cancellationToken);
        }
        catch (ApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            composeError = ex.ApiMessage ?? "No compose file found.";
        }

        if (Output.IsJson)
        {
            Output.Json(new { stack, compose, composeError });
            return ExitCodes.Success;
        }

        var grid = new Grid().AddColumn(new GridColumn().NoWrap()).AddColumn();
        grid.AddRow("[grey]Name[/]", $"[bold]{Styles.Escape(stack.Name)}[/]");
        grid.AddRow("[grey]Status[/]", Styles.StackStatus(stack.Status));
        grid.AddRow("[grey]Source[/]", Styles.Dim(stack.SourceType));
        grid.AddRow("[grey]Containers[/]", stack.Containers is { Count: > 0 } c ? Styles.Escape(string.Join(", ", c)) : "[grey]none[/]");
        grid.AddRow("[grey]Compose file[/]", Styles.Dim(compose?.ComposePath ?? composeError));
        grid.AddRow("[grey]Env file[/]", Styles.Dim(compose?.EnvPath));
        Output.Write(new Panel(grid).Header($"Stack {Styles.Escape(stack.Name)}").BorderColor(Color.Grey));

        if (compose?.Content is { } content)
        {
            Output.Write(new Panel(new Text(content)).Header("compose").BorderColor(Color.Grey));
        }

        return ExitCodes.Success;
    }
}

internal sealed class StackActionSettings : StackSettings
{
    [CommandOption("-y|--yes")]
    [Description("Do not ask for confirmation")]
    public bool Yes { get; init; }

    [CommandOption("--pull")]
    [Description("deploy: pull images first")]
    public bool Pull { get; init; }

    [CommandOption("--build")]
    [Description("deploy: build images first")]
    public bool Build { get; init; }

    [CommandOption("--force-recreate")]
    [Description("deploy: recreate containers even if unchanged")]
    public bool ForceRecreate { get; init; }

    [CommandOption("--remove-volumes")]
    [Description("down: also remove named volumes (data loss)")]
    public bool RemoveVolumes { get; init; }

    [CommandOption("--mode <MODE>")]
    [Description("restart: restart, ordered or recreate")]
    public string? Mode { get; init; }
}

/// <summary>Stack lifecycle through Dockhand jobs. Everything except start asks for confirmation.</summary>
internal abstract class StackActionCommand(CommandServices services, StackAction action) : DockhandCommand<StackActionSettings>(services)
{
    protected override async Task<int> RunAsync(StackActionSettings settings, CancellationToken cancellationToken)
    {
        Validate(settings);
        var (api, environment) = await ConnectToEnvironmentAsync(cancellationToken);
        var verb = action.ToString().ToLowerInvariant();

        if (action != StackAction.Start)
        {
            var question = action == StackAction.Down && settings.RemoveVolumes
                ? $"Take stack '{settings.Stack}' down on {Where(api, environment)} AND delete its named volumes? This destroys data."
                : $"{action} stack '{settings.Stack}' on {Where(api, environment)}?";
            await Confirmation.EnsureAsync(Services.Prompt, settings.Yes, question, cancellationToken);
        }

        var options = new StackActionOptions
        {
            Pull = settings.Pull,
            Build = settings.Build,
            ForceRecreate = settings.ForceRecreate,
            RemoveVolumes = settings.RemoveVolumes,
            RestartMode = settings.Mode,
        };

        var result = await RunWithProgressAsync(api, environment, settings.Stack, options, cancellationToken);
        var success = result.Success == true;

        if (Output.IsJson)
        {
            Output.Json(new { stack = settings.Stack, environmentId = environment.Id, action = verb, success, output = result.Output, error = result.Error });
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(result.Output) && (Options.Verbose || !success))
            {
                Output.Write(new Panel(new Text(result.Output.TrimEnd())).Header("output").BorderColor(Color.Grey));
            }

            if (success)
            {
                Output.Success($"{action} of stack '{settings.Stack}' on {Where(api, environment)} completed.");
            }
        }

        if (!success)
        {
            throw new DockhandException(ExitCodes.ApiError, $"{action} of stack '{settings.Stack}' failed: {result.Error ?? "the API reported a failure"}");
        }

        return ExitCodes.Success;
    }

    private void Validate(StackActionSettings settings)
    {
        if (action != StackAction.Deploy && (settings.Pull || settings.Build || settings.ForceRecreate))
        {
            throw DockhandException.Usage("--pull, --build and --force-recreate only apply to 'stacks deploy'.");
        }

        if (action != StackAction.Down && settings.RemoveVolumes)
        {
            throw DockhandException.Usage("--remove-volumes only applies to 'stacks down'.");
        }

        if (settings.Mode is not null && (action != StackAction.Restart || settings.Mode is not ("restart" or "ordered" or "recreate")))
        {
            throw DockhandException.Usage("--mode applies to 'stacks restart' and must be restart, ordered or recreate.");
        }
    }

    private Task<StackJobResult> RunWithProgressAsync(
        IDockhandApi api, DockerEnvironment environment, string stack, StackActionOptions options, CancellationToken cancellationToken)
    {
        if (Output.IsJson || !Context.Console.InteractiveOutput)
        {
            return api.RunStackActionAsync(environment.Id, stack, action, options, null, cancellationToken);
        }

        return Context.Console.Output.Status().Spinner(Spinner.Known.Dots).StartAsync(
            Markup.Escape($"{action} {stack}…"),
            status => api.RunStackActionAsync(environment.Id, stack, action, options,
                new CallbackProgress<JobProgress>(progress =>
                {
                    if (ProgressText(progress) is { } text)
                    {
                        status.Status(Markup.Escape($"{action} {stack}: {Truncate(text)}"));
                    }
                }),
                cancellationToken));
    }

    private static string? ProgressText(JobProgress progress)
    {
        var data = progress.Data;
        if (data.ValueKind == JsonValueKind.String)
        {
            return data.GetString();
        }

        if (data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in new[] { "line", "message", "status", "output" })
        {
            if (data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static string Truncate(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length > 80 ? line[..80] + "…" : line;
    }
}

internal sealed class StackStartCommand(CommandServices services) : StackActionCommand(services, StackAction.Start);

internal sealed class StackStopCommand(CommandServices services) : StackActionCommand(services, StackAction.Stop);

internal sealed class StackRestartCommand(CommandServices services) : StackActionCommand(services, StackAction.Restart);

internal sealed class StackDeployCommand(CommandServices services) : StackActionCommand(services, StackAction.Deploy);

internal sealed class StackDownCommand(CommandServices services) : StackActionCommand(services, StackAction.Down);
