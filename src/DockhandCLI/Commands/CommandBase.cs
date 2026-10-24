using System.ComponentModel;
using DockhandCLI.Core.Interfaces;
using DockhandCLI.Core.Models;
using DockhandCLI.Core.Services;
using DockhandCLI.Hosting;
using DockhandCLI.Presentation;
using Spectre.Console.Cli;

namespace DockhandCLI.Commands;

/// <summary>
/// Options every command accepts. They are extracted by <see cref="GlobalOptionParser"/> before
/// Spectre.Console.Cli parses the command, so they work before or after the command name; they are
/// declared here so that <c>--help</c> lists them. Read them from <see cref="InvocationContext.Options"/>.
/// </summary>
internal class GlobalSettings : CommandSettings
{
    [CommandOption("--server <ALIAS>")]
    [Description("Endpoint to use (default: the saved default). Shorthand: [green]-<alias>[/], e.g. -srv01")]
    public string? Server { get; init; }

    [CommandOption("--env <ID|NAME>")]
    [Description("Docker environment (default: the endpoint's saved environment)")]
    public string? Env { get; init; }

    [CommandOption("-o|--output <FORMAT>")]
    [Description("Output format: [green]table[/] (default) or [green]json[/]")]
    public string? Output { get; init; }

    [CommandOption("--timeout <SECONDS>")]
    [Description("Request timeout, e.g. 30, 90s or 5m")]
    public string? Timeout { get; init; }

    [CommandOption("--verbose")]
    [Description("Write diagnostic details to stderr")]
    public bool Verbose { get; init; }

    [CommandOption("--no-color")]
    [Description("Disable colors and other terminal styling")]
    public bool NoColor { get; init; }
}

internal class ConfirmSettings : GlobalSettings
{
    [CommandOption("-y|--yes")]
    [Description("Do not ask for confirmation")]
    public bool Yes { get; init; }
}

/// <summary>Services shared by all commands.</summary>
internal sealed class CommandServices(
    InvocationContext context,
    IConfigStore store,
    IDockhandApiFactory apiFactory,
    ICredentialManager credentials,
    EndpointService endpoints,
    UpdateService updates,
    SpectreUserPrompt prompt,
    OutputWriter output)
{
    public InvocationContext Context { get; } = context;

    public IConfigStore Store { get; } = store;

    public IDockhandApiFactory ApiFactory { get; } = apiFactory;

    public ICredentialManager Credentials { get; } = credentials;

    public EndpointService Endpoints { get; } = endpoints;

    public UpdateService Updates { get; } = updates;

    public SpectreUserPrompt Prompt { get; } = prompt;

    public OutputWriter Output { get; } = output;
}

internal abstract class DockhandCommand<TSettings>(CommandServices services) : AsyncCommand<TSettings>
    where TSettings : GlobalSettings
{
    protected CommandServices Services { get; } = services;

    protected InvocationContext Context => Services.Context;

    protected GlobalOptions Options => Context.Options;

    protected OutputWriter Output => Services.Output;

    /// <summary>Only <c>check updates</c> supports <c>--all-envs</c>.</summary>
    protected virtual bool SupportsAllEnvironments => false;

    public sealed override async Task<int> ExecuteAsync(CommandContext context, TSettings settings, CancellationToken cancellationToken)
    {
        if (Options.AllEnvironments && !SupportsAllEnvironments)
        {
            throw DockhandException.Usage("--all-envs is only supported by 'check updates'.");
        }

        return await RunAsync(settings, cancellationToken);
    }

    protected abstract Task<int> RunAsync(TSettings settings, CancellationToken cancellationToken);

    protected async Task<EndpointConfig> ResolveEndpointAsync(CancellationToken cancellationToken)
    {
        var config = await Services.Store.LoadAsync(cancellationToken);
        return EndpointResolver.Resolve(config, Options.Server, Context.SessionEndpoint);
    }

    protected async Task<IDockhandApi> ConnectAsync(CancellationToken cancellationToken)
    {
        var endpoint = await ResolveEndpointAsync(cancellationToken);
        return await Services.ApiFactory.CreateAsync(endpoint, cancellationToken);
    }

    /// <summary>Resolves the endpoint and the single Docker environment the command targets.</summary>
    protected async Task<(IDockhandApi Api, DockerEnvironment Environment)> ConnectToEnvironmentAsync(CancellationToken cancellationToken)
    {
        var api = await ConnectAsync(cancellationToken);
        var environment = await EnvironmentResolver.ResolveAsync(api, Options.Environment, cancellationToken);
        return (api, environment);
    }

    protected static string Where(IDockhandApi api, DockerEnvironment environment) =>
        $"{api.Endpoint.Alias} / {environment.Name} ({environment.Id})";
}
