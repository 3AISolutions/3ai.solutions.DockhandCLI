using DockhandCLI.Core.Models;

namespace DockhandCLI.Core.Services;

/// <summary>Chooses the endpoint for a command: explicit selection, then the shell's active endpoint, then the saved default.</summary>
public static class EndpointResolver
{
    public static EndpointConfig Resolve(CliConfiguration config, string? explicitAlias, string? sessionAlias = null)
    {
        var alias = explicitAlias ?? sessionAlias ?? config.DefaultEndpoint;

        if (alias is not null)
        {
            return EndpointService.Require(config, alias);
        }

        return config.Endpoints.Count switch
        {
            0 => throw DockhandException.Usage("No endpoints are registered. Add one with 'dockhand endpoint add <alias> <url>'."),
            1 => config.Endpoints[0],
            _ => throw DockhandException.Usage(
                $"No endpoint selected and no default is set. Use --server <alias> (or -<alias>), or run 'dockhand endpoint default <alias>'. Registered: {string.Join(", ", config.Endpoints.Select(e => e.Alias))}."),
        };
    }
}
