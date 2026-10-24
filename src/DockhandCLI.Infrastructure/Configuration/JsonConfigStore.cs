using System.Text.Json;
using DockhandCLI.Core.Interfaces;
using DockhandCLI.Core.Models;
using DockhandCLI.Infrastructure.Persistence;

namespace DockhandCLI.Infrastructure.Configuration;

/// <summary>Stores <see cref="CliConfiguration"/> as JSON in the user's configuration directory.</summary>
internal sealed class JsonConfigStore(ConfigPaths paths) : IConfigStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public string ConfigFilePath => paths.ConfigFile;

    public async Task<CliConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ConfigFilePath))
        {
            return new CliConfiguration();
        }

        CliConfiguration? config;
        try
        {
            await using var stream = File.OpenRead(ConfigFilePath);
            config = await JsonSerializer.DeserializeAsync<CliConfiguration>(stream, Options, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw DockhandException.Config(
                $"The configuration file '{ConfigFilePath}' is not valid JSON ({ex.Message}). Fix it or move it away; DockhandCLI will not overwrite it.", ex);
        }

        config ??= new CliConfiguration();
        if (config.Version > CliConfiguration.CurrentVersion)
        {
            throw DockhandException.Config(
                $"The configuration file '{ConfigFilePath}' was written by a newer DockhandCLI (format {config.Version}). Upgrade DockhandCLI.");
        }

        config.Endpoints ??= [];
        return config;
    }

    public async Task<T> UpdateAsync<T>(Func<CliConfiguration, T> mutate, CancellationToken cancellationToken = default)
    {
        using var _ = await SecureFile.LockAsync(ConfigFilePath, cancellationToken).ConfigureAwait(false);
        var config = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var result = mutate(config);
        config.Version = CliConfiguration.CurrentVersion;
        await SecureFile.WriteAllTextAsync(ConfigFilePath, JsonSerializer.Serialize(config, Options), cancellationToken).ConfigureAwait(false);
        return result;
    }
}
