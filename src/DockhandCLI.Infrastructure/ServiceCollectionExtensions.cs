using System.Net;
using System.Reflection;
using DockhandCLI.Core.Interfaces;
using DockhandCLI.Infrastructure.Api;
using DockhandCLI.Infrastructure.Authentication;
using DockhandCLI.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DockhandCLI.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers configuration storage, credential storage and the Dockhand API client factory.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="paths">Configuration directory; defaults to the OS-specific location.</param>
    /// <param name="primaryHandler">Replaces the HTTP handler (used by tests to fake the API).</param>
    public static IServiceCollection AddDockhandInfrastructure(
        this IServiceCollection services,
        ConfigPaths? paths = null,
        Func<HttpMessageHandler>? primaryHandler = null)
    {
        services.AddSingleton(paths ?? ConfigPaths.FromEnvironment());
        services.AddSingleton<IConfigStore, JsonConfigStore>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<ICredentialManager, CredentialManager>();
        services.AddSingleton<ApiClientOptions>();
        services.AddSingleton<IDockhandApiFactory, DockhandApiFactory>();

        var version = typeof(ServiceCollectionExtensions).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";

        services.AddHttpClient(DockhandApiFactory.HttpClientName, client =>
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd($"DockhandCLI/{version}");
            })
            .ConfigurePrimaryHttpMessageHandler(primaryHandler ?? (() => new SocketsHttpHandler
            {
                // Cookies are handled per client (login sessions), never shared between endpoints.
                UseCookies = false,
                // Redirects could carry the Authorization header to another host; report them instead.
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            }))
            // The built-in HTTP logging is replaced by the client's own, which never logs headers.
            .RemoveAllLoggers();

        return services;
    }
}
