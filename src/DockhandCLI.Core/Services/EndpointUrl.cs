using DockhandCLI.Core.Models;

namespace DockhandCLI.Core.Services;

public static class EndpointUrl
{
    /// <summary>Validates and normalizes an endpoint base URL (absolute, http(s), no credentials, query or fragment).</summary>
    public static string Normalize(string url, bool allowHttp)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            throw DockhandException.Usage($"'{url}' is not an absolute URL. Example: https://docker01.example.com");
        }

        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            throw DockhandException.Usage($"Unsupported URL scheme '{uri.Scheme}'. Use https://.");
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !allowHttp)
        {
            throw DockhandException.Usage(
                "Plain http:// would send your API token unencrypted. Use https://, or pass --allow-http if you accept that risk.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw DockhandException.Usage("Do not put credentials in the URL. Use 'dockhand endpoint auth <alias>' instead.");
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw DockhandException.Usage("The endpoint URL must not contain a query string or fragment.");
        }

        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }
}
