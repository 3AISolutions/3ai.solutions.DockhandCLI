using System.Text.Json;
using DockhandCLI.Core.Models;

namespace DockhandCLI.Core.Interfaces;

/// <summary>Persists the non-secret configuration file.</summary>
public interface IConfigStore
{
    string ConfigFilePath { get; }

    Task<CliConfiguration> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Loads, mutates and atomically saves the configuration under an exclusive lock.</summary>
    Task<T> UpdateAsync<T>(Func<CliConfiguration, T> mutate, CancellationToken cancellationToken = default);
}

public sealed record StoredToken(string Token, string Source);

/// <summary>Stores and retrieves API tokens outside the configuration file.</summary>
public interface ICredentialManager
{
    /// <summary>Stores the token and returns the credential reference to save in the configuration.</summary>
    Task<string> StoreTokenAsync(string alias, string token, CancellationToken cancellationToken = default);

    /// <summary>Returns the token for an endpoint: the DOCKHAND_TOKEN_&lt;ALIAS&gt; variable first, then the stored one.</summary>
    Task<StoredToken?> GetTokenAsync(EndpointConfig endpoint, CancellationToken cancellationToken = default);

    Task RemoveTokenAsync(EndpointConfig endpoint, CancellationToken cancellationToken = default);

    /// <summary>Describes where the token comes from without reading it, or null when none is configured.</summary>
    string? DescribeCredential(EndpointConfig endpoint);
}

/// <summary>Creates API clients bound to one endpoint.</summary>
public interface IDockhandApiFactory
{
    /// <summary>Creates a client authenticated with the endpoint's stored token, if any.</summary>
    Task<IDockhandApi> CreateAsync(EndpointConfig endpoint, CancellationToken cancellationToken = default);

    /// <summary>Creates a client with an explicit token (or none).</summary>
    IDockhandApi Create(EndpointConfig endpoint, string? token);
}

public enum ContainerAction
{
    Start,
    Stop,
    Restart,
    Pause,
    Unpause,
}

public enum StackAction
{
    Start,
    Stop,
    Restart,
    Deploy,
    Down,
}

public sealed record StackActionOptions
{
    public bool Pull { get; init; }

    public bool Build { get; init; }

    public bool ForceRecreate { get; init; }

    public bool RemoveVolumes { get; init; }

    /// <summary>Restart mode (restart, ordered or recreate).</summary>
    public string? RestartMode { get; init; }
}

/// <summary>Typed access to one Dockhand installation. Every call maps to a documented API operation.</summary>
public interface IDockhandApi
{
    EndpointConfig Endpoint { get; }

    bool HasCredentials { get; }

    Task<HealthStatus> GetHealthAsync(CancellationToken cancellationToken = default);

    Task<DatabaseHealth> GetDatabaseHealthAsync(CancellationToken cancellationToken = default);

    Task<SessionInfo> GetSessionAsync(CancellationToken cancellationToken = default);

    /// <summary>Username/password login. On success the client holds the session cookie.</summary>
    Task<LoginResult> LoginAsync(string username, string password, string? mfaToken, CancellationToken cancellationToken = default);

    /// <summary>Creates an API token. Requires a session obtained with <see cref="LoginAsync"/>.</summary>
    Task<CreatedToken> CreateTokenAsync(string name, DateTimeOffset? expiresAt, string? password, CancellationToken cancellationToken = default);

    Task LogoutAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DockerEnvironment>> GetEnvironmentsAsync(CancellationToken cancellationToken = default);

    Task<DockerEnvironment> GetEnvironmentAsync(int id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ContainerSummary>> GetContainersAsync(int environmentId, bool includeStopped, CancellationToken cancellationToken = default);

    Task<JsonElement> InspectContainerAsync(int environmentId, string containerId, CancellationToken cancellationToken = default);

    Task<ContainerLogs> GetContainerLogsAsync(int environmentId, string containerId, int? tail, string? since, string? until, CancellationToken cancellationToken = default);

    Task<ActionResult> RunContainerActionAsync(int environmentId, string containerId, ContainerAction action, CancellationToken cancellationToken = default);

    Task<UpdateCheckSummary> CheckUpdatesAsync(int environmentId, IProgress<JobProgress>? progress, CancellationToken cancellationToken = default);

    Task<CachedUpdatesResponse> GetCachedUpdatesAsync(int environmentId, CancellationToken cancellationToken = default);

    Task<PendingUpdatesResponse> GetPendingUpdatesAsync(int environmentId, CancellationToken cancellationToken = default);

    /// <summary>Recreates containers with their latest images. Not idempotent: never retried.</summary>
    Task<BatchUpdateResponse> BatchUpdateAsync(int environmentId, IReadOnlyList<string> containerIds, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ImageSummary>> GetImagesAsync(int environmentId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StackSummary>> GetStacksAsync(int environmentId, CancellationToken cancellationToken = default);

    Task<StackCompose> GetStackComposeAsync(int environmentId, string name, CancellationToken cancellationToken = default);

    Task<StackJobResult> RunStackActionAsync(int environmentId, string name, StackAction action, StackActionOptions options, IProgress<JobProgress>? progress, CancellationToken cancellationToken = default);

    Task<JobStatus> GetJobAsync(string jobId, CancellationToken cancellationToken = default);

    Task<bool> CancelJobAsync(string jobId, CancellationToken cancellationToken = default);

    Task<JsonElement> GetSystemInfoAsync(int? environmentId, CancellationToken cancellationToken = default);
}

/// <summary>User interaction needed by core services (confirmations).</summary>
public interface IUserPrompt
{
    /// <summary>False when stdin is redirected or machine-readable output was requested.</summary>
    bool CanPrompt { get; }

    Task<bool> ConfirmAsync(string message, CancellationToken cancellationToken = default);
}
