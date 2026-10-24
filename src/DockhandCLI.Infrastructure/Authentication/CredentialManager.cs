using DockhandCLI.Core.Interfaces;
using DockhandCLI.Core.Models;
using DockhandCLI.Core.Services;
using DockhandCLI.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;

namespace DockhandCLI.Infrastructure.Authentication;

/// <summary>
/// Keeps tokens in the OS keyring, falling back to an owner-only file when no keyring is usable.
/// A <c>DOCKHAND_TOKEN_&lt;ALIAS&gt;</c> environment variable overrides the stored token (for CI).
/// </summary>
internal sealed class CredentialManager : ICredentialManager
{
    private readonly ISecretStore? _primary;
    private readonly ISecretStore _fallback;
    private readonly ILogger<CredentialManager> _logger;

    public CredentialManager(ConfigPaths paths, IProcessRunner runner, ILogger<CredentialManager> logger)
        : this(CreatePrimary(runner), new FileSecretStore(paths), logger)
    {
    }

    internal CredentialManager(ISecretStore? primary, ISecretStore fallback, ILogger<CredentialManager> logger)
    {
        _primary = primary;
        _fallback = fallback;
        _logger = logger;
    }

    public async Task<string> StoreTokenAsync(string alias, string token, CancellationToken cancellationToken = default)
    {
        if (!TokenFormat.IsValid(token))
        {
            throw DockhandException.Usage("That does not look like a Dockhand API token (expected 'dh_' followed by letters, digits, '-' or '_').");
        }

        if (_primary is not null && await _primary.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await _primary.SetAsync(alias, token, cancellationToken).ConfigureAwait(false);
                await _fallback.DeleteAsync(alias, cancellationToken).ConfigureAwait(false);
                return Reference(_primary, alias);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Could not use {Store} ({Reason}).", _primary.DisplayName, SecretRedactor.Redact(ex.Message));
            }
        }

        _logger.LogWarning(
            "No OS keyring is available; the token is stored in {Store}, readable only by your user account.", _fallback.DisplayName);
        await _fallback.SetAsync(alias, token, cancellationToken).ConfigureAwait(false);
        return Reference(_fallback, alias);
    }

    public async Task<StoredToken?> GetTokenAsync(EndpointConfig endpoint, CancellationToken cancellationToken = default)
    {
        var variable = TokenEnvironment.VariableFor(endpoint.Alias);
        var fromEnvironment = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return new StoredToken(fromEnvironment.Trim(), $"environment variable {variable}");
        }

        if (endpoint.CredentialRef is null)
        {
            return null;
        }

        var (store, key) = Parse(endpoint.CredentialRef);
        if (store is null || !await store.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            throw DockhandException.Config(
                $"The token for '{endpoint.Alias}' is in a credential store that is not available here ({endpoint.CredentialRef}). Run 'dockhand endpoint auth {endpoint.Alias}' again.");
        }

        try
        {
            var token = await store.GetAsync(key, cancellationToken).ConfigureAwait(false);
            return token is null ? null : new StoredToken(token, store.DisplayName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw DockhandException.Config($"Could not read the token for '{endpoint.Alias}' from {store.DisplayName}: {SecretRedactor.Redact(ex.Message)}", ex);
        }
    }

    public async Task RemoveTokenAsync(EndpointConfig endpoint, CancellationToken cancellationToken = default)
    {
        if (endpoint.CredentialRef is null)
        {
            return;
        }

        var (store, key) = Parse(endpoint.CredentialRef);
        if (store is null)
        {
            return;
        }

        try
        {
            await store.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Could not remove the stored token for {Alias} from {Store}: {Reason}", endpoint.Alias, store.DisplayName, ex.Message);
        }
    }

    public string? DescribeCredential(EndpointConfig endpoint)
    {
        var variable = TokenEnvironment.VariableFor(endpoint.Alias);
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
        {
            return $"environment variable {variable}";
        }

        if (endpoint.CredentialRef is null)
        {
            return null;
        }

        var (store, _) = Parse(endpoint.CredentialRef);
        return store?.DisplayName ?? endpoint.CredentialRef;
    }

    private (ISecretStore? Store, string Key) Parse(string reference)
    {
        var separator = reference.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return (null, reference);
        }

        var name = reference[..separator];
        var key = reference[(separator + 1)..];
        var store = _primary?.Name == name ? _primary : _fallback.Name == name ? _fallback : null;
        return (store, key);
    }

    private static string Reference(ISecretStore store, string alias) => $"{store.Name}:{SecretKeys.Normalize(alias)}";

    private static ISecretStore? CreatePrimary(IProcessRunner runner)
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsCredentialStore();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacKeychainStore(runner);
        }

        return new LinuxSecretToolStore(runner);
    }
}
