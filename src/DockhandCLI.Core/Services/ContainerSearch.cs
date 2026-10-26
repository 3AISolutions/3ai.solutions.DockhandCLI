using System.Text.Json.Serialization;
using DockhandCLI.Core.Interfaces;
using DockhandCLI.Core.Models;

namespace DockhandCLI.Core.Services;

public sealed record ContainerMatch
{
    public required string Endpoint { get; init; }

    public required int EnvironmentId { get; init; }

    public required string EnvironmentName { get; init; }

    public required string ContainerId { get; init; }

    public required string ContainerName { get; init; }

    public required string Image { get; init; }

    public required string State { get; init; }

    public string? Status { get; init; }

    /// <summary>Command that checks this container for a new image and updates it.</summary>
    public required string UpdateCommand { get; init; }
}

/// <summary>An endpoint or environment that could not be searched.</summary>
public sealed record ContainerSearchFailure
{
    public required string Endpoint { get; init; }

    public int? EnvironmentId { get; init; }

    public string? EnvironmentName { get; init; }

    public required string Error { get; init; }

    [JsonIgnore]
    public int ExitCode { get; init; }
}

/// <param name="Term">The search text.</param>
/// <param name="EnvironmentsSearched">Environments whose containers were read.</param>
/// <param name="Matches">Matching containers, ordered by endpoint, environment and name.</param>
/// <param name="Errors">Endpoints or environments that could not be searched.</param>
public sealed record ContainerSearchReport(string Term, int EnvironmentsSearched, IReadOnlyList<ContainerMatch> Matches, IReadOnlyList<ContainerSearchFailure> Errors)
{
    [JsonIgnore]
    public int ExitCode => Errors.Count == 0 ? ExitCodes.Success
        : EnvironmentsSearched == 0 ? Errors[0].ExitCode
        : ExitCodes.PartialFailure;
}

/// <summary>Finds containers by name or image across environments and endpoints.</summary>
public static class ContainerSearch
{
    /// <summary>Case-insensitive substring match on the container name or its image.</summary>
    public static bool IsMatch(ContainerSummary container, string term) =>
        container.Name.TrimStart('/').Contains(term, StringComparison.OrdinalIgnoreCase)
        || container.Image.Contains(term, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Searches every environment of each endpoint (or only <paramref name="environmentSelector"/>). When more than
    /// one place is searched, a failing endpoint or environment is reported in the result instead of stopping the search.
    /// </summary>
    public static async Task<ContainerSearchReport> SearchAsync(
        IReadOnlyList<EndpointConfig> endpoints,
        IDockhandApiFactory apiFactory,
        string? environmentSelector,
        string term,
        CancellationToken cancellationToken = default)
    {
        term = term.Trim();
        if (term.Length == 0)
        {
            throw DockhandException.Usage("Search text is required, e.g. 'dockhand containers search nginx'.");
        }

        var matches = new List<ContainerMatch>();
        var errors = new List<ContainerSearchFailure>();
        var searched = 0;

        foreach (var endpoint in endpoints)
        {
            IDockhandApi api;
            IReadOnlyList<DockerEnvironment> environments;
            try
            {
                api = await apiFactory.CreateAsync(endpoint, cancellationToken).ConfigureAwait(false);
                environments = await api.GetEnvironmentsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DockhandException ex) when (endpoints.Count > 1)
            {
                errors.Add(new ContainerSearchFailure { Endpoint = endpoint.Alias, Error = ex.Message, ExitCode = ex.ExitCode });
                continue;
            }

            IReadOnlyList<DockerEnvironment> targets = environmentSelector is null
                ? environments
                : [EnvironmentResolver.Match(environments, environmentSelector, endpoint.Alias)];

            foreach (var environment in targets)
            {
                IReadOnlyList<ContainerSummary> containers;
                try
                {
                    containers = await api.GetContainersAsync(environment.Id, includeStopped: true, cancellationToken).ConfigureAwait(false);
                }
                catch (DockhandException ex) when (endpoints.Count > 1 || targets.Count > 1)
                {
                    errors.Add(new ContainerSearchFailure
                    {
                        Endpoint = endpoint.Alias,
                        EnvironmentId = environment.Id,
                        EnvironmentName = environment.Name,
                        Error = ex.Message,
                        ExitCode = ex.ExitCode,
                    });
                    continue;
                }

                searched++;
                var selector = CommandLines.EnvironmentSelector(environment, environments);
                matches.AddRange(containers.Where(c => IsMatch(c, term)).Select(c => new ContainerMatch
                {
                    Endpoint = endpoint.Alias,
                    EnvironmentId = environment.Id,
                    EnvironmentName = environment.Name,
                    ContainerId = c.Id,
                    ContainerName = c.Name.TrimStart('/'),
                    Image = c.Image,
                    State = c.State,
                    Status = c.Status,
                    UpdateCommand = CommandLines.UpdateContainer(endpoint.Alias, selector, c),
                }));
            }
        }

        var ordered = matches
            .OrderBy(m => m.Endpoint, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.EnvironmentName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.ContainerName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new ContainerSearchReport(term, searched, ordered, errors);
    }
}
