namespace CodeReviewDaemon.Sample.Workspace.Sandbox;

/// <summary>Host-process <see cref="ISandboxFileSystem"/> for the daemon's retention checkout (design §6).</summary>
internal sealed class HostFileSystem : ISandboxFileSystem
{
    public async Task<string?> ReadFileAsync(string path, CancellationToken cancellationToken)
        => File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false) : null;

    public async Task WriteFileAsync(string path, string content, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<string>> ListFilesAsync(string directory, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory)) return Task.FromResult<IReadOnlyList<string>>([]);
        IReadOnlyList<string> names = [.. Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName)!];
        return Task.FromResult(names);
    }
}
