using DockhandCLI.Core.Models;
using DockhandCLI.Core.Services;
using DockhandCLI.Infrastructure.Authentication;
using DockhandCLI.Infrastructure.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace DockhandCLI.UnitTests;

public sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dockhand-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

public sealed class JsonConfigStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly JsonConfigStore _store;

    public JsonConfigStoreTests() => _store = new JsonConfigStore(new ConfigPaths(_temp.Path));

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Missing_file_is_an_empty_configuration()
    {
        var config = await _store.LoadAsync();

        Assert.Empty(config.Endpoints);
        Assert.False(File.Exists(_store.ConfigFilePath));
    }

    [Fact]
    public async Task Round_trips_endpoints()
    {
        await _store.UpdateAsync(c =>
        {
            c.Endpoints.Add(new EndpointConfig { Alias = "srv01", BaseUrl = "https://docker01.example.com", DefaultEnvironment = 1, CredentialRef = "file:srv01" });
            c.DefaultEndpoint = "srv01";
            return 0;
        });

        var config = await _store.LoadAsync();

        Assert.Equal("srv01", config.DefaultEndpoint);
        Assert.Equal(1, config.FindEndpoint("SRV01")!.DefaultEnvironment);
        Assert.Empty(Directory.GetFiles(_temp.Path, "*.tmp"));
    }

    [Fact]
    public async Task Corrupt_file_is_reported_and_left_untouched()
    {
        Directory.CreateDirectory(_temp.Path);
        await File.WriteAllTextAsync(_store.ConfigFilePath, "{ not json");

        var ex = await Assert.ThrowsAsync<DockhandException>(() => _store.UpdateAsync(c => 0));

        Assert.Contains("not valid JSON", ex.Message);
        Assert.Equal("{ not json", await File.ReadAllTextAsync(_store.ConfigFilePath));
    }

    [Fact]
    public async Task Failed_mutation_does_not_write()
    {
        await _store.UpdateAsync(c =>
        {
            c.Endpoints.Add(new EndpointConfig { Alias = "srv01", BaseUrl = "https://a.example.com" });
            return 0;
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.UpdateAsync<int>(c =>
        {
            c.Endpoints.Clear();
            throw new InvalidOperationException("boom");
        }));

        Assert.Single((await _store.LoadAsync()).Endpoints);
    }

    [Fact]
    public async Task Configuration_file_is_private_on_unix()
    {
        await _store.UpdateAsync(c => 0);

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_store.ConfigFilePath));
        }
    }
}

public sealed class SecretStorageTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task File_store_round_trips_without_plaintext_on_windows()
    {
        var paths = new ConfigPaths(_temp.Path);
        var store = new FileSecretStore(paths);

        await store.SetAsync("srv01", "dh_abcdefghijklmnopqrstuvwxyz", CancellationToken.None);

        Assert.Equal("dh_abcdefghijklmnopqrstuvwxyz", await store.GetAsync("SRV01", CancellationToken.None));
        var raw = await File.ReadAllTextAsync(paths.SecretsFile);
        if (OperatingSystem.IsWindows())
        {
            Assert.DoesNotContain("dh_abcdefghijklmnopqrstuvwxyz", raw);
        }
        else
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(paths.SecretsFile));
        }

        Assert.True(await store.DeleteAsync("srv01", CancellationToken.None));
        Assert.Null(await store.GetAsync("srv01", CancellationToken.None));
    }

    [Fact]
    public async Task Falls_back_to_file_when_keyring_fails()
    {
        var fallback = new FileSecretStore(new ConfigPaths(_temp.Path));
        var manager = new CredentialManager(new FailingStore(), fallback, NullLogger<CredentialManager>.Instance);

        var reference = await manager.StoreTokenAsync("srv01", "dh_abcdefghijklmnop");
        var token = await manager.GetTokenAsync(new EndpointConfig { Alias = "srv01", BaseUrl = "https://x", CredentialRef = reference });

        Assert.Equal("file:srv01", reference);
        Assert.Equal("dh_abcdefghijklmnop", token!.Token);
    }

    [Fact]
    public async Task Rejects_malformed_tokens()
    {
        var manager = new CredentialManager(null, new FileSecretStore(new ConfigPaths(_temp.Path)), NullLogger<CredentialManager>.Instance);

        await Assert.ThrowsAsync<DockhandException>(() => manager.StoreTokenAsync("srv01", "not a token"));
    }

    [Fact]
    public async Task Environment_variable_overrides_stored_token()
    {
        var manager = new CredentialManager(null, new FileSecretStore(new ConfigPaths(_temp.Path)), NullLogger<CredentialManager>.Instance);
        var alias = "envtest" + Guid.NewGuid().ToString("N")[..6];
        var variable = TokenEnvironment.VariableFor(alias);
        Environment.SetEnvironmentVariable(variable, "dh_fromenvironmentvariable");
        try
        {
            var token = await manager.GetTokenAsync(new EndpointConfig { Alias = alias, BaseUrl = "https://x" });

            Assert.Equal("dh_fromenvironmentvariable", token!.Token);
            Assert.Contains(variable, token.Source);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    private sealed class FailingStore : ISecretStore
    {
        public string Name => "failing";

        public string DisplayName => "Failing keyring";

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task SetAsync(string key, string secret, CancellationToken cancellationToken) => throw new InvalidOperationException("no keyring");

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken) => Task.FromResult(false);
    }
}
