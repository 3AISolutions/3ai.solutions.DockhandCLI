using System.Text.RegularExpressions;

namespace DockhandCLI.Core.Services;

/// <summary>Keeps API tokens out of terminal output, logs and exception messages.</summary>
public static partial class SecretRedactor
{
    /// <summary>Masks a token to its prefix and last four characters, e.g. <c>dh_…x9Qa</c>.</summary>
    public static string Mask(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return "(none)";
        }

        var prefix = token.StartsWith("dh_", StringComparison.Ordinal) ? "dh_" : "";
        return token.Length <= prefix.Length + 8 ? $"{prefix}…" : $"{prefix}…{token[^4..]}";
    }

    /// <summary>Replaces anything that looks like a Dockhand token or bearer credential.</summary>
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        text = TokenPattern().Replace(text, m => Mask(m.Value));
        return BearerPattern().Replace(text, "Bearer ***");
    }

    [GeneratedRegex(@"dh_[A-Za-z0-9_\-]{8,}")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9_\-\.~+/=]{8,}", RegexOptions.IgnoreCase)]
    private static partial Regex BearerPattern();
}

public static partial class TokenFormat
{
    /// <summary>Dockhand tokens are <c>dh_</c> plus base64url text; the server rejects headers over 207 characters.</summary>
    public static bool IsValid(string token) => token.Length <= 200 && Pattern().IsMatch(token);

    [GeneratedRegex(@"^dh_[A-Za-z0-9_\-]{8,}$")]
    private static partial Regex Pattern();
}

public static class TokenEnvironment
{
    public const string Prefix = "DOCKHAND_TOKEN_";

    /// <summary>Name of the variable that overrides the stored token, e.g. <c>DOCKHAND_TOKEN_SRV01</c>.</summary>
    public static string VariableFor(string alias) =>
        Prefix + new string(alias.ToUpperInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());
}
