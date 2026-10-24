using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DockhandCLI.Core.Interfaces;
using DockhandCLI.Core.Models;
using DockhandCLI.Core.Services;
using Microsoft.Extensions.Logging;

namespace DockhandCLI.Infrastructure.Api;

/// <summary>
/// Typed client for one Dockhand installation. Every method maps to an operation in the Dockhand
/// OpenAPI document (see docs/ApiAnalysis.md). Requests are never retried automatically.
/// </summary>
internal sealed class DockhandApiClient : IDockhandApi
{
    private const string SessionCookieName = "dockhand_session";
    private const string JobAccept = "text/event-stream, application/json";

    private readonly HttpClient _http;
    private readonly string? _token;
    private readonly ApiClientOptions _options;
    private readonly ILogger _logger;
    private string? _sessionCookie;

    public DockhandApiClient(HttpClient http, EndpointConfig endpoint, string? token, ApiClientOptions options, ILogger logger)
    {
        _http = http;
        _http.BaseAddress = new Uri(endpoint.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = Timeout.InfiniteTimeSpan;
        Endpoint = endpoint;
        _token = string.IsNullOrWhiteSpace(token) ? null : token;
        _options = options;
        _logger = logger;
    }

    public EndpointConfig Endpoint { get; }

    public bool HasCredentials => _token is not null || _sessionCookie is not null;

    private TimeSpan RequestTimeout =>
        _options.TimeoutOverride
        ?? (Endpoint.TimeoutSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : _options.DefaultRequestTimeout);

    private TimeSpan LongTimeout => _options.TimeoutOverride ?? _options.LongOperationTimeout;

    // ---- health and authentication -------------------------------------------------------

    public Task<HealthStatus> GetHealthAsync(CancellationToken cancellationToken = default) =>
        GetAsync<HealthStatus>("api/health", cancellationToken);

    public Task<DatabaseHealth> GetDatabaseHealthAsync(CancellationToken cancellationToken = default) =>
        WithTimeoutAsync(RequestTimeout, "api/health/database", async ct =>
        {
            using var response = await SendAsync(HttpMethod.Get, "api/health/database", null, null, ct).ConfigureAwait(false);
            // 503 means "unhealthy" and carries the same body as 200.
            if (response.StatusCode != HttpStatusCode.ServiceUnavailable)
            {
                await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
            }

            return await ReadAsync<DatabaseHealth>(response, ct).ConfigureAwait(false);
        }, cancellationToken);

    public Task<SessionInfo> GetSessionAsync(CancellationToken cancellationToken = default) =>
        GetAsync<SessionInfo>("api/auth/session", cancellationToken);

    public Task<LoginResult> LoginAsync(string username, string password, string? mfaToken, CancellationToken cancellationToken = default) =>
        WithTimeoutAsync(RequestTimeout, "api/auth/login", async ct =>
        {
            var body = new Dictionary<string, string> { ["username"] = username, ["password"] = password };
            if (!string.IsNullOrEmpty(mfaToken))
            {
                body["mfaToken"] = mfaToken;
            }

            using var response = await SendAsync(HttpMethod.Post, "api/auth/login", body, null, ct, authenticate: false).ConfigureAwait(false);
            await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
            var result = await ReadAsync<LoginResult>(response, ct).ConfigureAwait(false);
            _sessionCookie = ExtractSessionCookie(response) ?? _sessionCookie;

            if (result.RequiresMfa != true && _sessionCookie is null)
            {
                throw new DockhandException(ExitCodes.Unauthorized, $"'{Endpoint.Alias}' accepted the login but did not return a session cookie.");
            }

            return result;
        }, cancellationToken);

    public Task<CreatedToken> CreateTokenAsync(string name, DateTimeOffset? expiresAt, string? password, CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, string> { ["name"] = name };
        if (expiresAt is { } expires)
        {
            body["expiresAt"] = expires.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        if (password is not null)
        {
            body["password"] = password;
        }

        return PostAsync<CreatedToken>("api/auth/tokens", body, RequestTimeout, cancellationToken);
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        if (_sessionCookie is null)
        {
            return;
        }

        await WithTimeoutAsync(RequestTimeout, "api/auth/logout", async ct =>
        {
            using var response = await SendAsync(HttpMethod.Post, "api/auth/logout", null, null, ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
        _sessionCookie = null;
    }

    // ---- environments and containers -----------------------------------------------------

    public async Task<IReadOnlyList<DockerEnvironment>> GetEnvironmentsAsync(CancellationToken cancellationToken = default) =>
        await GetAsync<List<DockerEnvironment>>("api/environments", cancellationToken).ConfigureAwait(false);

    public Task<DockerEnvironment> GetEnvironmentAsync(int id, CancellationToken cancellationToken = default) =>
        GetAsync<DockerEnvironment>($"api/environments/{id.ToString(CultureInfo.InvariantCulture)}", cancellationToken);

    public async Task<IReadOnlyList<ContainerSummary>> GetContainersAsync(int environmentId, bool includeStopped, CancellationToken cancellationToken = default) =>
        await GetAsync<List<ContainerSummary>>(
            Url("api/containers", ("env", Env(environmentId)), ("all", includeStopped ? "true" : "false")), cancellationToken).ConfigureAwait(false);

    public Task<JsonElement> InspectContainerAsync(int environmentId, string containerId, CancellationToken cancellationToken = default) =>
        GetAsync<JsonElement>(Url($"api/containers/{Segment(containerId)}", ("env", Env(environmentId))), cancellationToken);

    public Task<ContainerLogs> GetContainerLogsAsync(
        int environmentId, string containerId, int? tail, string? since, string? until, CancellationToken cancellationToken = default) =>
        GetAsync<ContainerLogs>(
            Url($"api/containers/{Segment(containerId)}/logs",
                ("env", Env(environmentId)),
                ("tail", tail?.ToString(CultureInfo.InvariantCulture)),
                ("since", since),
                ("until", until)),
            cancellationToken);

    public Task<ActionResult> RunContainerActionAsync(int environmentId, string containerId, ContainerAction action, CancellationToken cancellationToken = default)
    {
        var verb = action.ToString().ToLowerInvariant();
        return PostAsync<ActionResult>(
            Url($"api/containers/{Segment(containerId)}/{verb}", ("env", Env(environmentId))), null, RequestTimeout, cancellationToken);
    }

    // ---- updates -------------------------------------------------------------------------

    public async Task<UpdateCheckSummary> CheckUpdatesAsync(int environmentId, IProgress<JobProgress>? progress, CancellationToken cancellationToken = default)
    {
        var url = Url("api/containers/check-updates", ("env", Env(environmentId)));
        var result = await RunJobAsync(url, null, progress, cancellationToken).ConfigureAwait(false);

        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("results", out _))
        {
            return Deserialize<UpdateCheckSummary>(result, url);
        }

        throw new DockhandException(ExitCodes.ApiError, $"The update check on environment {environmentId} failed: {ErrorText(result) ?? "no result was returned"}.");
    }

    public Task<CachedUpdatesResponse> GetCachedUpdatesAsync(int environmentId, CancellationToken cancellationToken = default) =>
        GetAsync<CachedUpdatesResponse>(Url("api/containers/check-updates", ("env", Env(environmentId))), cancellationToken);

    public Task<PendingUpdatesResponse> GetPendingUpdatesAsync(int environmentId, CancellationToken cancellationToken = default) =>
        GetAsync<PendingUpdatesResponse>(Url("api/containers/pending-updates", ("env", Env(environmentId))), cancellationToken);

    public Task<BatchUpdateResponse> BatchUpdateAsync(int environmentId, IReadOnlyList<string> containerIds, CancellationToken cancellationToken = default) =>
        PostAsync<BatchUpdateResponse>(
            Url("api/containers/batch-update", ("env", Env(environmentId))),
            new { containerIds },
            LongTimeout,
            cancellationToken);

    // ---- images, stacks, jobs, system ----------------------------------------------------

    public async Task<IReadOnlyList<ImageSummary>> GetImagesAsync(int environmentId, CancellationToken cancellationToken = default) =>
        await GetAsync<List<ImageSummary>>(Url("api/images", ("env", Env(environmentId))), cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<StackSummary>> GetStacksAsync(int environmentId, CancellationToken cancellationToken = default) =>
        await GetAsync<List<StackSummary>>(Url("api/stacks", ("env", Env(environmentId))), cancellationToken).ConfigureAwait(false);

    public Task<StackCompose> GetStackComposeAsync(int environmentId, string name, CancellationToken cancellationToken = default) =>
        GetAsync<StackCompose>(Url($"api/stacks/{Segment(name)}/compose", ("env", Env(environmentId))), cancellationToken);

    public async Task<StackJobResult> RunStackActionAsync(
        int environmentId, string name, StackAction action, StackActionOptions options, IProgress<JobProgress>? progress, CancellationToken cancellationToken = default)
    {
        var path = $"api/stacks/{Segment(name)}/{action.ToString().ToLowerInvariant()}";
        object? body = null;
        string url;
        switch (action)
        {
            case StackAction.Restart:
                url = Url(path, ("env", Env(environmentId)), ("mode", options.RestartMode));
                break;
            case StackAction.Deploy:
                url = Url(path, ("env", Env(environmentId)));
                body = new { pull = options.Pull, build = options.Build, forceRecreate = options.ForceRecreate };
                break;
            case StackAction.Down:
                url = Url(path, ("env", Env(environmentId)));
                body = new { removeVolumes = options.RemoveVolumes };
                break;
            default:
                url = Url(path, ("env", Env(environmentId)));
                break;
        }

        var result = await RunJobAsync(url, body, progress, cancellationToken).ConfigureAwait(false);
        return Deserialize<StackJobResult>(result, url);
    }

    public Task<JobStatus> GetJobAsync(string jobId, CancellationToken cancellationToken = default) =>
        GetAsync<JobStatus>($"api/jobs/{Segment(jobId)}", cancellationToken);

    public Task<bool> CancelJobAsync(string jobId, CancellationToken cancellationToken = default) =>
        WithTimeoutAsync(RequestTimeout, "api/jobs", async ct =>
        {
            using var response = await SendAsync(HttpMethod.Delete, $"api/jobs/{Segment(jobId)}", null, null, ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
            var result = await ReadAsync<JsonElement>(response, ct).ConfigureAwait(false);
            return result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("cancelled", out var cancelled)
                && cancelled.ValueKind == JsonValueKind.True;
        }, cancellationToken);

    public Task<JsonElement> GetSystemInfoAsync(int? environmentId, CancellationToken cancellationToken = default) =>
        GetAsync<JsonElement>(Url("api/system", ("env", environmentId is { } id ? Env(id) : null)), cancellationToken);

    // ---- jobs ----------------------------------------------------------------------------

    /// <summary>
    /// Runs a Dockhand job endpoint. Current servers answer with <c>{jobId}</c> and the job is polled via
    /// <c>GET /api/jobs/{id}</c>; older servers stream Server-Sent Events. Both report progress and return the final result.
    /// </summary>
    private Task<JsonElement> RunJobAsync(string url, object? body, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        WithTimeoutAsync(LongTimeout, url, async ct =>
        {
            using var response = await SendAsync(HttpMethod.Post, url, body, JobAccept, ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, ct).ConfigureAwait(false);

            if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            {
                return await ReadEventStreamAsync(response, progress, ct).ConfigureAwait(false);
            }

            var json = await ReadAsync<JsonElement>(response, ct).ConfigureAwait(false);
            if (json.ValueKind == JsonValueKind.Object
                && json.TryGetProperty("jobId", out var jobId)
                && jobId.ValueKind == JsonValueKind.String
                && json.EnumerateObject().Count() == 1)
            {
                return await PollJobAsync(jobId.GetString()!, progress, ct).ConfigureAwait(false);
            }

            return json;
        }, cancellationToken);

    private async Task<JsonElement> PollJobAsync(string jobId, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Polling job {JobId}", jobId);
        var seen = 0;
        try
        {
            while (true)
            {
                JobStatus job;
                using (var response = await SendAsync(HttpMethod.Get, $"api/jobs/{Segment(jobId)}", null, null, cancellationToken).ConfigureAwait(false))
                {
                    await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
                    job = await ReadAsync<JobStatus>(response, cancellationToken).ConfigureAwait(false);
                }

                for (; seen < job.Lines.Count; seen++)
                {
                    progress?.Report(new JobProgress(job.Lines[seen].Event, job.Lines[seen].Data));
                }

                if (string.Equals(job.Status, "running", StringComparison.OrdinalIgnoreCase))
                {
                    await Task.Delay(_options.JobPollInterval, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (job.Result is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } result)
                {
                    return result;
                }

                var last = job.Lines.LastOrDefault(l => l.Event == "result");
                if (last is not null)
                {
                    return last.Data;
                }

                throw new DockhandException(ExitCodes.ApiError, $"Job {jobId} ended with status '{job.Status}' and no result.");
            }
        }
        catch (OperationCanceledException)
        {
            await TryCancelJobAsync(jobId).ConfigureAwait(false);
            throw;
        }
    }

    private async Task TryCancelJobAsync(string jobId)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var response = await SendAsync(HttpMethod.Delete, $"api/jobs/{Segment(jobId)}", null, null, cts.Token).ConfigureAwait(false);
            _logger.LogDebug("Requested cancellation of job {JobId}: {Status}", jobId, (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            _logger.LogDebug("Could not request cancellation of job {JobId}: {Message}", jobId, ex.Message);
        }
    }

    private static async Task<JsonElement> ReadEventStreamAsync(HttpResponseMessage response, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var item in SseReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            JsonElement data;
            try
            {
                data = JsonSerializer.Deserialize<JsonElement>(item.Data, ApiJson.Options);
            }
            catch (JsonException)
            {
                data = JsonSerializer.SerializeToElement(item.Data);
            }

            switch (item.Event)
            {
                case "result":
                    return data;
                case "error":
                    throw new DockhandException(ExitCodes.ApiError, ErrorText(data) ?? item.Data);
                default:
                    progress?.Report(new JobProgress(item.Event, data));
                    break;
            }
        }

        throw new DockhandException(ExitCodes.ApiError, "The event stream ended without a result.");
    }

    // ---- HTTP plumbing -------------------------------------------------------------------

    private Task<T> GetAsync<T>(string url, CancellationToken cancellationToken) =>
        WithTimeoutAsync(RequestTimeout, url, async ct =>
        {
            using var response = await SendAsync(HttpMethod.Get, url, null, null, ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
            return await ReadAsync<T>(response, ct).ConfigureAwait(false);
        }, cancellationToken);

    private Task<T> PostAsync<T>(string url, object? body, TimeSpan timeout, CancellationToken cancellationToken) =>
        WithTimeoutAsync(timeout, url, async ct =>
        {
            using var response = await SendAsync(HttpMethod.Post, url, body, null, ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
            return await ReadAsync<T>(response, ct).ConfigureAwait(false);
        }, cancellationToken);

    private async Task<T> WithTimeoutAsync<T>(TimeSpan timeout, string what, Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            return await operation(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DockhandException(
                ExitCodes.Network,
                $"No response from '{Endpoint.Alias}' ({Endpoint.BaseUrl}) within {FormatTimeout(timeout)} for {PathOnly(what)}. Use --timeout to allow longer.");
        }
        catch (HttpRequestException ex)
        {
            throw NetworkError(ex);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, object? body, string? accept, CancellationToken cancellationToken, bool authenticate = true)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Accept.ParseAdd(accept ?? "application/json");

        if (authenticate)
        {
            if (_sessionCookie is not null)
            {
                request.Headers.Add("Cookie", $"{SessionCookieName}={_sessionCookie}");
            }
            else if (_token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            }
        }

        if (body is not null)
        {
            // Buffered (with Content-Length) rather than chunked, which some reverse proxies reject.
            request.Content = new StringContent(JsonSerializer.Serialize(body, body.GetType(), ApiJson.Options), Encoding.UTF8, "application/json");
        }

        _logger.LogDebug("{Method} {Url}", method.Method, new Uri(_http.BaseAddress!, url));
        var stopwatch = Stopwatch.StartNew();
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("{Status} {Url} in {Elapsed} ms", (int)response.StatusCode, PathOnly(url), stopwatch.ElapsedMilliseconds);
        return response;
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var status = response.StatusCode;
        var path = PathOnly(response.RequestMessage?.RequestUri?.PathAndQuery ?? "");

        if ((int)status is >= 300 and < 400)
        {
            throw new ApiException(status,
                $"'{Endpoint.Alias}' redirected {path} to {response.Headers.Location}. Update the endpoint URL; redirects are not followed so the token is never sent elsewhere.");
        }

        string? apiMessage = null;
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            apiMessage = ExtractMessage(text);
        }
        catch (HttpRequestException)
        {
        }

        apiMessage = apiMessage is null ? null : SecretRedactor.Redact(apiMessage);
        var detail = apiMessage is null ? "" : $": {apiMessage}";
        var alias = Endpoint.Alias;

        var message = status switch
        {
            HttpStatusCode.Unauthorized when !HasCredentials =>
                $"'{alias}' requires authentication and no API token is configured. Run 'dockhand endpoint auth {alias}'.",
            HttpStatusCode.Unauthorized =>
                $"'{alias}' rejected the credentials (HTTP 401{detail}). Run 'dockhand endpoint auth {alias}' to sign in again.",
            HttpStatusCode.Forbidden => $"Permission denied by '{alias}' for {path} (HTTP 403{detail}).",
            HttpStatusCode.NotFound => $"Not found on '{alias}': {path} (HTTP 404{detail}).",
            HttpStatusCode.TooManyRequests =>
                $"'{alias}' is rate limiting this client (HTTP 429). Retry after {response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 300:0} seconds. Dockhand blocks an IP for 5 minutes after 10 failed token attempts.",
            _ => $"'{alias}' returned HTTP {(int)status} ({response.ReasonPhrase}) for {path}{detail}.",
        };

        throw new ApiException(status, message, apiMessage);
    }

    private async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var value = await response.Content.ReadFromJsonAsync<T>(ApiJson.Options, cancellationToken).ConfigureAwait(false);
            return value ?? throw new JsonException("The response body was empty.");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "unknown";
            throw new DockhandException(
                ExitCodes.ApiError,
                $"'{Endpoint.Alias}' returned an unexpected response for {PathOnly(response.RequestMessage?.RequestUri?.PathAndQuery ?? "")} (content type {mediaType}). Is {Endpoint.BaseUrl} a Dockhand server?",
                ex);
        }
    }

    private T Deserialize<T>(JsonElement element, string url)
    {
        try
        {
            return element.Deserialize<T>(ApiJson.Options) ?? throw new JsonException("null");
        }
        catch (JsonException ex)
        {
            throw new DockhandException(ExitCodes.ApiError, $"'{Endpoint.Alias}' returned an unexpected result for {PathOnly(url)}.", ex);
        }
    }

    private DockhandException NetworkError(HttpRequestException ex)
    {
        var target = $"'{Endpoint.Alias}' ({Endpoint.BaseUrl})";
        var cause = ex.InnerException is { } inner ? $"{ex.Message} {inner.Message}" : ex.Message;
        var message = ex.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => $"Could not resolve the host of {target}.",
            HttpRequestError.SecureConnectionError =>
                $"TLS connection to {target} failed: {ex.InnerException?.Message ?? ex.Message} Certificate validation is always enforced; install the server's CA certificate in your OS trust store.",
            HttpRequestError.ConnectionError => $"Could not connect to {target}: {cause}",
            _ => $"Request to {target} failed: {cause}",
        };
        return new DockhandException(ExitCodes.Network, SecretRedactor.Redact(message), ex);
    }

    internal static string? ExtractMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return ErrorText(document.RootElement);
        }
        catch (JsonException)
        {
            var text = body.Trim();
            return text.StartsWith('<') ? null : text.Length > 300 ? text[..300] + "…" : text;
        }
    }

    private static string? ErrorText(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        }

        string? Get(string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        var main = Get("error") ?? Get("message");
        var details = Get("details");
        return main is null ? details : details is null ? main : $"{main} ({details})";
    }

    private static string? ExtractSessionCookie(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            return null;
        }

        foreach (var cookie in cookies)
        {
            var first = cookie.Split(';', 2)[0].Trim();
            if (first.StartsWith(SessionCookieName + "=", StringComparison.Ordinal))
            {
                var value = first[(SessionCookieName.Length + 1)..];
                return value.Length > 0 ? value : null;
            }
        }

        return null;
    }

    private static string Url(string path, params (string Name, string? Value)[] query)
    {
        var builder = new StringBuilder(path);
        var separator = '?';
        foreach (var (name, value) in query)
        {
            if (value is null)
            {
                continue;
            }

            builder.Append(separator).Append(name).Append('=').Append(Uri.EscapeDataString(value));
            separator = '&';
        }

        return builder.ToString();
    }

    private static string Env(int id) => id.ToString(CultureInfo.InvariantCulture);

    private static string Segment(string value) => Uri.EscapeDataString(value);

    private static string PathOnly(string url)
    {
        var path = url.Split('?', 2)[0];
        return path.StartsWith('/') ? path : "/" + path;
    }

    private static string FormatTimeout(TimeSpan timeout) =>
        timeout.TotalSeconds < 120 ? $"{timeout.TotalSeconds:0.#} s" : $"{timeout.TotalMinutes:0.#} min";
}
