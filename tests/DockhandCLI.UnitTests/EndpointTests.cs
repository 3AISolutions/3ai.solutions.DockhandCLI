using DockhandCLI.Core.Models;
using DockhandCLI.Core.Services;

namespace DockhandCLI.UnitTests;

public class EndpointServiceTests
{
    private readonly InMemoryConfigStore _store = new();
    private readonly InMemoryCredentialManager _credentials = new();
    private readonly EndpointService _service;

    public EndpointServiceTests() => _service = new EndpointService(_store, _credentials);

    [Fact]
    public async Task First_endpoint_becomes_the_default()
    {
        await _service.AddAsync(new NewEndpoint("srv01", "https://docker01.example.com/"));
        await _service.AddAsync(new NewEndpoint("srv02", "https://docker02.example.com"));

        Assert.Equal("srv01", _store.Config.DefaultEndpoint);
        Assert.Equal("https://docker01.example.com", _store.Config.FindEndpoint("srv01")!.BaseUrl);
        Assert.Equal(2, _store.Config.Endpoints.Count);
    }

    [Fact]
    public async Task Add_stores_all_settings_and_can_set_default()
    {
        await _service.AddAsync(new NewEndpoint("srv01", "https://docker01.example.com"));
        await _service.AddAsync(new NewEndpoint("srv02", "https://docker02.example.com/dockhand", Description: "Staging", DefaultEnvironment: 2, TimeoutSeconds: 30, MakeDefault: true));

        var endpoint = _store.Config.FindEndpoint("srv02")!;
        Assert.Equal("https://docker02.example.com/dockhand", endpoint.BaseUrl);
        Assert.Equal("Staging", endpoint.Description);
        Assert.Equal(2, endpoint.DefaultEnvironment);
        Assert.Equal(30, endpoint.TimeoutSeconds);
        Assert.Equal("srv02", _store.Config.DefaultEndpoint);
    }

    [Theory]
    [InlineData("srv01")]
    [InlineData("SRV01")]
    public async Task Duplicate_aliases_are_rejected_case_insensitively(string alias)
    {
        await _service.AddAsync(new NewEndpoint("srv01", "https://docker01.example.com"));

        var ex = await Assert.ThrowsAsync<DockhandException>(() => _service.AddAsync(new NewEndpoint(alias, "https://other.example.com")));

        Assert.Equal(ExitCodes.Usage, ex.ExitCode);
        Assert.Single(_store.Config.Endpoints);
    }

    [Theory]
    [InlineData("s")]
    [InlineData("1srv")]
    [InlineData("srv 01")]
    [InlineData("help")]
    [InlineData("server")]
    [InlineData("-srv01")]
    public async Task Invalid_or_reserved_aliases_are_rejected(string alias)
    {
        await Assert.ThrowsAsync<DockhandException>(() => _service.AddAsync(new NewEndpoint(alias, "https://docker01.example.com")));
        Assert.Empty(_store.Config.Endpoints);
    }

    [Fact]
    public async Task Remove_deletes_endpoint_token_and_default()
    {
        await _service.AddAsync(new NewEndpoint("srv01", "https://docker01.example.com"));
        await _service.AddAsync(new NewEndpoint("srv02", "https://docker02.example.com"));
        await _service.StoreTokenAsync("srv01", "dh_abcdefghijklmnop");

        await _service.RemoveAsync("SRV01");

        Assert.Null(_store.Config.FindEndpoint("srv01"));
        Assert.False(_credentials.Tokens.ContainsKey("srv01"));
        Assert.Equal("srv02", _store.Config.DefaultEndpoint);
    }

    [Fact]
    public async Task Removing_unknown_endpoint_is_not_found()
    {
        var ex = await Assert.ThrowsAsync<DockhandException>(() => _service.RemoveAsync("nope"));
        Assert.Equal(ExitCodes.NotFound, ex.ExitCode);
    }

    [Fact]
    public async Task Set_default_and_environment()
    {
        await _service.AddAsync(new NewEndpoint("srv01", "https://docker01.example.com"));
        await _service.AddAsync(new NewEndpoint("srv02", "https://docker02.example.com"));

        await _service.SetDefaultAsync("srv02");
        await _service.SetDefaultEnvironmentAsync("srv02", 4);

        Assert.Equal("srv02", _store.Config.DefaultEndpoint);
        Assert.Equal(4, _store.Config.FindEndpoint("srv02")!.DefaultEnvironment);
    }

    [Fact]
    public async Task Token_is_stored_as_reference_not_in_configuration()
    {
        await _service.AddAsync(new NewEndpoint("srv01", "https://docker01.example.com"));

        await _service.StoreTokenAsync("srv01", "dh_secretsecretsecret");

        Assert.Equal("memory:srv01", _store.Config.FindEndpoint("srv01")!.CredentialRef);
        Assert.DoesNotContain("dh_secret", System.Text.Json.JsonSerializer.Serialize(_store.Config));
    }
}

public class EndpointUrlTests
{
    [Theory]
    [InlineData("https://docker01.example.com", "https://docker01.example.com")]
    [InlineData("https://docker01.example.com/", "https://docker01.example.com")]
    [InlineData(" https://docker01.example.com:8443/dockhand/ ", "https://docker01.example.com:8443/dockhand")]
    public void Normalizes(string input, string expected) => Assert.Equal(expected, EndpointUrl.Normalize(input, allowHttp: false));

    [Theory]
    [InlineData("docker01.example.com")]
    [InlineData("ftp://docker01.example.com")]
    [InlineData("https://user:pass@docker01.example.com")]
    [InlineData("https://docker01.example.com/?a=1")]
    [InlineData("https://docker01.example.com/#x")]
    public void Rejects_invalid_urls(string input) =>
        Assert.Equal(ExitCodes.Usage, Assert.Throws<DockhandException>(() => EndpointUrl.Normalize(input, allowHttp: true)).ExitCode);

    [Fact]
    public void Http_requires_explicit_opt_in()
    {
        Assert.Throws<DockhandException>(() => EndpointUrl.Normalize("http://docker01.lan", allowHttp: false));
        Assert.Equal("http://docker01.lan", EndpointUrl.Normalize("http://docker01.lan", allowHttp: true));
    }
}

public class EndpointResolverTests
{
    private static CliConfiguration Config(string? defaultAlias, params string[] aliases) => new()
    {
        DefaultEndpoint = defaultAlias,
        Endpoints = aliases.Select(a => new EndpointConfig { Alias = a, BaseUrl = $"https://{a}.example.com" }).ToList(),
    };

    [Fact]
    public void Explicit_selection_wins() =>
        Assert.Equal("srv02", EndpointResolver.Resolve(Config("srv01", "srv01", "srv02"), "srv02", "srv01").Alias);

    [Fact]
    public void Session_endpoint_beats_saved_default() =>
        Assert.Equal("srv02", EndpointResolver.Resolve(Config("srv01", "srv01", "srv02"), null, "srv02").Alias);

    [Fact]
    public void Saved_default_is_used_without_selection() =>
        Assert.Equal("srv01", EndpointResolver.Resolve(Config("srv01", "srv01", "srv02"), null).Alias);

    [Fact]
    public void Single_endpoint_is_used_when_no_default() =>
        Assert.Equal("srv01", EndpointResolver.Resolve(Config(null, "srv01"), null).Alias);

    [Fact]
    public void Several_endpoints_without_default_require_selection() =>
        Assert.Equal(ExitCodes.Usage, Assert.Throws<DockhandException>(() => EndpointResolver.Resolve(Config(null, "srv01", "srv02"), null)).ExitCode);

    [Fact]
    public void No_endpoints_is_a_usage_error() =>
        Assert.Equal(ExitCodes.Usage, Assert.Throws<DockhandException>(() => EndpointResolver.Resolve(Config(null), null)).ExitCode);

    [Fact]
    public void Unknown_alias_lists_registered_endpoints()
    {
        var ex = Assert.Throws<DockhandException>(() => EndpointResolver.Resolve(Config("srv01", "srv01", "srv02"), "srv09"));

        Assert.Equal(ExitCodes.NotFound, ex.ExitCode);
        Assert.Contains("srv01, srv02", ex.Message);
    }
}
