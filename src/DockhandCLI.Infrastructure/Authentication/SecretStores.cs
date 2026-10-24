using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DockhandCLI.Infrastructure.Configuration;
using DockhandCLI.Infrastructure.Persistence;

namespace DockhandCLI.Infrastructure.Authentication;

/// <summary>A place to keep API tokens, keyed by endpoint alias.</summary>
internal interface ISecretStore
{
    /// <summary>Stable identifier written into credential references.</summary>
    string Name { get; }

    string DisplayName { get; }

    Task<bool> IsAvailableAsync(CancellationToken cancellationToken);

    Task SetAsync(string key, string secret, CancellationToken cancellationToken);

    Task<string?> GetAsync(string key, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(string key, CancellationToken cancellationToken);
}

internal static class SecretKeys
{
    public const string Service = "DockhandCLI";

    public static string Normalize(string alias) => alias.ToLowerInvariant();
}

/// <summary>Windows Credential Manager (generic credentials, per user, not roaming).</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsCredentialStore : ISecretStore
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public string Name => "windows-credential-manager";

    public string DisplayName => "Windows Credential Manager";

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    public Task SetAsync(string key, string secret, CancellationToken cancellationToken)
    {
        var target = Target(key);
        var blob = Encoding.Unicode.GetBytes(secret);
        var targetPtr = Marshal.StringToCoTaskMemUni(target);
        var userPtr = Marshal.StringToCoTaskMemUni(SecretKeys.Normalize(key));
        var blobPtr = Marshal.AllocCoTaskMem(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = targetPtr,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPtr,
                Persist = CredPersistLocalMachine,
                UserName = userPtr,
            };

            if (!NativeMethods.CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CredWrite failed");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(blob);
            Marshal.FreeCoTaskMem(targetPtr);
            Marshal.FreeCoTaskMem(userPtr);
            Marshal.FreeCoTaskMem(blobPtr);
        }

        return Task.CompletedTask;
    }

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken)
    {
        if (!NativeMethods.CredRead(Target(key), CredTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            return error == ErrorNotFound
                ? Task.FromResult<string?>(null)
                : throw new Win32Exception(error, "CredRead failed");
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            var secret = credential.CredentialBlobSize == 0
                ? ""
                : Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
            return Task.FromResult<string?>(secret);
        }
        finally
        {
            NativeMethods.CredFree(pointer);
        }
    }

    public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken)
    {
        if (NativeMethods.CredDelete(Target(key), CredTypeGeneric, 0))
        {
            return Task.FromResult(true);
        }

        var error = Marshal.GetLastWin32Error();
        return error == ErrorNotFound ? Task.FromResult(false) : throw new Win32Exception(error, "CredDelete failed");
    }

    private static string Target(string key) => $"{SecretKeys.Service}:{SecretKeys.Normalize(key)}";

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    private static class NativeMethods
    {
        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
        public static extern bool CredWrite(ref Credential credential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CredDelete(string target, uint type, uint flags);

        [DllImport("advapi32.dll")]
        public static extern void CredFree(IntPtr buffer);
    }
}

/// <summary>macOS login keychain through the <c>security</c> tool. The token is passed on stdin, never as an argument.</summary>
internal sealed class MacKeychainStore(IProcessRunner runner) : ISecretStore
{
    private const string Tool = "/usr/bin/security";

    public string Name => "macos-keychain";

    public string DisplayName => "macOS Keychain";

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) =>
        Task.FromResult(OperatingSystem.IsMacOS() && File.Exists(Tool));

    public async Task SetAsync(string key, string secret, CancellationToken cancellationToken)
    {
        var account = SecretKeys.Normalize(key);
        // Interactive mode reads commands from stdin. Alias and token are validated to contain no spaces or quotes.
        var command = $"add-generic-password -U -s {SecretKeys.Service} -a {account} -w {secret}\n";
        await runner.RunAsync(Tool, ["-i"], command, cancellationToken).ConfigureAwait(false);

        if (await GetAsync(key, cancellationToken).ConfigureAwait(false) != secret)
        {
            throw new InvalidOperationException("The keychain did not accept the token.");
        }
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            Tool, ["find-generic-password", "-s", SecretKeys.Service, "-a", SecretKeys.Normalize(key), "-w"], null, cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 ? result.StandardOutput.TrimEnd('\r', '\n') : null;
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            Tool, ["delete-generic-password", "-s", SecretKeys.Service, "-a", SecretKeys.Normalize(key)], null, cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0;
    }
}

/// <summary>Linux Secret Service (GNOME Keyring, KWallet) through <c>secret-tool</c> from libsecret.</summary>
internal sealed class LinuxSecretToolStore(IProcessRunner runner) : ISecretStore
{
    private const string Tool = "secret-tool";
    private bool? _available;

    public string Name => "linux-secret-service";

    public string DisplayName => "Secret Service (libsecret)";

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        if (_available is { } known)
        {
            return known;
        }

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsFreeBSD())
        {
            return (_available = false).Value;
        }

        try
        {
            // A lookup for a missing item exits 1 with no stderr when the keyring service is reachable.
            var probe = await runner.RunAsync(Tool, ["lookup", "service", SecretKeys.Service, "account", "__probe__"], null, cancellationToken)
                .ConfigureAwait(false);
            _available = probe.ExitCode is 0 or 1 && string.IsNullOrWhiteSpace(probe.StandardError);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or TimeoutException)
        {
            _available = false;
        }

        return _available.Value;
    }

    public async Task SetAsync(string key, string secret, CancellationToken cancellationToken)
    {
        var account = SecretKeys.Normalize(key);
        var result = await runner.RunAsync(
            Tool, ["store", "--label", $"{SecretKeys.Service} ({account})", "service", SecretKeys.Service, "account", account], secret, cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0 || await GetAsync(key, cancellationToken).ConfigureAwait(false) != secret)
        {
            throw new InvalidOperationException($"secret-tool could not store the token: {result.StandardError.Trim()}");
        }
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(Tool, ["lookup", "service", SecretKeys.Service, "account", SecretKeys.Normalize(key)], null, cancellationToken)
            .ConfigureAwait(false);
        return result.ExitCode == 0 ? result.StandardOutput.TrimEnd('\r', '\n') : null;
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(Tool, ["clear", "service", SecretKeys.Service, "account", SecretKeys.Normalize(key)], null, cancellationToken)
            .ConfigureAwait(false);
        return result.ExitCode == 0;
    }
}

/// <summary>
/// Fallback when no OS keyring is usable: a separate owner-only (0600) file next to the configuration.
/// On Windows the values are additionally encrypted with DPAPI for the current user.
/// </summary>
internal sealed class FileSecretStore(ConfigPaths paths) : ISecretStore
{
    private const string DpapiPrefix = "dpapi:";
    private const string PlainPrefix = "plain:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DockhandCLI.secrets.v1");

    public string Name => "file";

    public string DisplayName => $"protected file ({paths.SecretsFile})";

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    public async Task SetAsync(string key, string secret, CancellationToken cancellationToken)
    {
        using var _ = await SecureFile.LockAsync(paths.SecretsFile, cancellationToken).ConfigureAwait(false);
        var secrets = await ReadAsync(cancellationToken).ConfigureAwait(false);
        secrets[SecretKeys.Normalize(key)] = Protect(secret);
        await WriteAsync(secrets, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken)
    {
        var secrets = await ReadAsync(cancellationToken).ConfigureAwait(false);
        return secrets.TryGetValue(SecretKeys.Normalize(key), out var value) ? Unprotect(value) : null;
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.SecretsFile))
        {
            return false;
        }

        using var _ = await SecureFile.LockAsync(paths.SecretsFile, cancellationToken).ConfigureAwait(false);
        var secrets = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!secrets.Remove(SecretKeys.Normalize(key)))
        {
            return false;
        }

        await WriteAsync(secrets, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<Dictionary<string, string>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.SecretsFile))
        {
            return new(StringComparer.Ordinal);
        }

        await using var stream = File.OpenRead(paths.SecretsFile);
        var document = await JsonSerializer.DeserializeAsync<SecretsDocument>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new(document?.Tokens ?? [], StringComparer.Ordinal);
    }

    private Task WriteAsync(Dictionary<string, string> secrets, CancellationToken cancellationToken) =>
        SecureFile.WriteAllTextAsync(
            paths.SecretsFile,
            JsonSerializer.Serialize(new SecretsDocument { Tokens = secrets }, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);

    private static string Protect(string secret)
    {
        if (!OperatingSystem.IsWindows())
        {
            return PlainPrefix + secret;
        }

        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser);
        return DpapiPrefix + Convert.ToBase64String(bytes);
    }

    private static string Unprotect(string value)
    {
        if (value.StartsWith(PlainPrefix, StringComparison.Ordinal))
        {
            return value[PlainPrefix.Length..];
        }

        if (value.StartsWith(DpapiPrefix, StringComparison.Ordinal) && OperatingSystem.IsWindows())
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value[DpapiPrefix.Length..]), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }

        throw new InvalidOperationException("The stored token cannot be decrypted on this system.");
    }

    private sealed class SecretsDocument
    {
        public Dictionary<string, string> Tokens { get; set; } = [];
    }
}
