namespace LmStreaming.Sample.Services;

/// <summary>
/// Seam over the file system used by <see cref="ProviderRegistry"/> to detect CLI
/// presence on PATH. Tests provide a fake to avoid touching the real environment.
/// </summary>
public interface IFileSystemProbe
{
    /// <summary>
    /// Returns true when an executable named <paramref name="executableBaseName"/> exists
    /// on the user's PATH. Probes platform-appropriate suffixes (.exe / .cmd on Windows).
    /// </summary>
    bool IsExecutableOnPath(string executableBaseName);

    /// <summary>
    /// Returns true when the file at <paramref name="path"/> exists on disk.
    /// </summary>
    bool FileExists(string path);
}

/// <summary>
/// Default <see cref="IFileSystemProbe"/> implementation that walks PATH and checks the
/// real file system. Probes are cached at <see cref="ProviderRegistry"/> construction so
/// no I/O happens per request.
/// </summary>
public sealed class FileSystemProbe : IFileSystemProbe
{
    public bool IsExecutableOnPath(string executableBaseName)
    {
        ArgumentException.ThrowIfNullOrEmpty(executableBaseName);

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var isWindows = OperatingSystem.IsWindows();
        var suffixes = isWindows
            ? new[] { string.Empty, ".exe", ".cmd", ".bat", ".ps1" }
            : new[] { string.Empty };

        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            foreach (var suffix in suffixes)
            {
                var candidate = Path.Combine(dir, executableBaseName + suffix);
                if (File.Exists(candidate))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public bool FileExists(string path)
    {
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
    }
}
