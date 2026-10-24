using System.Text.Json;
using System.Text.Json.Serialization;

namespace DockhandCLI.Infrastructure.Api;

/// <summary>Timeouts and polling behavior of API clients. <see cref="TimeoutOverride"/> is set from <c>--timeout</c>.</summary>
public sealed class ApiClientOptions
{
    /// <summary>Applies to every request, including long-running ones, when set.</summary>
    public TimeSpan? TimeoutOverride { get; set; }

    /// <summary>Default for ordinary requests when the endpoint has no timeout configured.</summary>
    public TimeSpan DefaultRequestTimeout { get; set; } = TimeSpan.FromSeconds(100);

    /// <summary>Update checks, container updates and stack jobs can take minutes.</summary>
    public TimeSpan LongOperationTimeout { get; set; } = TimeSpan.FromMinutes(30);

    public TimeSpan JobPollInterval { get; set; } = TimeSpan.FromMilliseconds(500);
}

internal static class ApiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}
