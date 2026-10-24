using DockhandCLI.Core.Interfaces;
using DockhandCLI.Core.Models;
using DockhandCLI.Hosting;
using DockhandCLI.Infrastructure.Api;
using DockhandCLI.Infrastructure.Configuration;
using DockhandCLI.Presentation;
using DockhandCLI.Shell;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Testing;

namespace DockhandCLI.IntegrationTests;

internal sealed record CliResult(int ExitCode, string Stdout, string Stderr);

/// <summary>Runs the real CLI pipeline against <see cref="FakeDockhandServer"/>, a temporary configuration directory and in-memory secrets.</summary>
internal sealed class CliTestHost : IDisposable, IConsoleFactory, ILineReaderFactory
{
    private readonly ServiceProvider _services;
    private TestConsole _out = NewConsole();
    private TestConsole _err = NewConsole();
    private StringWriter _raw = new();
    private string _stdin = "";

    public CliTestHost()
    {
        ConfigDirectory = Path.Combine(Path.GetTempPath(), "dockhand-it", Guid.NewGuid().ToString("N"));
        _services = CliHost.BuildServices(new CliHostOptions
        {
            ConfigPaths = new ConfigPaths(ConfigDirectory),
            HttpHandler = () => Server,
            Configure = services =>
            {
                services.AddSingleton<ICredentialManager>(Credentials);
                services.AddSingleton<IConsoleFactory>(this);
                services.AddSingleton<ILineReaderFactory>(this);
            },
        });

        var api = _services.GetRequiredService<ApiClientOptions>();
        api.JobPollInterval = TimeSpan.FromMilliseconds(1);
    }

    public string ConfigDirectory { get; }

    public FakeDockhandServer Server { get; } = new();

    public MemoryCredentials Credentials { get; } = new();

    public bool CanPrompt { get; set; }

    public IConfigStore Store => _services.GetRequiredService<IConfigStore>();

    public async Task<CliResult> RunAsync(params string[] args) => await RunAsync(args, stdin: "");

    public async Task<CliResult> RunAsync(string[] args, string stdin, string? keys = null)
    {
        _out = NewConsole();
        _err = NewConsole();
        _raw = new StringWriter();
        _stdin = stdin;
        if (keys is not null)
        {
            foreach (var key in keys)
            {
                _out.Input.PushCharacter(key);
            }

            _out.Input.PushKey(ConsoleKey.Enter);
        }

        var exitCode = await _services.GetRequiredService<CliApplication>().RunAsync(args, CancellationToken.None);
        return new CliResult(exitCode, _out.Output + _raw, _err.Output);
    }

    /// <summary>Registers endpoints srv01 (docker01) and srv02 (docker02), both with tokens; srv01 is the default.</summary>
    public async Task WithEndpointsAsync()
    {
        await Store.UpdateAsync(config =>
        {
            config.Endpoints.Add(new EndpointConfig { Alias = "srv01", BaseUrl = "https://docker01.example.com", DefaultEnvironment = 1, CredentialRef = "memory:srv01" });
            config.Endpoints.Add(new EndpointConfig { Alias = "srv02", BaseUrl = "https://docker02.example.com", DefaultEnvironment = 1, CredentialRef = "memory:srv02" });
            config.DefaultEndpoint = "srv01";
            return 0;
        });
        Credentials.Tokens["srv01"] = "dh_srv01tokenAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        Credentials.Tokens["srv02"] = "dh_srv02tokenBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    }

    CliConsole IConsoleFactory.Create(GlobalOptions options) =>
        new(_out, _err, _raw, new StringReader(_stdin), CanPrompt, interactiveOutput: false);

    ILineReader ILineReaderFactory.Create(CliConsole console, ShellCompleter completer, Action<string> writePrompt) =>
        new StreamLineReader(new StringReader(_stdin));

    public void Dispose()
    {
        _services.Dispose();
        if (Directory.Exists(ConfigDirectory))
        {
            Directory.Delete(ConfigDirectory, recursive: true);
        }
    }

    private static TestConsole NewConsole()
    {
        var console = new TestConsole();
        console.Profile.Width = 250;
        console.Profile.Capabilities.Interactive = true;
        return console;
    }
}

internal sealed class MemoryCredentials : ICredentialManager
{
    public Dictionary<string, string> Tokens { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<string> StoreTokenAsync(string alias, string token, CancellationToken cancellationToken = default)
    {
        Tokens[alias] = token;
        return Task.FromResult($"memory:{alias}");
    }

    public Task<StoredToken?> GetTokenAsync(EndpointConfig endpoint, CancellationToken cancellationToken = default) =>
        Task.FromResult(endpoint.CredentialRef is not null && Tokens.TryGetValue(endpoint.Alias, out var token) ? new StoredToken(token, "memory") : null);

    public Task RemoveTokenAsync(EndpointConfig endpoint, CancellationToken cancellationToken = default)
    {
        Tokens.Remove(endpoint.Alias);
        return Task.CompletedTask;
    }

    public string? DescribeCredential(EndpointConfig endpoint) => endpoint.CredentialRef is null ? null : "test memory store";
}
