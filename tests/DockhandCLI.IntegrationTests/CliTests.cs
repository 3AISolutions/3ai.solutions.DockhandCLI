using System.Net;
using System.Text.Json;
using DockhandCLI.Commands;
using DockhandCLI.Core.Models;

namespace DockhandCLI.IntegrationTests;

public sealed class CliTests : IDisposable
{
    private readonly CliTestHost _host = new();

    public CliTests() => _host.Server.WithDefaultData();

    public void Dispose() => _host.Dispose();

    private static void AssertValidJson(string text)
    {
        Assert.DoesNotContain("\u001b", text);
        using var _ = JsonDocument.Parse(text);
    }

    [Fact]
    public async Task Version_and_help_succeed()
    {
        Assert.Equal(0, (await _host.RunAsync("--version")).ExitCode);
        var help = await _host.RunAsync("--help");

        Assert.Equal(0, help.ExitCode);
        Assert.Contains("endpoint", help.Stdout);
    }

    [Fact]
    public async Task Every_catalog_command_is_registered_and_has_help()
    {
        foreach (var entry in CommandCatalog.Entries)
        {
            var result = await _host.RunAsync([.. entry.Words, "--help"], "");
            Assert.True(result.ExitCode == 0, $"'{entry.Path} --help' exited with {result.ExitCode}: {result.Stderr}");
        }
    }

    [Fact]
    public async Task Unknown_command_is_a_usage_error()
    {
        var result = await _host.RunAsync("containers", "lst");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("list", result.Stderr);
    }

    [Fact]
    public async Task Endpoint_add_list_default_remove()
    {
        Assert.Equal(0, (await _host.RunAsync("endpoint", "add", "srv01", "https://docker01.example.com", "-d", "Production")).ExitCode);
        Assert.Equal(0, (await _host.RunAsync("endpoint", "add", "srv02", "https://docker02.example.com")).ExitCode);
        Assert.Equal(ExitCodes.Usage, (await _host.RunAsync("endpoint", "add", "SRV01", "https://other.example.com")).ExitCode);
        Assert.Equal(0, (await _host.RunAsync("endpoint", "default", "srv02")).ExitCode);

        var list = await _host.RunAsync("endpoint", "list", "--output", "json");
        AssertValidJson(list.Stdout);
        using (var json = JsonDocument.Parse(list.Stdout))
        {
            Assert.Equal(2, json.RootElement.GetArrayLength());
            Assert.True(json.RootElement[1].GetProperty("isDefault").GetBoolean());
        }

        var remove = await _host.RunAsync("endpoint", "remove", "srv02");
        Assert.Equal(ExitCodes.Usage, remove.ExitCode);
        Assert.Contains("--yes", remove.Stderr);

        Assert.Equal(0, (await _host.RunAsync("endpoint", "remove", "srv02", "--yes")).ExitCode);
        var config = await _host.Store.LoadAsync();
        Assert.Equal("srv01", Assert.Single(config.Endpoints).Alias);
        Assert.Equal("srv01", config.DefaultEndpoint);
    }

    [Theory]
    [InlineData("-srv02", "containers", "list", "--output", "json")]
    [InlineData("--server", "srv02", "containers", "list", "--output", "json")]
    [InlineData("containers", "list", "--server", "srv02", "-o", "json")]
    [InlineData("containers", "list", "-srv02", "--output=json")]
    public async Task Endpoint_selection_forms_are_equivalent(params string[] args)
    {
        await _host.WithEndpointsAsync();

        var result = await _host.RunAsync(args);

        Assert.Equal(0, result.ExitCode);
        AssertValidJson(result.Stdout);
        Assert.All(_host.Server.Requests, r => Assert.Equal("docker02.example.com", r.Uri.Host));
        Assert.All(_host.Server.Requests, r => Assert.Equal("Bearer dh_srv02tokenBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB", r.Authorization));
    }

    [Fact]
    public async Task Default_endpoint_and_environment_are_used()
    {
        await _host.WithEndpointsAsync();

        var result = await _host.RunAsync("containers", "list");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("nginx", result.Stdout);
        Assert.Contains(_host.Server.Requests, r => r.PathAndQuery == "/api/containers?env=1&all=true");
        Assert.All(_host.Server.Requests, r => Assert.Equal("docker01.example.com", r.Uri.Host));
    }

    [Fact]
    public async Task Env_option_selects_environment_by_id_or_name()
    {
        await _host.WithEndpointsAsync();

        Assert.Equal(0, (await _host.RunAsync("-srv01", "--env", "2", "containers", "list")).ExitCode);
        Assert.Equal(0, (await _host.RunAsync("-srv01", "--env", "hhdocker02", "containers", "list")).ExitCode);
        Assert.Equal(2, _host.Server.Requests.Count(r => r.PathAndQuery == "/api/containers?env=2&all=true"));

        var missing = await _host.RunAsync("-srv01", "--env", "9", "containers", "list");
        Assert.Equal(ExitCodes.NotFound, missing.ExitCode);
    }

    [Fact]
    public async Task Missing_token_gives_actionable_error()
    {
        await _host.RunAsync("endpoint", "add", "srv01", "https://docker01.example.com");
        _host.Server.OnJson(HttpMethod.Get, "/api/environments", new { error = "Unauthorized", message = "Authentication required" }, HttpStatusCode.Unauthorized);

        var result = await _host.RunAsync("env", "list");

        Assert.Equal(ExitCodes.Unauthorized, result.ExitCode);
        Assert.Contains("dockhand endpoint auth srv01", result.Stderr);
        Assert.Null(_host.Server.Requests.Last().Authorization);
    }

    [Fact]
    public async Task Network_failure_exit_code()
    {
        await _host.WithEndpointsAsync();
        _host.Server.Fault = _ => new HttpRequestException(HttpRequestError.NameResolutionError, "No such host");

        var result = await _host.RunAsync("env", "list");

        Assert.Equal(ExitCodes.Network, result.ExitCode);
        Assert.Contains("resolve", result.Stderr);
    }

    [Fact]
    public async Task Json_errors_go_to_stderr_only()
    {
        await _host.WithEndpointsAsync();
        _host.Server.OnJson(HttpMethod.Get, "/api/environments", new { error = "boom" }, HttpStatusCode.InternalServerError);

        var result = await _host.RunAsync("env", "list", "--output", "json");

        Assert.Equal(ExitCodes.ApiError, result.ExitCode);
        Assert.Equal("", result.Stdout.Trim());
        Assert.Contains("boom", result.Stderr);
    }

    [Fact]
    public async Task Check_updates_json_report()
    {
        await _host.WithEndpointsAsync();
        _host.Server.OnJson(HttpMethod.Post, "/api/containers/check-updates?env=1", new { jobId = "j1" });
        _host.Server.OnJson(HttpMethod.Get, "/api/jobs/j1", new
        {
            id = "j1",
            status = "done",
            lines = Array.Empty<object>(),
            result = new
            {
                total = 2,
                updatesFound = 1,
                results = new object[]
                {
                    new { containerId = "a1", containerName = "nginx", imageName = "nginx:latest", hasUpdate = true },
                    new { containerId = "b2", containerName = "redis", imageName = "redis:7", hasUpdate = false },
                },
            },
        });

        var result = await _host.RunAsync("-srv01", "check", "updates", "--output", "json");

        Assert.Equal(0, result.ExitCode);
        AssertValidJson(result.Stdout);
        using var json = JsonDocument.Parse(result.Stdout);
        var environment = json.RootElement.GetProperty("environments")[0];
        Assert.Equal("fresh", json.RootElement.GetProperty("mode").GetString());
        Assert.Equal(1, environment.GetProperty("updatesFound").GetInt32());
        Assert.Equal("update-available", environment.GetProperty("containers")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Check_updates_pending_and_cached_use_get_routes()
    {
        await _host.WithEndpointsAsync();
        _host.Server.OnJson(HttpMethod.Get, "/api/containers/check-updates", new { environmentId = 1, pendingUpdates = Array.Empty<object>() });

        var pending = await _host.RunAsync("-srv01", "check", "updates", "--pending");
        var cached = await _host.RunAsync("-srv01", "check", "updates", "--cached");

        Assert.Equal(0, pending.ExitCode);
        Assert.Contains("nginx", pending.Stdout);
        Assert.Equal(0, cached.ExitCode);
        Assert.DoesNotContain(_host.Server.Requests, r => r.Method == HttpMethod.Post);
        Assert.Equal(ExitCodes.Usage, (await _host.RunAsync("check", "updates", "--pending", "--cached")).ExitCode);
    }

    [Fact]
    public async Task Check_updates_all_envs_groups_results_and_reports_partial_failure()
    {
        await _host.WithEndpointsAsync();
        _host.Server.OnJson(HttpMethod.Get, "/api/containers/pending-updates?env=2", new { error = "Docker unreachable" }, HttpStatusCode.InternalServerError);

        var result = await _host.RunAsync("-srv01", "--all-envs", "check", "updates", "--pending");

        Assert.Equal(ExitCodes.PartialFailure, result.ExitCode);
        Assert.Contains("hhdocker01", result.Stdout);
        Assert.Contains("hhdocker02", result.Stdout);
        Assert.Contains("Docker unreachable", result.Stdout);
    }

    [Fact]
    public async Task All_envs_is_rejected_by_other_commands()
    {
        await _host.WithEndpointsAsync();

        Assert.Equal(ExitCodes.Usage, (await _host.RunAsync("--all-envs", "containers", "list")).ExitCode);
    }

    [Theory]
    [InlineData("check", "apply", "update", "docker", "nginx", "--dry-run")]
    [InlineData("update", "apply", "nginx", "--dry-run")]
    public async Task Dry_run_sends_no_mutating_request(params string[] args)
    {
        await _host.WithEndpointsAsync();

        var result = await _host.RunAsync(["-srv01", .. args], "");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Dry run", result.Stdout);
        Assert.All(_host.Server.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task Apply_without_yes_and_without_terminal_is_refused()
    {
        await _host.WithEndpointsAsync();

        var result = await _host.RunAsync("-srv01", "check", "apply", "update", "docker", "nginx");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("--yes", result.Stderr);
        Assert.Empty(_host.Server.Matching(HttpMethod.Post, "/api/containers/batch-update"));
    }

    [Fact]
    public async Task Apply_asks_and_respects_a_no()
    {
        await _host.WithEndpointsAsync();
        _host.CanPrompt = true;

        var result = await _host.RunAsync(["-srv01", "check", "apply", "update", "docker", "nginx"], "", keys: "n");

        Assert.Equal(ExitCodes.Aborted, result.ExitCode);
        Assert.Empty(_host.Server.Matching(HttpMethod.Post, "/api/containers/batch-update"));
    }

    [Theory]
    [InlineData("check", "apply", "update", "docker", "nginx", "--yes")]
    [InlineData("update", "apply", "nginx", "-y")]
    public async Task Apply_with_yes_updates_the_resolved_container_once(params string[] args)
    {
        await _host.WithEndpointsAsync();
        _host.Server.OnJson(HttpMethod.Post, "/api/containers/batch-update?env=1", new
        {
            success = true,
            results = new[] { new { containerId = "a1b2c3d4e5f6a7b8c9d0a1b2c3d4e5f6", containerName = "nginx", success = true } },
            summary = new { total = 1, success = 1, failed = 0 },
        });

        var result = await _host.RunAsync(["-srv01", .. args], "");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("recreated", result.Stdout);
        var request = Assert.Single(_host.Server.Matching(HttpMethod.Post, "/api/containers/batch-update"));
        Assert.Equal("{\"containerIds\":[\"a1b2c3d4e5f6a7b8c9d0a1b2c3d4e5f6\"]}", request.Body);
    }

    [Fact]
    public async Task Apply_reports_api_failure_with_exit_code()
    {
        await _host.WithEndpointsAsync();
        _host.Server.OnJson(HttpMethod.Post, "/api/containers/batch-update?env=1", new
        {
            success = false,
            results = new[] { new { containerId = "a1b2c3d4e5f6a7b8c9d0a1b2c3d4e5f6", containerName = "nginx", success = false, error = "Image pull failed" } },
            summary = new { total = 1, success = 0, failed = 1 },
        });

        var result = await _host.RunAsync("-srv01", "update", "apply", "nginx", "--yes", "--output", "json");

        Assert.Equal(ExitCodes.ApiError, result.ExitCode);
        AssertValidJson(result.Stdout);
        Assert.Contains("Image pull failed", result.Stderr);
    }

    [Fact]
    public async Task Apply_never_guesses_a_container()
    {
        await _host.WithEndpointsAsync();

        var result = await _host.RunAsync("-srv01", "update", "apply", "ngin", "--yes");

        Assert.Equal(ExitCodes.NotFound, result.ExitCode);
        Assert.Contains("Did you mean", result.Stderr);
        Assert.Empty(_host.Server.Matching(HttpMethod.Post, "/api/containers/batch-update"));
    }

    [Fact]
    public async Task Search_finds_containers_in_every_environment_and_prints_update_commands()
    {
        await _host.WithEndpointsAsync();

        var result = await _host.RunAsync("containers", "search", "NGIN");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("dockhand -srv01 --env hhdocker01 update container nginx", result.Stdout);
        Assert.Contains("dockhand -srv01 --env hhdocker02 update container nginx", result.Stdout);
        Assert.DoesNotContain("redis", result.Stdout);
        Assert.Contains(_host.Server.Requests, r => r.PathAndQuery == "/api/containers?env=2&all=true");
    }

    [Fact]
    public async Task Search_matches_image_and_spans_all_endpoints_in_json()
    {
        await _host.WithEndpointsAsync();

        var result = await _host.RunAsync("containers", "search", "redis:7", "--all-endpoints", "-o", "json");

        Assert.Equal(0, result.ExitCode);
        AssertValidJson(result.Stdout);
        using var json = JsonDocument.Parse(result.Stdout);
        var matches = json.RootElement.GetProperty("matches").EnumerateArray().ToList();
        Assert.Equal(4, matches.Count);
        Assert.Equal(4, json.RootElement.GetProperty("environmentsSearched").GetInt32());
        Assert.Contains(matches, m => m.GetProperty("updateCommand").GetString() == "dockhand -srv02 --env hhdocker02 update container redis");
    }

    [Fact]
    public async Task Search_reports_failed_environments_and_missing_matches()
    {
        await _host.WithEndpointsAsync();
        _host.Server.OnJson(HttpMethod.Get, "/api/containers?env=2&all=true", new { error = "Docker unreachable" }, HttpStatusCode.InternalServerError);

        var partial = await _host.RunAsync("containers", "search", "nginx");
        var none = await _host.RunAsync("containers", "search", "nothing-like-this", "--env", "1");

        Assert.Equal(ExitCodes.PartialFailure, partial.ExitCode);
        Assert.Contains("Docker unreachable", partial.Stdout);
        Assert.Contains("update container nginx", partial.Stdout);
        Assert.Equal(ExitCodes.NotFound, none.ExitCode);
        Assert.Equal(ExitCodes.Usage, (await _host.RunAsync("-srv01", "containers", "search", "x", "--all-endpoints")).ExitCode);
    }

    private void CheckReturns(bool hasUpdate) =>
        _host.Server.OnJson(HttpMethod.Post, "/api/containers/check-updates?env=1", new
        {
            total = 2,
            updatesFound = hasUpdate ? 1 : 0,
            results = new object[]
            {
                new { containerId = "a1b2c3d4e5f6a7b8c9d0a1b2c3d4e5f6", containerName = "nginx", imageName = "nginx:latest", hasUpdate, currentDigest = "sha256:111111111111aaaa", newDigest = hasUpdate ? "sha256:222222222222bbbb" : null },
                new { containerId = "0f0e0d0c0b0a09080706050403020100", containerName = "redis", imageName = "redis:7", hasUpdate = false },
            },
        });

    private void BatchUpdateSucceeds() =>
        _host.Server.OnJson(HttpMethod.Post, "/api/containers/batch-update?env=1", new
        {
            success = true,
            results = new[] { new { containerId = "a1b2c3d4e5f6a7b8c9d0a1b2c3d4e5f6", containerName = "nginx", success = true } },
            summary = new { total = 1, success = 1, failed = 0 },
        });

    [Fact]
    public async Task Update_container_updates_when_the_check_finds_a_new_image()
    {
        await _host.WithEndpointsAsync();
        CheckReturns(hasUpdate: true);
        BatchUpdateSucceeds();

        var result = await _host.RunAsync("-srv01", "--env", "hhdocker01", "update", "container", "nginx", "--yes");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("recreated", result.Stdout);
        Assert.Contains("dockhand -srv01 --env hhdocker01 update container nginx --yes", result.Stdout);
        var request = Assert.Single(_host.Server.Matching(HttpMethod.Post, "/api/containers/batch-update"));
        Assert.Equal("{\"containerIds\":[\"a1b2c3d4e5f6a7b8c9d0a1b2c3d4e5f6\"]}", request.Body);
    }

    [Fact]
    public async Task Update_container_does_nothing_when_up_to_date_unless_forced()
    {
        await _host.WithEndpointsAsync();
        CheckReturns(hasUpdate: false);
        BatchUpdateSucceeds();

        var upToDate = await _host.RunAsync("-srv01", "update", "container", "nginx", "--yes");
        Assert.Equal(0, upToDate.ExitCode);
        Assert.Contains("nothing to update", upToDate.Stdout);
        Assert.Empty(_host.Server.Matching(HttpMethod.Post, "/api/containers/batch-update"));

        var forced = await _host.RunAsync("-srv01", "update", "container", "nginx", "--yes", "--force");
        Assert.Equal(0, forced.ExitCode);
        Assert.Single(_host.Server.Matching(HttpMethod.Post, "/api/containers/batch-update"));
    }

    [Fact]
    public async Task Update_container_check_only_reports_without_updating()
    {
        await _host.WithEndpointsAsync();
        CheckReturns(hasUpdate: true);

        var result = await _host.RunAsync("-srv01", "update", "container", "nginx", "--check-only", "-o", "json");

        Assert.Equal(0, result.ExitCode);
        AssertValidJson(result.Stdout);
        using var json = JsonDocument.Parse(result.Stdout);
        Assert.True(json.RootElement.GetProperty("updateAvailable").GetBoolean());
        Assert.False(json.RootElement.GetProperty("updated").GetBoolean());
        Assert.Equal("dockhand -srv01 --env hhdocker01 update container nginx", json.RootElement.GetProperty("command").GetString());
        Assert.Empty(_host.Server.Matching(HttpMethod.Post, "/api/containers/batch-update"));
    }

    [Fact]
    public async Task Update_container_needs_yes_without_a_terminal()
    {
        await _host.WithEndpointsAsync();
        CheckReturns(hasUpdate: true);

        var result = await _host.RunAsync("-srv01", "update", "container", "nginx");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("--yes", result.Stderr);
        Assert.Empty(_host.Server.Matching(HttpMethod.Post, "/api/containers/batch-update"));
    }

    [Fact]
    public async Task Stop_requires_confirmation_but_start_does_not()
    {
        await _host.WithEndpointsAsync();
        _host.Server.OnJson(HttpMethod.Post, "/api/containers/a1b2c3d4e5f6a7b8c9d0a1b2c3d4e5f6/start?env=1", new { success = true });
        _host.Server.OnJson(HttpMethod.Post, "/api/containers/a1b2c3d4e5f6a7b8c9d0a1b2c3d4e5f6/stop?env=1", new { success = true });

        Assert.Equal(0, (await _host.RunAsync("containers", "start", "nginx")).ExitCode);
        Assert.Equal(ExitCodes.Usage, (await _host.RunAsync("containers", "stop", "nginx")).ExitCode);
        Assert.Equal(0, (await _host.RunAsync("containers", "stop", "nginx", "--yes")).ExitCode);
        Assert.Single(_host.Server.Matching(HttpMethod.Post, "/api/containers/a1b2c3d4e5f6a7b8c9d0a1b2c3d4e5f6/stop"));
    }

    [Fact]
    public async Task Endpoint_auth_imports_and_verifies_token_from_stdin()
    {
        await _host.RunAsync("endpoint", "add", "srv01", "https://docker01.example.com");
        const string token = "dh_importedTOKENimportedTOKENimportedTOKEN123";

        var result = await _host.RunAsync(["endpoint", "auth", "srv01", "--token-stdin"], token + "\n");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(token, _host.Credentials.Tokens["srv01"]);
        Assert.Equal("memory:srv01", (await _host.Store.LoadAsync()).FindEndpoint("srv01")!.CredentialRef);
        Assert.Contains(_host.Server.Requests, r => r.Uri.AbsolutePath == "/api/environments" && r.Authorization == $"Bearer {token}");
        Assert.DoesNotContain(token, result.Stdout + result.Stderr);
        Assert.DoesNotContain(token, await File.ReadAllTextAsync(Path.Combine(_host.ConfigDirectory, "config.json")));
    }

    [Fact]
    public async Task Endpoint_auth_rejected_token_is_not_stored()
    {
        await _host.RunAsync("endpoint", "add", "srv01", "https://docker01.example.com");
        _host.Server.OnJson(HttpMethod.Get, "/api/environments", new { error = "Unauthorized" }, HttpStatusCode.Unauthorized);

        var result = await _host.RunAsync(["endpoint", "auth", "srv01", "--token-stdin"], "dh_wrongwrongwrongwrong\n");

        Assert.Equal(ExitCodes.Unauthorized, result.ExitCode);
        Assert.Empty(_host.Credentials.Tokens);
    }

    [Fact]
    public async Task Endpoint_test_reports_checks()
    {
        await _host.WithEndpointsAsync();

        var result = await _host.RunAsync("endpoint", "test", "srv02", "--output", "json");

        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.Stdout);
        Assert.True(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(3, json.RootElement.GetProperty("checks").GetArrayLength());
    }

    [Fact]
    public async Task Failed_request_does_not_change_configuration()
    {
        await _host.WithEndpointsAsync();
        var before = await File.ReadAllTextAsync(Path.Combine(_host.ConfigDirectory, "config.json"));
        _host.Server.Fault = r => r.Uri.Host == "docker02.example.com" ? new HttpRequestException("down") : null;

        await _host.RunAsync("-srv02", "endpoint", "set-env", "srv02", "2");

        Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(_host.ConfigDirectory, "config.json")));
    }

    [Fact]
    public async Task Set_env_verifies_against_the_api()
    {
        await _host.WithEndpointsAsync();

        Assert.Equal(0, (await _host.RunAsync("endpoint", "set-env", "srv01", "hhdocker02")).ExitCode);
        Assert.Equal(2, (await _host.Store.LoadAsync()).FindEndpoint("srv01")!.DefaultEnvironment);
        Assert.Equal(ExitCodes.NotFound, (await _host.RunAsync("endpoint", "set-env", "srv01", "42")).ExitCode);
    }

    [Fact]
    public async Task Shell_dispatches_commands_and_use_does_not_change_default()
    {
        await _host.WithEndpointsAsync();
        // The shell exits with the status of the last command, like a POSIX shell.
        const string script = "/command\n'unterminated\nuse srv02\n/containers list --output json\n/exit\n";

        var result = await _host.RunAsync([], script);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Dockhand CLI", result.Stdout);
        Assert.Contains("/check apply update docker", result.Stdout);
        Assert.Contains("Active endpoint: srv02", result.Stdout);
        Assert.Contains("Unterminated", result.Stderr);
        Assert.Contains(_host.Server.Requests, r => r.Uri.Host == "docker02.example.com" && r.Uri.AbsolutePath == "/api/containers");
        Assert.Equal("srv01", (await _host.Store.LoadAsync()).DefaultEndpoint);
    }

    [Fact]
    public async Task Shell_shorthand_overrides_active_endpoint_for_one_command()
    {
        await _host.WithEndpointsAsync();

        await _host.RunAsync([], "-srv02 env list\nenv list\nexit\n");

        var hosts = _host.Server.Requests.Where(r => r.Uri.AbsolutePath == "/api/environments").Select(r => r.Uri.Host).ToList();
        Assert.Equal(["docker02.example.com", "docker01.example.com"], hosts);
    }

    [Fact]
    public async Task Shell_command_help()
    {
        var result = await _host.RunAsync([], "/command check updates\n/help endpoint add\n");

        Assert.Contains("--pending", result.Stdout);
        Assert.Contains("<URL>", result.Stdout);
    }

    [Fact]
    public async Task Stack_down_with_volumes_requires_yes_and_reports_result()
    {
        await _host.WithEndpointsAsync();
        _host.Server.OnJson(HttpMethod.Post, "/api/stacks/web/down?env=1", new { jobId = "j9" });
        _host.Server.OnJson(HttpMethod.Get, "/api/jobs/j9", new { id = "j9", status = "done", lines = Array.Empty<object>(), result = new { success = true, output = "removed" } });

        Assert.Equal(ExitCodes.Usage, (await _host.RunAsync("stacks", "down", "web", "--remove-volumes")).ExitCode);
        var result = await _host.RunAsync("stacks", "down", "web", "--remove-volumes", "--yes", "-o", "json");

        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.Stdout);
        Assert.True(json.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task Health_works_without_token()
    {
        await _host.RunAsync("endpoint", "add", "srv01", "https://docker01.example.com");
        _host.Server.OnJson(HttpMethod.Get, "/api/health/database", new { healthy = true, database = "sqlite", migrationsTable = true, appliedMigrations = 3, pendingMigrations = 0, tables = 10, timestamp = "x" });

        var result = await _host.RunAsync("health");

        Assert.Equal(0, result.ExitCode);
        Assert.All(_host.Server.Requests, r => Assert.Null(r.Authorization));
    }
}
