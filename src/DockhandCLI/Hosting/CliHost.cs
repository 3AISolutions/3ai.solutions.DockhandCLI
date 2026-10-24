using DockhandCLI.Commands;
using DockhandCLI.Core.Interfaces;
using DockhandCLI.Core.Services;
using DockhandCLI.Infrastructure;
using DockhandCLI.Infrastructure.Configuration;
using DockhandCLI.Presentation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DockhandCLI.Hosting;

internal sealed class CliHostOptions
{
    public ConfigPaths? ConfigPaths { get; init; }

    public Func<HttpMessageHandler>? HttpHandler { get; init; }

    /// <summary>Last chance to replace services (tests).</summary>
    public Action<IServiceCollection>? Configure { get; init; }
}

internal static class CliHost
{
    public static ServiceProvider BuildServices(CliHostOptions? options = null)
    {
        options ??= new CliHostOptions();
        var services = new ServiceCollection();

        var context = new InvocationContext();
        services.AddSingleton(context);
        services.AddLogging(logging => logging
            .ClearProviders()
            .SetMinimumLevel(LogLevel.Trace)
            .AddProvider(new StderrLoggerProvider(context)));

        services.AddDockhandInfrastructure(options.ConfigPaths, options.HttpHandler);

        services.AddSingleton<IConsoleFactory, SystemConsoleFactory>();
        services.AddSingleton<EndpointService>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<SpectreUserPrompt>();
        services.AddSingleton<IUserPrompt>(sp => sp.GetRequiredService<SpectreUserPrompt>());
        services.AddSingleton<OutputWriter>();
        services.AddSingleton<CommandServices>();
        services.AddSingleton<CliApplication>();
        services.AddSingleton<Shell.ILineReaderFactory, Shell.ConsoleLineReaderFactory>();

        options.Configure?.Invoke(services);
        return services.BuildServiceProvider();
    }
}
