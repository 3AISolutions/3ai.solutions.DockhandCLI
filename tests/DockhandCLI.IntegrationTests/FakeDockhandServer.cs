using System.Net;
using System.Text;
using System.Text.Json;

namespace DockhandCLI.IntegrationTests;

internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Authorization, string? Cookie, string? Accept, string? Body)
{
    public string PathAndQuery => Uri.PathAndQuery;
}

/// <summary>
/// An in-memory Dockhand API. Routes match on method and path (+ query when the route contains '?'),
/// optionally scoped to a host. Unmatched requests get a 404 like the real server.
/// </summary>
internal sealed class FakeDockhandServer : HttpMessageHandler
{
    private readonly List<(HttpMethod Method, string Route, string? Host, Func<RecordedRequest, HttpResponseMessage> Respond)> _routes = [];

    public List<RecordedRequest> Requests { get; } = [];

    public Func<RecordedRequest, Exception?>? Fault { get; set; }

    public FakeDockhandServer On(HttpMethod method, string route, Func<RecordedRequest, HttpResponseMessage> respond, string? host = null)
    {
        _routes.Insert(0, (method, route, host, respond));
        return this;
    }

    public FakeDockhandServer OnJson(HttpMethod method, string route, object body, HttpStatusCode status = HttpStatusCode.OK, string? host = null) =>
        On(method, route, _ => Json(body, status), host);

    /// <summary>Returns the responses in order; the last one repeats.</summary>
    public FakeDockhandServer OnSequence(HttpMethod method, string route, params object[] bodies)
    {
        var index = 0;
        return On(method, route, _ => Json(bodies[Math.Min(index++, bodies.Length - 1)]));
    }

    public IEnumerable<RecordedRequest> Matching(HttpMethod method, string pathPrefix) =>
        Requests.Where(r => r.Method == method && r.Uri.AbsolutePath.StartsWith(pathPrefix, StringComparison.Ordinal));

    public static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body is string s ? s : JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json"),
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var recorded = new RecordedRequest(
            request.Method,
            request.RequestUri!,
            request.Headers.Authorization?.ToString(),
            request.Headers.TryGetValues("Cookie", out var cookies) ? string.Join("; ", cookies) : null,
            request.Headers.Accept.ToString(),
            request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
        Requests.Add(recorded);

        if (Fault?.Invoke(recorded) is { } fault)
        {
            throw fault;
        }

        foreach (var (method, route, host, respond) in _routes)
        {
            if (method != request.Method || (host is not null && host != request.RequestUri!.Host))
            {
                continue;
            }

            var target = route.Contains('?') ? request.RequestUri!.PathAndQuery : request.RequestUri!.AbsolutePath;
            if (target == route)
            {
                var response = respond(recorded);
                response.RequestMessage = request;
                return response;
            }
        }

        var notFound = Json(new { error = "Not found" }, HttpStatusCode.NotFound);
        notFound.RequestMessage = request;
        return notFound;
    }

    /// <summary>Standard data for one installation with two environments.</summary>
    public FakeDockhandServer WithDefaultData()
    {
        OnJson(HttpMethod.Get, "/api/health", new { status = "ok", timestamp = "2026-10-24T12:00:00.000Z" });
        OnJson(HttpMethod.Get, "/api/auth/session", new { authenticated = false, authEnabled = true });
        OnJson(HttpMethod.Get, "/api/environments", new[]
        {
            new { id = 1, name = "hhdocker01", connectionType = "socket" },
            new { id = 2, name = "hhdocker02", connectionType = "hawser-edge" },
        });
        OnJson(HttpMethod.Get, "/api/containers", new[]
        {
            new { id = "a1b2c3d4e5f6a7b8c9d0a1b2c3d4e5f6", name = "nginx", image = "nginx:latest", state = "running", status = "Up 2 hours" },
            new { id = "0f0e0d0c0b0a09080706050403020100", name = "redis", image = "redis:7", state = "exited", status = "Exited (0) 1 day ago" },
        });
        OnJson(HttpMethod.Get, "/api/containers/pending-updates", new
        {
            environmentId = 1,
            minimumReleaseAgeHours = 0,
            pendingUpdates = new[]
            {
                new { containerId = "a1b2c3d4e5f6a7b8c9d0a1b2c3d4e5f6", containerName = "nginx", currentImage = "nginx:latest", checkedAt = "2026-10-24T10:00:00Z", hasImageUpdate = true, pinnedToDigest = false },
            },
        });
        return this;
    }
}
