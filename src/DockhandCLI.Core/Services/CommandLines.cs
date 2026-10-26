using System.Globalization;
using System.Text.RegularExpressions;
using DockhandCLI.Core.Models;

namespace DockhandCLI.Core.Services;

/// <summary>
/// Builds copy-pasteable <c>dockhand</c> command lines for scripts. Every part is a plain token
/// (no quoting needed), so the same line works in PowerShell, cmd and POSIX shells.
/// </summary>
public static partial class CommandLines
{
    /// <summary>The one-step "check, then update if a new image exists" command for a container.</summary>
    public static string UpdateContainer(string endpointAlias, string environmentSelector, ContainerSummary container) =>
        string.Join(' ', "dockhand", "-" + endpointAlias, "--env", environmentSelector, "update", "container", ContainerSelector(container));

    /// <summary>
    /// The environment's name when it is a plain token that selects exactly this environment (easier to read in a
    /// script); otherwise its numeric id.
    /// </summary>
    public static string EnvironmentSelector(DockerEnvironment environment, IReadOnlyList<DockerEnvironment> all) =>
        PlainName().IsMatch(environment.Name)
        && all.Count(e => string.Equals(e.Name, environment.Name, StringComparison.OrdinalIgnoreCase)) == 1
            ? environment.Name
            : environment.Id.ToString(CultureInfo.InvariantCulture);

    private static string ContainerSelector(ContainerSummary container)
    {
        var name = container.Name.TrimStart('/');
        return PlainName().IsMatch(name) ? name : container.Id;
    }

    // Starts with a letter so it is never read as an option or as a numeric environment id.
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9._-]*$")]
    private static partial Regex PlainName();
}
