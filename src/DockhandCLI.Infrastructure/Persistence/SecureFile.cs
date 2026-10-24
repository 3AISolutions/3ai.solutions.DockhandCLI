using System.Text;

namespace DockhandCLI.Infrastructure.Persistence;

/// <summary>Private (owner-only) files written atomically, so a crash never leaves a half-written file.</summary>
internal static class SecureFile
{
    private const UnixFileMode OwnerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerDirectory = OwnerReadWrite | UnixFileMode.UserExecute;

    public static void EnsureDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
        }
        else
        {
            Directory.CreateDirectory(directory, OwnerDirectory);
        }
    }

    /// <summary>Writes to a temporary file in the same directory, flushes it to disk, then renames it over the target.</summary>
    public static async Task WriteAllTextAsync(string path, string content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)!;
        EnsureDirectory(directory);

        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = OwnerReadWrite;
        }

        try
        {
            await using (var stream = new FileStream(temp, options))
            {
                var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Takes an exclusive, cross-process lock next to <paramref name="path"/>.</summary>
    public static async Task<IDisposable> LockAsync(string path, CancellationToken cancellationToken)
    {
        EnsureDirectory(Path.GetDirectoryName(path)!);
        var lockPath = path + ".lock";
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
