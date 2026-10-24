using DockhandCLI.Core.Models;

namespace DockhandCLI.Core.Services;

/// <summary>
/// Resolves a user-supplied container reference without guessing. Accepted: an exact container
/// name, an exact id, or an unambiguous id prefix of at least 12 hex characters (Docker's short id).
/// </summary>
public static class ContainerResolver
{
    private const int MinimumIdPrefix = 12;

    public static ContainerSummary Resolve(IReadOnlyList<ContainerSummary> containers, string reference, string environmentLabel)
    {
        var input = reference.Trim().TrimStart('/');
        if (input.Length == 0)
        {
            throw DockhandException.Usage("A container name or id is required.");
        }

        var matches = new List<ContainerSummary>();
        matches.AddRange(containers.Where(c => string.Equals(c.Name.TrimStart('/'), input, StringComparison.Ordinal)));
        matches.AddRange(containers.Where(c => string.Equals(c.Id, input, StringComparison.OrdinalIgnoreCase)));

        if (matches.Count == 0 && input.Length >= MinimumIdPrefix && input.All(Uri.IsHexDigit))
        {
            matches.AddRange(containers.Where(c => c.Id.StartsWith(input, StringComparison.OrdinalIgnoreCase)));
        }

        var distinct = matches.DistinctBy(c => c.Id).ToList();
        if (distinct.Count == 1)
        {
            return distinct[0];
        }

        if (distinct.Count > 1)
        {
            var candidates = string.Join(", ", distinct.Select(c => $"{c.Name} ({ShortId(c.Id)})"));
            throw DockhandException.Usage($"'{reference}' matches more than one container in {environmentLabel}: {candidates}. Use the full container id.");
        }

        var similar = containers
            .Where(c => c.Name.Contains(input, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Name)
            .Take(5)
            .ToList();
        var hint = similar.Count > 0 ? $" Did you mean: {string.Join(", ", similar)}?" : "";
        throw DockhandException.NotFound($"No container named or with id '{reference}' in {environmentLabel}.{hint}");
    }

    public static string ShortId(string id) => id.Length > 12 ? id[..12] : id;
}
