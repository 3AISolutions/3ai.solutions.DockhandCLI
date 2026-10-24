using System.Globalization;
using DockhandCLI.Core.Interfaces;
using DockhandCLI.Core.Models;

namespace DockhandCLI.Core.Services;

/// <summary>
/// Picks the Docker environment for a command. Order: <c>--env</c>, the endpoint's saved default,
/// the only environment when exactly one exists. Dockhand documents no default environment, so
/// anything else requires an explicit choice.
/// </summary>
public static class EnvironmentResolver
{
    public static async Task<DockerEnvironment> ResolveAsync(
        IDockhandApi api, string? selector, CancellationToken cancellationToken = default)
    {
        var environments = await api.GetEnvironmentsAsync(cancellationToken).ConfigureAwait(false);
        return Select(environments, selector, api.Endpoint);
    }

    public static DockerEnvironment Select(IReadOnlyList<DockerEnvironment> environments, string? selector, EndpointConfig endpoint)
    {
        if (selector is not null)
        {
            return Match(environments, selector, endpoint.Alias);
        }

        if (endpoint.DefaultEnvironment is { } id)
        {
            return environments.FirstOrDefault(e => e.Id == id)
                ?? throw DockhandException.NotFound(
                    $"The default environment {id} of endpoint '{endpoint.Alias}' no longer exists. Run 'dockhand -{endpoint.Alias} env list' and 'dockhand endpoint set-env {endpoint.Alias} <id>'.");
        }

        return environments.Count switch
        {
            0 => throw DockhandException.NotFound($"Endpoint '{endpoint.Alias}' has no Docker environments you can access."),
            1 => environments[0],
            _ => throw DockhandException.Usage(
                $"Endpoint '{endpoint.Alias}' has {environments.Count} environments ({Describe(environments)}). Choose one with --env <id>, or save a default with 'dockhand endpoint set-env {endpoint.Alias} <id>'."),
        };
    }

    /// <summary>Matches an environment by numeric id, or else by exact name (case-insensitive).</summary>
    public static DockerEnvironment Match(IReadOnlyList<DockerEnvironment> environments, string selector, string endpointAlias)
    {
        if (int.TryParse(selector, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            return environments.FirstOrDefault(e => e.Id == id)
                ?? throw DockhandException.NotFound($"Environment {id} was not found on '{endpointAlias}'. Available: {Describe(environments)}.");
        }

        var byName = environments.Where(e => string.Equals(e.Name, selector, StringComparison.OrdinalIgnoreCase)).ToList();
        return byName.Count switch
        {
            1 => byName[0],
            0 => throw DockhandException.NotFound($"Environment '{selector}' was not found on '{endpointAlias}'. Available: {Describe(environments)}."),
            _ => throw DockhandException.Usage($"Environment name '{selector}' is ambiguous on '{endpointAlias}' ({Describe(byName)}). Use the numeric id."),
        };
    }

    private static string Describe(IEnumerable<DockerEnvironment> environments)
    {
        var text = string.Join(", ", environments.Select(e => $"{e.Id}={e.Name}"));
        return text.Length == 0 ? "none" : text;
    }
}
