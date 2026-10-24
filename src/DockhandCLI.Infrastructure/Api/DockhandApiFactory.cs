using DockhandCLI.Core.Interfaces;
using DockhandCLI.Core.Models;
using Microsoft.Extensions.Logging;

namespace DockhandCLI.Infrastructure.Api;

internal sealed class DockhandApiFactory(
    IHttpClientFactory httpClientFactory,
    ICredentialManager credentials,
    ApiClientOptions options,
    ILoggerFactory loggerFactory) : IDockhandApiFactory
{
    public const string HttpClientName = "dockhand";

    public async Task<IDockhandApi> CreateAsync(EndpointConfig endpoint, CancellationToken cancellationToken = default)
    {
        var token = await credentials.GetTokenAsync(endpoint, cancellationToken).ConfigureAwait(false);
        return Create(endpoint, token?.Token);
    }

    public IDockhandApi Create(EndpointConfig endpoint, string? token) =>
        new DockhandApiClient(
            httpClientFactory.CreateClient(HttpClientName),
            endpoint,
            token,
            options,
            loggerFactory.CreateLogger<DockhandApiClient>());
}
