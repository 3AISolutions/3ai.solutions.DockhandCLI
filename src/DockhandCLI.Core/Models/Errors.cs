using System.Net;

namespace DockhandCLI.Core.Models;

/// <summary>Process exit codes. Zero is success; everything else is a failure class scripts can branch on.</summary>
public static class ExitCodes
{
    public const int Success = 0;
    public const int Error = 1;
    public const int Usage = 2;
    public const int NotFound = 3;
    public const int Unauthorized = 4;
    public const int ApiError = 5;
    public const int PartialFailure = 6;
    public const int Network = 7;
    public const int Aborted = 8;
    public const int Cancelled = 130;
}

/// <summary>An expected failure with a user-facing message and an exit code.</summary>
public class DockhandException(int exitCode, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public int ExitCode { get; } = exitCode;

    public static DockhandException Usage(string message) => new(ExitCodes.Usage, message);

    public static DockhandException NotFound(string message) => new(ExitCodes.NotFound, message);

    public static DockhandException Aborted(string message = "Operation aborted.") => new(ExitCodes.Aborted, message);

    public static DockhandException Config(string message, Exception? inner = null) => new(ExitCodes.Error, message, inner);
}

/// <summary>A non-success HTTP response from a Dockhand API.</summary>
public sealed class ApiException(HttpStatusCode statusCode, string message, string? apiMessage = null)
    : DockhandException(MapExitCode(statusCode), message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    /// <summary>The error text the server returned, if any.</summary>
    public string? ApiMessage { get; } = apiMessage;

    private static int MapExitCode(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ExitCodes.Unauthorized,
        HttpStatusCode.NotFound => ExitCodes.NotFound,
        _ => ExitCodes.ApiError,
    };
}
