using System.Text.Json;
using System.Text.Json.Serialization;

namespace DockhandCLI.Core.Models;

// Models for Dockhand API responses (OpenAPI 1.0.53, see docs/ApiAnalysis.md).
// Only documented fields are typed. Everything else the server sends is kept in
// AdditionalProperties so `--output json` does not lose data.

public abstract class ApiObject
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class HealthStatus : ApiObject
{
    public string Status { get; set; } = "";

    public string? Timestamp { get; set; }
}

public sealed class DatabaseHealth : ApiObject
{
    public bool Healthy { get; set; }

    public string? Database { get; set; }

    public int? AppliedMigrations { get; set; }

    public int? PendingMigrations { get; set; }

    public int? Tables { get; set; }

    public string? Timestamp { get; set; }
}

public sealed class SessionInfo : ApiObject
{
    public bool Authenticated { get; set; }

    public bool AuthEnabled { get; set; }

    public SessionUser? User { get; set; }
}

public sealed class SessionUser : ApiObject
{
    public int Id { get; set; }

    public string Username { get; set; } = "";

    public string? Email { get; set; }

    public string? DisplayName { get; set; }

    public bool? IsAdmin { get; set; }

    public string? Provider { get; set; }
}

public sealed class LoginResult : ApiObject
{
    public bool? RequiresMfa { get; set; }

    public bool? Success { get; set; }
}

public sealed class CreatedToken : ApiObject
{
    public string Token { get; set; } = "";

    public int Id { get; set; }

    public string? TokenPrefix { get; set; }
}

public sealed class DockerEnvironment : ApiObject
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public string? ConnectionType { get; set; }

    public string? Host { get; set; }

    public int? Port { get; set; }

    public string? Protocol { get; set; }

    public string? Icon { get; set; }

    public string? PublicIp { get; set; }

    public string? Timezone { get; set; }

    public bool? HasTlsKey { get; set; }

    public bool? HasHawserToken { get; set; }
}

public sealed class ContainerSummary : ApiObject
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string Image { get; set; } = "";

    public string State { get; set; } = "";

    public string Status { get; set; } = "";
}

/// <summary>Generic <c>{success, error?}</c> response of container actions.</summary>
public sealed class ActionResult : ApiObject
{
    public bool? Success { get; set; }

    public string? Error { get; set; }
}

public sealed class ContainerLogs : ApiObject
{
    public string Logs { get; set; } = "";
}

/// <summary>Final result of <c>POST /api/containers/check-updates</c>.</summary>
public sealed class UpdateCheckSummary : ApiObject
{
    public int Total { get; set; }

    public int UpdatesFound { get; set; }

    public List<UpdateCheckResult> Results { get; set; } = [];
}

/// <summary>
/// Per-container item of a fresh update check. The OpenAPI document leaves this untyped; the
/// field names match <c>UpdateCheckResult</c> in Dockhand's check-updates route handler.
/// </summary>
public sealed class UpdateCheckResult : ApiObject
{
    public string ContainerId { get; set; } = "";

    public string ContainerName { get; set; } = "";

    public string? ImageName { get; set; }

    public bool HasUpdate { get; set; }

    public int? ReleaseAgeRemainingHours { get; set; }

    public string? CurrentDigest { get; set; }

    public string? NewDigest { get; set; }

    public string? Error { get; set; }

    public bool? IsLocalImage { get; set; }

    public string? SystemContainer { get; set; }

    public bool? UpdateDisabled { get; set; }

    public NewerVersion? NewerVersion { get; set; }

    public bool? PinnedToDigest { get; set; }
}

/// <summary>A newer version tag suggestion (advisory; never applied automatically by Dockhand).</summary>
public sealed class NewerVersion : ApiObject
{
    public string? Tag { get; set; }

    public string? Bump { get; set; }

    public string? Digest { get; set; }
}

/// <summary><c>GET /api/containers/check-updates</c>: cached records from the last check.</summary>
public sealed class CachedUpdatesResponse : ApiObject
{
    public int EnvironmentId { get; set; }

    public List<PendingUpdate> PendingUpdates { get; set; } = [];
}

/// <summary><c>GET /api/containers/pending-updates</c>.</summary>
public sealed class PendingUpdatesResponse : ApiObject
{
    public int EnvironmentId { get; set; }

    public int MinimumReleaseAgeHours { get; set; }

    public List<PendingUpdate> PendingUpdates { get; set; } = [];
}

public sealed class PendingUpdate : ApiObject
{
    public string ContainerId { get; set; } = "";

    public string ContainerName { get; set; } = "";

    public string? CurrentImage { get; set; }

    public string? CheckedAt { get; set; }

    public bool? HasImageUpdate { get; set; }

    public bool? PinnedToDigest { get; set; }

    public NewerVersion? NewerVersion { get; set; }

    public int? ReleaseAgeRemainingHours { get; set; }
}

/// <summary><c>POST /api/containers/batch-update</c> response.</summary>
public sealed class BatchUpdateResponse : ApiObject
{
    public bool Success { get; set; }

    public List<BatchUpdateItem> Results { get; set; } = [];

    public BatchUpdateSummary? Summary { get; set; }
}

public sealed class BatchUpdateItem : ApiObject
{
    public string ContainerId { get; set; } = "";

    public string ContainerName { get; set; } = "";

    public bool Success { get; set; }

    public string? Error { get; set; }

    public List<UpdateWarning>? Warnings { get; set; }
}

public sealed class UpdateWarning : ApiObject
{
    public string Status { get; set; } = "";

    public string Message { get; set; } = "";
}

public sealed class BatchUpdateSummary : ApiObject
{
    public int Total { get; set; }

    public int Success { get; set; }

    public int Failed { get; set; }
}

public sealed class ImageSummary : ApiObject
{
    [JsonPropertyName("Id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("RepoTags")]
    public List<string>? RepoTags { get; set; }

    [JsonPropertyName("Size")]
    public long? Size { get; set; }

    [JsonPropertyName("Created")]
    public long? Created { get; set; }
}

/// <summary>Item of <c>GET /api/stacks</c> (matches Dockhand's <c>ComposeStackInfo</c>).</summary>
public sealed class StackSummary : ApiObject
{
    public string Name { get; set; } = "";

    public List<string>? Containers { get; set; }

    public string? Status { get; set; }

    public string? SourceType { get; set; }

    public bool? HasComposeFile { get; set; }
}

public sealed class StackCompose : ApiObject
{
    public string? Content { get; set; }

    public string? StackDir { get; set; }

    public string? ComposePath { get; set; }

    public string? EnvPath { get; set; }
}

/// <summary><c>GET /api/jobs/{id}</c>.</summary>
public sealed class JobStatus : ApiObject
{
    public string Id { get; set; } = "";

    /// <summary><c>running</c>, <c>done</c> or <c>error</c>.</summary>
    public string Status { get; set; } = "";

    public List<JobLine> Lines { get; set; } = [];

    public JsonElement? Result { get; set; }
}

public sealed class JobLine
{
    public string? Event { get; set; }

    public JsonElement Data { get; set; }
}

/// <summary>Final <c>{success, output|error}</c> result of a stack job.</summary>
public sealed class StackJobResult : ApiObject
{
    public bool? Success { get; set; }

    public string? Output { get; set; }

    public string? Error { get; set; }
}

/// <summary>A progress event emitted by a long-running Dockhand job.</summary>
public sealed record JobProgress(string? Event, JsonElement Data);
