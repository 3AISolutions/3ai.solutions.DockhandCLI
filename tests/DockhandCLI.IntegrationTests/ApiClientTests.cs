using System.Net;
using System.Text;
using System.Text.Json;
using DockhandCLI.Core.Models;
using DockhandCLI.Infrastructure.Api;
using Microsoft.Extensions.Logging.Abstractions;

namespace DockhandCLI.IntegrationTests;

public class ApiClientTests
{
    private const string Token = "dh_abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQ";
    private readonly FakeDockhandServer _server = new FakeDockhandServer().WithDefaultData();
    private readonly ApiClientOptions _options = new() { JobPollInterval = TimeSpan.FromMilliseconds(1) };

    private DockhandApiClient Client(string? token = Token, string baseUrl = "https://docker01.example.com", int? timeoutSeconds = null) =>
        new(new HttpClient(_server), new EndpointConfig { Alias = "srv01", BaseUrl = baseUrl, TimeoutSeconds = timeoutSeconds }, token, _options, NullLogger.Instance);

    [Fact]
    public async Task Sends_bearer_token()
    {
        await Client().GetEnvironmentsAsync();

        Assert.Equal($"Bearer {Token}", _server.Requests.Single().Authorization);
    }

    [Fact]
    public async Task Sends_no_authorization_without_token()
    {
        await Client(token: null).GetHealthAsync();

        Assert.Null(_server.Requests.Single().Authorization);
    }

    [Fact]
    public async Task Keeps_base_path_of_endpoint()
    {
        _server.OnJson(HttpMethod.Get, "/dockhand/api/health", new { status = "ok" });

        var health = await Client(baseUrl: "https://proxy.example.com/dockhand").GetHealthAsync();

        Assert.Equal("ok", health.Status);
        Assert.Equal("https://proxy.example.com/dockhand/api/health", _server.Requests.Single().Uri.ToString());
    }

    [Fact]
    public async Task Passes_environment_as_query_parameter()
    {
        var containers = await Client().GetContainersAsync(7, includeStopped: false);

        Assert.Equal("/api/containers?env=7&all=false", _server.Requests.Single().PathAndQuery);
        Assert.Equal("nginx", containers[0].Name);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ExitCodes.Unauthorized, "endpoint auth srv01")]
    [InlineData(HttpStatusCode.Forbidden, ExitCodes.Unauthorized, "Permission denied")]
    [InlineData(HttpStatusCode.NotFound, ExitCodes.NotFound, "Not found")]
    [InlineData(HttpStatusCode.InternalServerError, ExitCodes.ApiError, "Failed to list")]
    [InlineData(HttpStatusCode.TooManyRequests, ExitCodes.ApiError, "rate limiting")]
    public async Task Maps_http_errors(HttpStatusCode status, int exitCode, string messagePart)
    {
        _server.OnJson(HttpMethod.Get, "/api/environments", new { error = "Failed to list environments" }, status);

        var ex = await Assert.ThrowsAsync<ApiException>(() => Client().GetEnvironmentsAsync());

        Assert.Equal(status, ex.StatusCode);
        Assert.Equal(exitCode, ex.ExitCode);
        Assert.Contains(messagePart, ex.Message);
    }

    [Fact]
    public async Task Error_messages_never_contain_the_token()
    {
        _server.OnJson(HttpMethod.Get, "/api/environments", new { error = $"bad token {Token}" }, HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsAsync<ApiException>(() => Client().GetEnvironmentsAsync());

        Assert.DoesNotContain(Token, ex.Message);
    }

    [Fact]
    public async Task Html_response_is_reported_as_unexpected()
    {
        _server.On(HttpMethod.Get, "/api/environments", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>login</html>", Encoding.UTF8, "text/html"),
        });

        var ex = await Assert.ThrowsAsync<DockhandException>(() => Client().GetEnvironmentsAsync());

        Assert.Equal(ExitCodes.ApiError, ex.ExitCode);
        Assert.Contains("text/html", ex.Message);
    }

    [Fact]
    public async Task Redirects_are_not_followed()
    {
        _server.On(HttpMethod.Get, "/api/environments", _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://elsewhere.example.com/api/environments");
            return response;
        });

        var ex = await Assert.ThrowsAsync<ApiException>(() => Client().GetEnvironmentsAsync());

        Assert.Contains("redirected", ex.Message);
        Assert.Single(_server.Requests);
    }

    [Fact]
    public async Task Connection_failure_is_a_network_error()
    {
        _server.Fault = _ => new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused");

        var ex = await Assert.ThrowsAsync<DockhandException>(() => Client().GetEnvironmentsAsync());

        Assert.Equal(ExitCodes.Network, ex.ExitCode);
        Assert.Contains("Could not connect", ex.Message);
    }

    [Fact]
    public async Task Timeout_is_a_network_error()
    {
        _server.On(HttpMethod.Get, "/api/environments", _ =>
        {
            Thread.Sleep(300);
            return FakeDockhandServer.Json(Array.Empty<object>());
        });
        _options.TimeoutOverride = TimeSpan.FromMilliseconds(50);

        var ex = await Assert.ThrowsAsync<DockhandException>(() => Client().GetEnvironmentsAsync());

        Assert.Equal(ExitCodes.Network, ex.ExitCode);
        Assert.Contains("--timeout", ex.Message);
    }

    [Fact]
    public async Task Update_check_polls_the_job_and_reports_progress()
    {
        _server.OnJson(HttpMethod.Post, "/api/containers/check-updates?env=1", new { jobId = "job-1" });
        _server.OnSequence(HttpMethod.Get, "/api/jobs/job-1",
            new { id = "job-1", status = "running", lines = new object[] { new { @event = "progress", data = new { @checked = 0, total = 2 } } } },
            new
            {
                id = "job-1",
                status = "done",
                lines = new object[]
                {
                    new { @event = "progress", data = new { @checked = 0, total = 2 } },
                    new { @event = "progress", data = new { @checked = 2, total = 2 } },
                },
                result = new
                {
                    total = 2,
                    updatesFound = 1,
                    results = new object[]
                    {
                        new { containerId = "a1", containerName = "nginx", imageName = "nginx:latest", hasUpdate = true, currentDigest = "sha256:111", newDigest = "sha256:222" },
                        new { containerId = "b2", containerName = "redis", imageName = "redis:7", hasUpdate = false },
                    },
                },
            });

        var progress = new List<JobProgress>();
        var summary = await Client().CheckUpdatesAsync(1, new SyncProgress(progress.Add));

        Assert.Equal(1, summary.UpdatesFound);
        Assert.Equal("sha256:222", summary.Results[0].NewDigest);
        Assert.Equal(2, progress.Count);
        Assert.Equal(2, progress[1].Data.GetProperty("checked").GetInt32());
        Assert.Contains("text/event-stream", _server.Requests[0].Accept);
    }

    [Fact]
    public async Task Update_check_accepts_a_synchronous_json_result()
    {
        _server.OnJson(HttpMethod.Post, "/api/containers/check-updates?env=1", new { total = 0, updatesFound = 0, results = Array.Empty<object>() });

        var summary = await Client().CheckUpdatesAsync(1, null);

        Assert.Equal(0, summary.Total);
        Assert.Single(_server.Requests);
    }

    [Fact]
    public async Task Update_check_reads_server_sent_events()
    {
        const string stream = "event: progress\ndata: {\"checked\":1,\"total\":1}\n\nevent: result\ndata: {\"total\":1,\"updatesFound\":0,\"results\":[]}\n\n";
        _server.On(HttpMethod.Post, "/api/containers/check-updates?env=1", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(stream, Encoding.UTF8, "text/event-stream"),
        });

        var progress = new List<JobProgress>();
        var summary = await Client().CheckUpdatesAsync(1, new SyncProgress(progress.Add));

        Assert.Equal(1, summary.Total);
        Assert.Single(progress);
    }

    [Fact]
    public async Task Failed_job_result_is_an_error()
    {
        _server.OnJson(HttpMethod.Post, "/api/containers/check-updates?env=1", new { jobId = "job-2" });
        _server.OnJson(HttpMethod.Get, "/api/jobs/job-2", new { id = "job-2", status = "error", lines = Array.Empty<object>(), result = new { success = false, error = "Docker unreachable" } });

        var ex = await Assert.ThrowsAsync<DockhandException>(() => Client().CheckUpdatesAsync(1, null));

        Assert.Contains("Docker unreachable", ex.Message);
    }

    [Fact]
    public async Task Batch_update_is_sent_once_and_not_retried_on_failure()
    {
        _server.OnJson(HttpMethod.Post, "/api/containers/batch-update?env=1", new { error = "Failed to run the batch update" }, HttpStatusCode.InternalServerError);

        await Assert.ThrowsAsync<ApiException>(() => Client().BatchUpdateAsync(1, ["a1"]));

        var request = Assert.Single(_server.Requests);
        Assert.Equal("{\"containerIds\":[\"a1\"]}", request.Body);
    }

    [Fact]
    public async Task Login_uses_session_cookie_to_create_token()
    {
        _server.On(HttpMethod.Post, "/api/auth/login", _ =>
        {
            var response = FakeDockhandServer.Json(new { success = true });
            response.Headers.Add("Set-Cookie", "dockhand_session=sess123; Path=/; HttpOnly; Secure");
            return response;
        });
        _server.OnJson(HttpMethod.Post, "/api/auth/tokens", new { token = Token, id = 5, tokenPrefix = "abcd" }, HttpStatusCode.Created);
        _server.OnJson(HttpMethod.Post, "/api/auth/logout", new { success = true });

        var client = Client(token: null);
        await client.LoginAsync("admin", "secret", null);
        var created = await client.CreateTokenAsync("cli", null, "secret");
        await client.LogoutAsync();

        Assert.Equal(Token, created.Token);
        var tokenRequest = _server.Requests.Single(r => r.Uri.AbsolutePath == "/api/auth/tokens");
        Assert.Equal("dockhand_session=sess123", tokenRequest.Cookie);
        Assert.Null(tokenRequest.Authorization);
        using var body = JsonDocument.Parse(tokenRequest.Body!);
        Assert.Equal("cli", body.RootElement.GetProperty("name").GetString());
        Assert.Null(_server.Requests.Single(r => r.Uri.AbsolutePath == "/api/auth/login").Authorization);
    }

    [Fact]
    public async Task Stack_action_returns_job_result()
    {
        _server.OnJson(HttpMethod.Post, "/api/stacks/web/down?env=1", new { jobId = "job-3" });
        _server.OnJson(HttpMethod.Get, "/api/jobs/job-3", new { id = "job-3", status = "done", lines = Array.Empty<object>(), result = new { success = true, output = "Stopped" } });

        var result = await Client().RunStackActionAsync(1, "web", Core.Interfaces.StackAction.Down, new() { RemoveVolumes = true }, null);

        Assert.True(result.Success);
        Assert.Equal("{\"removeVolumes\":true}", _server.Requests[0].Body);
    }

    [Fact]
    public async Task Database_health_503_returns_unhealthy_body()
    {
        _server.OnJson(HttpMethod.Get, "/api/health/database", new { healthy = false, database = "sqlite", migrationsTable = true, appliedMigrations = 3, pendingMigrations = 1, tables = 10, timestamp = "x" }, HttpStatusCode.ServiceUnavailable);

        var health = await Client().GetDatabaseHealthAsync();

        Assert.False(health.Healthy);
    }

    private sealed class SyncProgress(Action<JobProgress> report) : IProgress<JobProgress>
    {
        public void Report(JobProgress value) => report(value);
    }
}
