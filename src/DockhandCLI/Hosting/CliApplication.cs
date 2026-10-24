using System.Reflection;
using DockhandCLI.Commands;
using DockhandCLI.Core.Interfaces;
using DockhandCLI.Core.Models;
using DockhandCLI.Core.Services;
using DockhandCLI.Infrastructure.Api;
using DockhandCLI.Presentation;
using DockhandCLI.Shell;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace DockhandCLI.Hosting;

/// <summary>
/// Executes command lines. Direct mode and the interactive shell both go through
/// <see cref="ExecuteAsync"/>, so they share option parsing, endpoint selection and command handlers.
/// </summary>
internal sealed class CliApplication(
    IServiceProvider services,
    InvocationContext context,
    IConsoleFactory consoleFactory,
    IConfigStore store,
    ApiClientOptions apiOptions)
{
    public static string Version { get; } =
        typeof(CliApplication).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";

    /// <summary>Process entry point: no command starts the interactive shell.</summary>
    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var parsed = await ParseAsync(args, cancellationToken);
        if (parsed is null)
        {
            return ExitCodes.Usage;
        }

        if (parsed.Remaining.Count == 0)
        {
            var shell = ActivatorUtilities.CreateInstance<InteractiveShell>(services, this);
            return await shell.RunAsync(parsed.Options, cancellationToken);
        }

        return await ExecuteParsedAsync(parsed, cancellationToken);
    }

    /// <summary>Runs one command line and returns its exit code. Never throws for command failures.</summary>
    public async Task<int> ExecuteAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var parsed = await ParseAsync(args, cancellationToken);
        return parsed is null ? ExitCodes.Usage : await ExecuteParsedAsync(parsed, cancellationToken);
    }

    private async Task<ParsedArguments?> ParseAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        CliConfiguration? config = null;
        try
        {
            config = await store.LoadAsync(cancellationToken);
        }
        catch (DockhandException)
        {
            // Reported by the command that needs the configuration.
        }

        try
        {
            return GlobalOptionParser.Parse(args, alias => config?.FindEndpoint(alias) is not null);
        }
        catch (DockhandException ex)
        {
            ErrorRenderer.Render(consoleFactory.Create(GlobalOptions.Default), ex, verbose: false);
            return null;
        }
    }

    private async Task<int> ExecuteParsedAsync(ParsedArguments parsed, CancellationToken cancellationToken)
    {
        context.Options = parsed.Options;
        context.Console = consoleFactory.Create(parsed.Options);
        apiOptions.TimeoutOverride = parsed.Options.Timeout;

        var app = new CommandApp(new TypeRegistrar(services));
        app.Configure(config =>
        {
            config.SetApplicationName("dockhand");
            config.SetApplicationVersion(Version);
            config.ConfigureConsole(context.Console.Output);
            config.PropagateExceptions();
            config.UseStrictParsing();
            config.CaseSensitivity(CaseSensitivity.None);
            config.Settings.CancellationExitCode = ExitCodes.Cancelled;
            CommandRegistration.Register(config);
        });

        try
        {
            return await app.RunAsync(parsed.Remaining, cancellationToken);
        }
        catch (Exception ex)
        {
            return ErrorRenderer.Render(context.Console, ex, parsed.Options.Verbose);
        }
    }
}
