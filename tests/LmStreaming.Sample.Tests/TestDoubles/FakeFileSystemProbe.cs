using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.TestDoubles;

internal sealed class FakeFileSystemProbe : IFileSystemProbe
{
    private readonly HashSet<string> _executablesOnPath;
    private readonly HashSet<string> _existingFiles;

    public FakeFileSystemProbe(
        IEnumerable<string>? executablesOnPath = null,
        IEnumerable<string>? existingFiles = null)
    {
        _executablesOnPath = new HashSet<string>(
            executablesOnPath ?? [],
            StringComparer.OrdinalIgnoreCase);
        _existingFiles = new HashSet<string>(
            existingFiles ?? [],
            StringComparer.OrdinalIgnoreCase);
    }

    public bool IsExecutableOnPath(string executableBaseName)
    {
        return _executablesOnPath.Contains(executableBaseName);
    }

    public bool FileExists(string path)
    {
        return !string.IsNullOrWhiteSpace(path) && _existingFiles.Contains(path);
    }
}
