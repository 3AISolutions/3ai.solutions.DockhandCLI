using DockhandCLI.Core.Models;
using DockhandCLI.Core.Services;

namespace DockhandCLI.UnitTests;

public class EnvironmentResolverTests
{
    private static readonly List<DockerEnvironment> Environments =
    [
        new() { Id = 1, Name = "hhdocker01" },
        new() { Id = 2, Name = "hhdocker02" },
        new() { Id = 7, Name = "Edge" },
    ];

    private static EndpointConfig Endpoint(int? defaultEnvironment = null) =>
        new() { Alias = "srv01", BaseUrl = "https://docker01.example.com", DefaultEnvironment = defaultEnvironment };

    [Fact]
    public void Explicit_id_is_matched() => Assert.Equal(2, EnvironmentResolver.Select(Environments, "2", Endpoint(1)).Id);

    [Fact]
    public void Explicit_name_is_matched_case_insensitively() => Assert.Equal(7, EnvironmentResolver.Select(Environments, "edge", Endpoint()).Id);

    [Fact]
    public void Saved_default_is_used() => Assert.Equal(2, EnvironmentResolver.Select(Environments, null, Endpoint(2)).Id);

    [Fact]
    public void Missing_saved_default_is_not_found() =>
        Assert.Equal(ExitCodes.NotFound, Assert.Throws<DockhandException>(() => EnvironmentResolver.Select(Environments, null, Endpoint(42))).ExitCode);

    [Fact]
    public void Unknown_environment_is_not_found() =>
        Assert.Equal(ExitCodes.NotFound, Assert.Throws<DockhandException>(() => EnvironmentResolver.Select(Environments, "9", Endpoint())).ExitCode);

    [Fact]
    public void Single_environment_is_selected_automatically() =>
        Assert.Equal(1, EnvironmentResolver.Select([Environments[0]], null, Endpoint()).Id);

    [Fact]
    public void Several_environments_without_default_require_explicit_choice()
    {
        var ex = Assert.Throws<DockhandException>(() => EnvironmentResolver.Select(Environments, null, Endpoint()));

        Assert.Equal(ExitCodes.Usage, ex.ExitCode);
        Assert.Contains("--env", ex.Message);
    }

    [Fact]
    public void Ambiguous_names_require_the_id()
    {
        List<DockerEnvironment> twins = [new() { Id = 1, Name = "docker" }, new() { Id = 2, Name = "Docker" }];

        Assert.Equal(ExitCodes.Usage, Assert.Throws<DockhandException>(() => EnvironmentResolver.Select(twins, "docker", Endpoint())).ExitCode);
    }
}

public class ContainerResolverTests
{
    private static readonly List<ContainerSummary> Containers =
    [
        new() { Id = "a1b2c3d4e5f6a7b8c9d0e1f2", Name = "nginx" },
        new() { Id = "a1b2c3d4e5f6ffffffffffff", Name = "nginx-proxy" },
        new() { Id = "0123456789abcdef01234567", Name = "redis" },
        new() { Id = "fedcba9876543210fedcba98", Name = "NGINX" },
    ];

    [Fact]
    public void Exact_name_is_resolved_case_sensitively()
    {
        Assert.Equal("a1b2c3d4e5f6a7b8c9d0e1f2", ContainerResolver.Resolve(Containers, "nginx", "env").Id);
        Assert.Equal("fedcba9876543210fedcba98", ContainerResolver.Resolve(Containers, "NGINX", "env").Id);
    }

    [Fact]
    public void Leading_slash_is_ignored() => Assert.Equal("redis", ContainerResolver.Resolve(Containers, "/redis", "env").Name);

    [Fact]
    public void Full_id_is_resolved() => Assert.Equal("redis", ContainerResolver.Resolve(Containers, "0123456789abcdef01234567", "env").Name);

    [Fact]
    public void Unique_short_id_is_resolved() => Assert.Equal("redis", ContainerResolver.Resolve(Containers, "0123456789ab", "env").Name);

    [Fact]
    public void Ambiguous_short_id_is_rejected()
    {
        var ex = Assert.Throws<DockhandException>(() => ContainerResolver.Resolve(Containers, "a1b2c3d4e5f6", "env"));

        Assert.Equal(ExitCodes.Usage, ex.ExitCode);
        Assert.Contains("nginx-proxy", ex.Message);
    }

    [Fact]
    public void Prefixes_shorter_than_twelve_characters_are_not_guessed() =>
        Assert.Equal(ExitCodes.NotFound, Assert.Throws<DockhandException>(() => ContainerResolver.Resolve(Containers, "0123456", "env")).ExitCode);

    [Fact]
    public void Partial_names_are_not_guessed_but_suggested()
    {
        var ex = Assert.Throws<DockhandException>(() => ContainerResolver.Resolve(Containers, "ngin", "env"));

        Assert.Equal(ExitCodes.NotFound, ex.ExitCode);
        Assert.Contains("Did you mean", ex.Message);
    }

    [Fact]
    public void Name_matching_another_containers_id_is_ambiguous()
    {
        List<ContainerSummary> clash = [new() { Id = "abc", Name = "web" }, new() { Id = "web", Name = "other" }];

        Assert.Throws<DockhandException>(() => ContainerResolver.Resolve(clash, "web", "env"));
    }
}
