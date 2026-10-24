namespace DockhandCLI.Core.Models;

/// <summary>Root of the user's configuration file. Contains no secrets.</summary>
public sealed class CliConfiguration
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public string? DefaultEndpoint { get; set; }

    public List<EndpointConfig> Endpoints { get; set; } = [];

    public EndpointConfig? FindEndpoint(string alias) =>
        Endpoints.FirstOrDefault(e => string.Equals(e.Alias, alias, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A saved Dockhand API installation.</summary>
public sealed class EndpointConfig
{
    public required string Alias { get; set; }

    /// <summary>Absolute base URL, normalized without a trailing slash.</summary>
    public required string BaseUrl { get; set; }

    /// <summary>Default Docker environment id on this installation.</summary>
    public int? DefaultEnvironment { get; set; }

    /// <summary>Reference to the stored token, e.g. <c>windows-credential-manager:DockhandCLI/srv01</c>. Never the token itself.</summary>
    public string? CredentialRef { get; set; }

    public int? TimeoutSeconds { get; set; }

    public string? Description { get; set; }
}
