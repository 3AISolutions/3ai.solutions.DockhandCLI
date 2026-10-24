using System.Text.Json.Serialization;
using DockhandCLI.Core.Interfaces;
using DockhandCLI.Core.Models;

namespace DockhandCLI.Core.Services;

public enum UpdateCheckMode
{
    /// <summary>POST /api/containers/check-updates: run a fresh check.</summary>
    Fresh,

    /// <summary>GET /api/containers/check-updates: records from the last check.</summary>
    Cached,

    /// <summary>GET /api/containers/pending-updates: pending records with hold/pin details.</summary>
    Pending,
}

public static class UpdateStatus
{
    public const string UpdateAvailable = "update-available";
    public const string UpToDate = "up-to-date";
    public const string Pending = "pending";
    public const string Held = "held";
    public const string NewerTag = "newer-tag";
    public const string PinnedDigest = "pinned-digest";
    public const string Disabled = "update-disabled";
    public const string SystemContainer = "system";
    public const string LocalImage = "local-image";
    public const string Error = "error";
}

/// <summary>One container's update state, normalized across the three check modes.</summary>
public sealed record UpdateRow
{
    public required string ContainerId { get; init; }

    public required string ContainerName { get; init; }

    public string? Image { get; init; }

    public required string Status { get; init; }

    public string? CurrentDigest { get; init; }

    public string? NewDigest { get; init; }

    public string? NewerVersionTag { get; init; }

    public int? ReleaseAgeRemainingHours { get; init; }

    public string? CheckedAt { get; init; }

    public string? Error { get; init; }
}

public sealed record EnvironmentUpdateReport
{
    public required int EnvironmentId { get; init; }

    public required string EnvironmentName { get; init; }

    public int? Total { get; init; }

    public int? UpdatesFound { get; init; }

    public IReadOnlyList<UpdateRow> Containers { get; init; } = [];

    public string? Error { get; init; }

    [JsonIgnore]
    public int ErrorExitCode { get; init; }
}

public sealed record UpdateCheckReport(string Endpoint, string Mode, IReadOnlyList<EnvironmentUpdateReport> Environments)
{
    [JsonIgnore]
    public int ExitCode
    {
        get
        {
            var failed = Environments.Where(e => e.Error is not null).ToList();
            if (failed.Count == 0)
            {
                return ExitCodes.Success;
            }

            return failed.Count == Environments.Count ? failed[0].ErrorExitCode : ExitCodes.PartialFailure;
        }
    }
}

public sealed record UpdatePlan(
    string Endpoint,
    DockerEnvironment Environment,
    ContainerSummary Container,
    PendingUpdate? PendingUpdate,
    bool PendingStateKnown);

public sealed record UpdateOutcome
{
    public required bool DryRun { get; init; }

    public required bool Executed { get; init; }

    public required bool Success { get; init; }

    public string? Error { get; init; }

    public IReadOnlyList<UpdateWarning> Warnings { get; init; } = [];

    public BatchUpdateItem? Result { get; init; }
}

/// <summary>Container update checks and updates. Shared by <c>check ...</c> and <c>update apply</c>.</summary>
public sealed class UpdateService
{
    public async Task<UpdateCheckReport> CheckAsync(
        IDockhandApi api,
        IReadOnlyList<DockerEnvironment> environments,
        UpdateCheckMode mode,
        Func<DockerEnvironment, IProgress<JobProgress>?>? progressFor = null,
        CancellationToken cancellationToken = default)
    {
        var reports = new List<EnvironmentUpdateReport>(environments.Count);
        var continueOnError = environments.Count > 1;

        foreach (var environment in environments)
        {
            try
            {
                reports.Add(await CheckEnvironmentAsync(api, environment, mode, progressFor?.Invoke(environment), cancellationToken)
                    .ConfigureAwait(false));
            }
            catch (DockhandException ex) when (continueOnError)
            {
                reports.Add(new EnvironmentUpdateReport
                {
                    EnvironmentId = environment.Id,
                    EnvironmentName = environment.Name,
                    Error = ex.Message,
                    ErrorExitCode = ex.ExitCode,
                });
            }
        }

        return new UpdateCheckReport(api.Endpoint.Alias, mode.ToString().ToLowerInvariant(), reports);
    }

    private static async Task<EnvironmentUpdateReport> CheckEnvironmentAsync(
        IDockhandApi api, DockerEnvironment environment, UpdateCheckMode mode, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        switch (mode)
        {
            case UpdateCheckMode.Fresh:
            {
                var summary = await api.CheckUpdatesAsync(environment.Id, progress, cancellationToken).ConfigureAwait(false);
                return new EnvironmentUpdateReport
                {
                    EnvironmentId = environment.Id,
                    EnvironmentName = environment.Name,
                    Total = summary.Total,
                    UpdatesFound = summary.UpdatesFound,
                    Containers = summary.Results.Select(FromCheckResult).OrderBy(SortKey).ThenBy(r => r.ContainerName, StringComparer.OrdinalIgnoreCase).ToList(),
                };
            }

            case UpdateCheckMode.Cached:
            {
                var cached = await api.GetCachedUpdatesAsync(environment.Id, cancellationToken).ConfigureAwait(false);
                var rows = cached.PendingUpdates.Select(p => FromPending(p, UpdateStatus.Pending)).ToList();
                return new EnvironmentUpdateReport
                {
                    EnvironmentId = environment.Id,
                    EnvironmentName = environment.Name,
                    UpdatesFound = rows.Count,
                    Containers = rows.OrderBy(r => r.ContainerName, StringComparer.OrdinalIgnoreCase).ToList(),
                };
            }

            default:
            {
                var pending = await api.GetPendingUpdatesAsync(environment.Id, cancellationToken).ConfigureAwait(false);
                var rows = pending.PendingUpdates.Select(p => FromPending(p, null)).ToList();
                return new EnvironmentUpdateReport
                {
                    EnvironmentId = environment.Id,
                    EnvironmentName = environment.Name,
                    UpdatesFound = rows.Count(r => r.Status == UpdateStatus.UpdateAvailable),
                    Containers = rows.OrderBy(SortKey).ThenBy(r => r.ContainerName, StringComparer.OrdinalIgnoreCase).ToList(),
                };
            }
        }
    }

    public async Task<UpdatePlan> PlanAsync(IDockhandApi api, DockerEnvironment environment, string containerReference, CancellationToken cancellationToken = default)
    {
        var containers = await api.GetContainersAsync(environment.Id, includeStopped: true, cancellationToken).ConfigureAwait(false);
        var container = ContainerResolver.Resolve(containers, containerReference, $"environment '{environment.Name}' ({environment.Id})");

        PendingUpdate? pending = null;
        var known = true;
        try
        {
            var response = await api.GetPendingUpdatesAsync(environment.Id, cancellationToken).ConfigureAwait(false);
            pending = response.PendingUpdates.FirstOrDefault(p => p.ContainerId == container.Id)
                ?? response.PendingUpdates.FirstOrDefault(p => p.ContainerName == container.Name);
        }
        catch (ApiException)
        {
            // Informational only; the update itself does not depend on it.
            known = false;
        }

        return new UpdatePlan(api.Endpoint.Alias, environment, container, pending, known);
    }

    /// <summary>
    /// Applies the plan through <c>POST /api/containers/batch-update</c> with a single container id.
    /// Dry runs never send a request. The call is not retried: recreating a container is not idempotent.
    /// </summary>
    /// <param name="api">Client for the plan's endpoint.</param>
    /// <param name="plan">Result of <see cref="PlanAsync"/>.</param>
    /// <param name="dryRun">Report what would happen without sending the request.</param>
    /// <param name="assumeYes">Skip the confirmation question.</param>
    /// <param name="prompt">Used for the confirmation question.</param>
    /// <param name="observe">Optional wrapper around the running request, e.g. to show a spinner after confirmation.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task<UpdateOutcome> ApplyAsync(
        IDockhandApi api,
        UpdatePlan plan,
        bool dryRun,
        bool assumeYes,
        IUserPrompt prompt,
        Func<Task<BatchUpdateResponse>, Task<BatchUpdateResponse>>? observe = null,
        CancellationToken cancellationToken = default)
    {
        if (dryRun)
        {
            return new UpdateOutcome { DryRun = true, Executed = false, Success = true };
        }

        await Confirmation.EnsureAsync(
            prompt,
            assumeYes,
            $"Recreate container '{plan.Container.Name}' on {plan.Endpoint} / {plan.Environment.Name} with its latest image?",
            cancellationToken).ConfigureAwait(false);

        var request = api.BatchUpdateAsync(plan.Environment.Id, [plan.Container.Id], cancellationToken);
        var response = await (observe is null ? request : observe(request)).ConfigureAwait(false);
        var item = response.Results.FirstOrDefault(r => r.ContainerId == plan.Container.Id);

        if (item is null)
        {
            return new UpdateOutcome
            {
                DryRun = false,
                Executed = true,
                Success = false,
                Error = "The API response did not include a result for this container, so the outcome is unknown. Check the container state.",
            };
        }

        return new UpdateOutcome
        {
            DryRun = false,
            Executed = true,
            Success = item.Success,
            Error = item.Success ? null : item.Error ?? "The API reported a failure without details.",
            Warnings = item.Warnings ?? [],
            Result = item,
        };
    }

    internal static UpdateRow FromCheckResult(UpdateCheckResult r)
    {
        var status = r switch
        {
            { Error: not null } => UpdateStatus.Error,
            { UpdateDisabled: true } => UpdateStatus.Disabled,
            { SystemContainer: not null } => UpdateStatus.SystemContainer,
            { HasUpdate: true } => UpdateStatus.UpdateAvailable,
            { ReleaseAgeRemainingHours: > 0 } => UpdateStatus.Held,
            { NewerVersion.Tag: not null } => UpdateStatus.NewerTag,
            { PinnedToDigest: true } => UpdateStatus.PinnedDigest,
            { IsLocalImage: true } => UpdateStatus.LocalImage,
            _ => UpdateStatus.UpToDate,
        };

        return new UpdateRow
        {
            ContainerId = r.ContainerId,
            ContainerName = r.ContainerName,
            Image = r.ImageName,
            Status = status,
            CurrentDigest = r.CurrentDigest,
            NewDigest = r.NewDigest,
            NewerVersionTag = r.NewerVersion?.Tag,
            ReleaseAgeRemainingHours = r.ReleaseAgeRemainingHours,
            Error = r.Error,
        };
    }

    internal static UpdateRow FromPending(PendingUpdate p, string? fixedStatus)
    {
        var status = fixedStatus ?? p switch
        {
            { HasImageUpdate: true } => UpdateStatus.UpdateAvailable,
            { ReleaseAgeRemainingHours: > 0 } => UpdateStatus.Held,
            { NewerVersion.Tag: not null } => UpdateStatus.NewerTag,
            { PinnedToDigest: true } => UpdateStatus.PinnedDigest,
            _ => UpdateStatus.Pending,
        };

        return new UpdateRow
        {
            ContainerId = p.ContainerId,
            ContainerName = p.ContainerName,
            Image = p.CurrentImage,
            Status = status,
            NewerVersionTag = p.NewerVersion?.Tag,
            ReleaseAgeRemainingHours = p.ReleaseAgeRemainingHours,
            CheckedAt = p.CheckedAt,
        };
    }

    private static int SortKey(UpdateRow row) => row.Status switch
    {
        UpdateStatus.UpdateAvailable => 0,
        UpdateStatus.Error => 1,
        UpdateStatus.Held or UpdateStatus.NewerTag or UpdateStatus.PinnedDigest or UpdateStatus.Pending => 2,
        _ => 3,
    };
}
