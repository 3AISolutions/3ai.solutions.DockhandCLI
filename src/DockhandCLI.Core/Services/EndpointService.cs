using DockhandCLI.Core.Interfaces;
using DockhandCLI.Core.Models;

namespace DockhandCLI.Core.Services;

public sealed record NewEndpoint(
    string Alias,
    string Url,
    bool AllowHttp = false,
    string? Description = null,
    int? DefaultEnvironment = null,
    int? TimeoutSeconds = null,
    bool MakeDefault = false);

/// <summary>Registration, removal and selection of saved endpoints.</summary>
public sealed class EndpointService(IConfigStore store, ICredentialManager credentials)
{
    public Task<CliConfiguration> LoadAsync(CancellationToken cancellationToken = default) => store.LoadAsync(cancellationToken);

    public async Task<EndpointConfig> AddAsync(NewEndpoint request, CancellationToken cancellationToken = default)
    {
        if (!AliasRules.IsValid(request.Alias, out var error))
        {
            throw DockhandException.Usage(error!);
        }

        if (request.TimeoutSeconds is <= 0)
        {
            throw DockhandException.Usage("--timeout must be a positive number of seconds.");
        }

        var url = EndpointUrl.Normalize(request.Url, request.AllowHttp);

        return await store.UpdateAsync(config =>
        {
            if (config.FindEndpoint(request.Alias) is { } existing)
            {
                throw DockhandException.Usage($"An endpoint with alias '{existing.Alias}' already exists. Remove it first or choose another alias.");
            }

            var endpoint = new EndpointConfig
            {
                Alias = request.Alias,
                BaseUrl = url,
                Description = request.Description,
                DefaultEnvironment = request.DefaultEnvironment,
                TimeoutSeconds = request.TimeoutSeconds,
            };
            config.Endpoints.Add(endpoint);

            if (request.MakeDefault || config.DefaultEndpoint is null)
            {
                config.DefaultEndpoint = endpoint.Alias;
            }

            return endpoint;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EndpointConfig> RemoveAsync(string alias, CancellationToken cancellationToken = default)
    {
        var removed = await store.UpdateAsync(config =>
        {
            var endpoint = Require(config, alias);
            config.Endpoints.Remove(endpoint);
            if (string.Equals(config.DefaultEndpoint, endpoint.Alias, StringComparison.OrdinalIgnoreCase))
            {
                config.DefaultEndpoint = config.Endpoints.Count == 1 ? config.Endpoints[0].Alias : null;
            }

            return endpoint;
        }, cancellationToken).ConfigureAwait(false);

        // Configuration first, then the secret: a failure here leaves an orphaned secret, never a dangling reference.
        await credentials.RemoveTokenAsync(removed, cancellationToken).ConfigureAwait(false);
        return removed;
    }

    public Task<EndpointConfig> SetDefaultAsync(string alias, CancellationToken cancellationToken = default) =>
        store.UpdateAsync(config =>
        {
            var endpoint = Require(config, alias);
            config.DefaultEndpoint = endpoint.Alias;
            return endpoint;
        }, cancellationToken);

    public Task<EndpointConfig> SetDefaultEnvironmentAsync(string alias, int? environmentId, CancellationToken cancellationToken = default) =>
        store.UpdateAsync(config =>
        {
            var endpoint = Require(config, alias);
            endpoint.DefaultEnvironment = environmentId;
            return endpoint;
        }, cancellationToken);

    public async Task<string> StoreTokenAsync(string alias, string token, CancellationToken cancellationToken = default)
    {
        var config = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var endpoint = Require(config, alias);
        var reference = await credentials.StoreTokenAsync(endpoint.Alias, token, cancellationToken).ConfigureAwait(false);
        await store.UpdateAsync(c => Require(c, alias).CredentialRef = reference, cancellationToken).ConfigureAwait(false);
        return reference;
    }

    public async Task RemoveTokenAsync(string alias, CancellationToken cancellationToken = default)
    {
        var config = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var endpoint = Require(config, alias);
        await credentials.RemoveTokenAsync(endpoint, cancellationToken).ConfigureAwait(false);
        await store.UpdateAsync(c => Require(c, alias).CredentialRef = null, cancellationToken).ConfigureAwait(false);
    }

    public static EndpointConfig Require(CliConfiguration config, string alias) =>
        config.FindEndpoint(alias) ?? throw UnknownAlias(config, alias);

    public static DockhandException UnknownAlias(CliConfiguration config, string alias)
    {
        var known = config.Endpoints.Count == 0
            ? "No endpoints are registered. Add one with 'dockhand endpoint add <alias> <url>'."
            : $"Registered endpoints: {string.Join(", ", config.Endpoints.Select(e => e.Alias))}.";
        return DockhandException.NotFound($"Unknown endpoint '{alias}'. {known}");
    }
}
