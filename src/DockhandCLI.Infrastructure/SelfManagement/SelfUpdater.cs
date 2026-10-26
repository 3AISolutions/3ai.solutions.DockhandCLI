using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using DockhandCLI.Core.Models;
using DockhandCLI.Infrastructure.Authentication;

namespace DockhandCLI.Infrastructure.SelfManagement;

public sealed record ReleaseAsset(string Name, Uri DownloadUrl, long Size);

public sealed record ReleaseInfo(string Tag, Version Version, Uri PageUrl, IReadOnlyList<ReleaseAsset> Assets);

/// <param name="Asset">The archive for this platform, or null when the release has none.</param>
public sealed record UpdateCheck(Version Current, ReleaseInfo Latest, ReleaseAsset? Asset)
{
    public bool UpdateAvailable => Latest.Version > Current;
}

/// <summary>Finds the latest GitHub release and replaces the running executable with it.</summary>
public sealed class SelfUpdater
{
    public const string HttpClientName = "github";
    public const string Repository = "3AISolutions/3ai.solutions.DockhandCLI";
    public const string ChecksumsAsset = "SHA256SUMS.txt";

    private readonly IHttpClientFactory _httpFactory;
    private readonly IProcessRunner _processes;

    internal SelfUpdater(IHttpClientFactory httpFactory, IProcessRunner processes)
    {
        _httpFactory = httpFactory;
        _processes = processes;
    }

    private HttpClient Http => _httpFactory.CreateClient(HttpClientName);

    public async Task<UpdateCheck> CheckAsync(Version current, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"https://api.github.com/repos/{Repository}/releases/latest"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw DockhandException.NotFound($"No release of {Repository} was found on GitHub.");
        }

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            throw new DockhandException(ExitCodes.Network, "The GitHub API rate limit was reached. Try again later.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new DockhandException(ExitCodes.Network, $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase} when looking for the latest release.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var release = ParseRelease(document.RootElement);
        return new UpdateCheck(current, release, FindAsset(release, RuntimeIdentifier()));
    }

    /// <summary>Downloads, verifies and installs <paramref name="check"/>'s release over <paramref name="location"/>.</summary>
    public async Task InstallAsync(UpdateCheck check, InstallLocation location, CancellationToken cancellationToken = default)
    {
        var asset = check.Asset ?? throw DockhandException.NotFound(
            $"Release {check.Latest.Tag} has no download for {RuntimeIdentifier() ?? RuntimeInformation.RuntimeIdentifier}. See {check.Latest.PageUrl}");
        var checksums = check.Latest.Assets.FirstOrDefault(a => a.Name == ChecksumsAsset)
            ?? throw new DockhandException(ExitCodes.Error, $"Release {check.Latest.Tag} has no {ChecksumsAsset}, so the download cannot be verified.");

        var work = Path.Combine(Path.GetTempPath(), "dockhand-update-" + Guid.NewGuid().ToString("N"));
        var staged = Path.Combine(location.Directory, $"dockhand.{Guid.NewGuid():N}.new" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        Directory.CreateDirectory(work);
        try
        {
            var archive = Path.Combine(work, asset.Name);
            await DownloadAsync(asset.DownloadUrl, archive, cancellationToken).ConfigureAwait(false);

            var sums = ParseChecksums(await DownloadStringAsync(checksums.DownloadUrl, cancellationToken).ConfigureAwait(false));
            if (!sums.TryGetValue(asset.Name, out var expected))
            {
                throw new DockhandException(ExitCodes.Error, $"{ChecksumsAsset} has no entry for {asset.Name}.");
            }

            await using (var file = File.OpenRead(archive))
            {
                var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    throw new DockhandException(ExitCodes.Error, $"Checksum mismatch for {asset.Name}: the download is damaged or was tampered with.");
                }
            }

            await WriteStagedAsync(() => ExtractExecutableAsync(archive, staged, cancellationToken), location).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(staged, File.GetUnixFileMode(location.ExecutablePath) | UnixFileMode.UserExecute);
            }

            await VerifyAsync(staged, check.Latest.Version, cancellationToken).ConfigureAwait(false);
            Replace(location.ExecutablePath, staged);
        }
        finally
        {
            TryDelete(staged);
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Temporary files; the OS cleans them up eventually.
            }
        }
    }

    // ---- release metadata -----------------------------------------------------------------------

    internal static ReleaseInfo ParseRelease(JsonElement release)
    {
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        var version = ParseVersion(tag) ?? throw new DockhandException(ExitCodes.Error, $"The latest release tag '{tag}' is not a version number.");
        var assets = release.TryGetProperty("assets", out var list)
            ? list.EnumerateArray()
                .Select(a => new ReleaseAsset(a.GetProperty("name").GetString() ?? "", new Uri(a.GetProperty("browser_download_url").GetString()!), a.GetProperty("size").GetInt64()))
                .ToList()
            : [];
        var page = release.TryGetProperty("html_url", out var url) && url.GetString() is { } html
            ? new Uri(html)
            : new Uri($"https://github.com/{Repository}/releases/tag/{tag}");
        return new ReleaseInfo(tag, version, page, assets);
    }

    /// <summary>Parses <c>v1.2.3</c>, <c>1.2.3</c> or <c>1.2.3-beta+abc</c> as 1.2.3.</summary>
    public static Version? ParseVersion(string text)
    {
        var core = text.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        return Version.TryParse(core, out var version)
            ? new Version(version.Major, version.Minor, Math.Max(version.Build, 0))
            : null;
    }

    /// <summary>The release platform of this process (win-x64, linux-arm64, …), or null when none is published for it.</summary>
    internal static string? RuntimeIdentifier()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsLinux() ? "linux" : null;
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => null,
        };
        return os is null || arch is null ? null : $"{os}-{arch}";
    }

    /// <summary>Release archives are named <c>dockhand-&lt;tag&gt;-&lt;rid&gt;.zip|tar.gz</c>.</summary>
    internal static ReleaseAsset? FindAsset(ReleaseInfo release, string? rid) =>
        rid is null
            ? null
            : release.Assets.FirstOrDefault(a => a.Name == $"dockhand-{release.Tag}-{rid}.zip" || a.Name == $"dockhand-{release.Tag}-{rid}.tar.gz");

    /// <summary>Reads <c>sha256sum</c> output: "&lt;hash&gt;  &lt;file&gt;" per line.</summary>
    internal static Dictionary<string, string> ParseChecksums(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2)
            {
                result[parts[1].TrimStart('*')] = parts[0];
            }
        }

        return result;
    }

    // ---- download and install -------------------------------------------------------------------

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new DockhandException(ExitCodes.Network, $"Could not reach GitHub: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DockhandException(ExitCodes.Network, "GitHub did not answer in time.", ex);
        }
    }

    private async Task DownloadAsync(Uri url, string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureDownloaded(response, url);
        await using var file = File.Create(path);
        await response.Content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> DownloadStringAsync(Uri url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureDownloaded(response, url);
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureDownloaded(HttpResponseMessage response, Uri url)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new DockhandException(ExitCodes.Network, $"Downloading {url} failed: {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
    }

    private static async Task ExtractExecutableAsync(string archive, string destination, CancellationToken cancellationToken)
    {
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(archive);
            var entry = zip.Entries.FirstOrDefault(e => e.Name == InstallLocation.ExecutableName)
                ?? throw new DockhandException(ExitCodes.Error, $"The download does not contain {InstallLocation.ExecutableName}.");
            entry.ExtractToFile(destination, overwrite: true);
            return;
        }

        await using var file = File.OpenRead(archive);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        await using var tar = new TarReader(gzip);
        while (await tar.GetNextEntryAsync(cancellationToken: cancellationToken).ConfigureAwait(false) is { } entry)
        {
            if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile && Path.GetFileName(entry.Name) == InstallLocation.ExecutableName)
            {
                await entry.ExtractToFileAsync(destination, overwrite: true, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        throw new DockhandException(ExitCodes.Error, $"The download does not contain {InstallLocation.ExecutableName}.");
    }

    /// <summary>Runs the new executable once, so a broken download never replaces a working one.</summary>
    private async Task VerifyAsync(string executable, Version expected, CancellationToken cancellationToken)
    {
        ProcessResult result;
        try
        {
            result = await _processes.RunAsync(executable, ["--version"], null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or TimeoutException or InvalidOperationException)
        {
            throw new DockhandException(ExitCodes.Error, $"The downloaded executable does not run: {ex.Message}", ex);
        }

        var reported = ParseVersion(result.StandardOutput.Trim());
        if (result.ExitCode != 0 || reported != expected)
        {
            throw new DockhandException(ExitCodes.Error, $"The downloaded executable reports version '{result.StandardOutput.Trim()}' instead of {expected}; nothing was changed.");
        }
    }

    /// <summary>
    /// Swaps in the new executable. Unix can rename over a running file. Windows cannot replace or delete it,
    /// but can rename it, so the old one is moved aside and removed by <see cref="InstallLocation.DeleteLeftovers"/>.
    /// </summary>
    private static void Replace(string target, string staged)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                File.Move(staged, target, overwrite: true);
                return;
            }

            var aside = $"{target}.{Guid.NewGuid():N}.old";
            File.Move(target, aside);
            try
            {
                File.Move(staged, target);
            }
            catch
            {
                File.Move(aside, target);
                throw;
            }

            TryDelete(aside);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw NoWriteAccess(target, ex);
        }
    }

    private static async Task WriteStagedAsync(Func<Task> write, InstallLocation location)
    {
        try
        {
            await write().ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw NoWriteAccess(location.ExecutablePath, ex);
        }
    }

    private static DockhandException NoWriteAccess(string target, Exception ex) =>
        new(ExitCodes.Error,
            OperatingSystem.IsWindows()
                ? $"Cannot replace {target}: {ex.Message} Run the update from an elevated terminal, or install dockhand in a folder you own."
                : $"Cannot replace {target}: {ex.Message} Run 'sudo dockhand self update', or install dockhand in a folder you own.",
            ex);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for InstallLocation.DeleteLeftovers.
        }
    }
}
