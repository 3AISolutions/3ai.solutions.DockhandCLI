using System.Text.Json;
using DockhandCLI.Core.Interfaces;
using DockhandCLI.Core.Models;

namespace DockhandCLI.UnitTests;

internal sealed class InMemoryConfigStore : IConfigStore
{
    public CliConfiguration Config { get; set; } = new();

    public int Saves { get; private set; }

    public string ConfigFilePath => "memory";

    public Task<CliConfiguration> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Clone(Config));

    public Task<T> UpdateAsync<T>(Func<CliConfiguration, T> mutate, CancellationToken cancellationToken = default)
    {
        var copy = Clone(Config);
        var result = mutate(copy);
        Config = copy;
        Saves++;
        return Task.FromResult(result);
    }

    private static CliConfiguration Clone(CliConfiguration config) =>
        JsonSerializer.Deserialize<CliConfiguration>(JsonSerializer.Serialize(config))!;
}

internal sealed class InMemoryCredentialManager : ICredentialManager
{
    public Dictionary<string, string> Tokens { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<string> StoreTokenAsync(string alias, string token, CancellationToken cancellationToken = default)
    {
        Tokens[alias] = token;
        return Task.FromResult($"memory:{alias}");
    }

    public Task<StoredToken?> GetTokenAsync(EndpointConfig endpoint, CancellationToken cancellationToken = default) =>
        Task.FromResult(endpoint.CredentialRef is not null && Tokens.TryGetValue(endpoint.Alias, out var token)
            ? new StoredToken(token, "memory")
            : null);

    public Task RemoveTokenAsync(EndpointConfig endpoint, CancellationToken cancellationToken = default)
    {
        Tokens.Remove(endpoint.Alias);
        return Task.CompletedTask;
    }

    public string? DescribeCredential(EndpointConfig endpoint) => endpoint.CredentialRef is null ? null : "memory";
}

internal sealed class FakePrompt(bool canPrompt, bool answer = false) : IUserPrompt
{
    public int Questions { get; private set; }

    public bool CanPrompt => canPrompt;

    public Task<bool> ConfirmAsync(string message, CancellationToken cancellationToken = default)
    {
        Questions++;
        return Task.FromResult(answer);
    }
}

/// <summary>An <see cref="IDockhandApi"/> with canned data; counts mutating calls.</summary>
internal sealed class FakeApi : IDockhandApi
{
    public EndpointConfig Endpoint { get; init; } = new() { Alias = "srv01", BaseUrl = "https://docker01.example.com" };

    public bool HasCredentials => true;

    public List<DockerEnvironment> Environments { get; init; } = [new() { Id = 1, Name = "hhdocker01" }];

    public List<ContainerSummary> Containers { get; init; } =
    [
        new() { Id = "a1b2c3d4e5f6a7b8c9d0", Name = "nginx", Image = "nginx:latest", State = "running", Status = "Up 2 hours" },
        new() { Id = "f6e5d4c3b2a1f6e5d4c3", Name = "redis", Image = "redis:7", State = "running", Status = "Up 3 days" },
    ];

    public PendingUpdatesResponse Pending { get; set; } = new() { EnvironmentId = 1 };

    public BatchUpdateResponse? BatchResponse { get; set; }

    public Func<int, UpdateCheckSummary>? CheckResult { get; set; }

    public List<IReadOnlyList<string>> BatchUpdateCalls { get; } = [];

    public Task<IReadOnlyList<DockerEnvironment>> GetEnvironmentsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DockerEnvironment>>(Environments);

    public Task<IReadOnlyList<ContainerSummary>> GetContainersAsync(int environmentId, bool includeStopped, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ContainerSummary>>(Containers);

    public Task<PendingUpdatesResponse> GetPendingUpdatesAsync(int environmentId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Pending);

    public Task<BatchUpdateResponse> BatchUpdateAsync(int environmentId, IReadOnlyList<string> containerIds, CancellationToken cancellationToken = default)
    {
        BatchUpdateCalls.Add(containerIds);
        return Task.FromResult(BatchResponse ?? new BatchUpdateResponse
        {
            Success = true,
            Results = containerIds.Select(id => new BatchUpdateItem { ContainerId = id, ContainerName = "x", Success = true }).ToList(),
        });
    }

    public Task<UpdateCheckSummary> CheckUpdatesAsync(int environmentId, IProgress<JobProgress>? progress, CancellationToken cancellationToken = default) =>
        CheckResult is null
            ? throw new DockhandException(ExitCodes.ApiError, $"check failed for {environmentId}")
            : Task.FromResult(CheckResult(environmentId));

    public Task<DockerEnvironment> GetEnvironmentAsync(int id, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<HealthStatus> GetHealthAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<DatabaseHealth> GetDatabaseHealthAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<SessionInfo> GetSessionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<LoginResult> LoginAsync(string username, string password, string? mfaToken, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<CreatedToken> CreateTokenAsync(string name, DateTimeOffset? expiresAt, string? password, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task LogoutAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<JsonElement> InspectContainerAsync(int environmentId, string containerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<ContainerLogs> GetContainerLogsAsync(int environmentId, string containerId, int? tail, string? since, string? until, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<ActionResult> RunContainerActionAsync(int environmentId, string containerId, ContainerAction action, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<CachedUpdatesResponse> GetCachedUpdatesAsync(int environmentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<ImageSummary>> GetImagesAsync(int environmentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<StackSummary>> GetStacksAsync(int environmentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<StackCompose> GetStackComposeAsync(int environmentId, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<StackJobResult> RunStackActionAsync(int environmentId, string name, StackAction action, StackActionOptions options, IProgress<JobProgress>? progress, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<JobStatus> GetJobAsync(string jobId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<bool> CancelJobAsync(string jobId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<JsonElement> GetSystemInfoAsync(int? environmentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
