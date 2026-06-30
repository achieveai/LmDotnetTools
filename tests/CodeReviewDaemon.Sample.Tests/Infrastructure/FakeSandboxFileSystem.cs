using CodeReviewDaemon.Sample.Workspace.Sandbox;

namespace CodeReviewDaemon.Sample.Tests.Infrastructure;

/// <summary>
/// In-memory <see cref="ISandboxFileSystem"/>. Seed <see cref="Files"/> to script reads (e.g. a
/// <c>.gitmodules</c> at a path); writes update <see cref="Files"/> and are also recorded in
/// <see cref="Writes"/> in order so artifact-writing sequences can be asserted.
/// </summary>
internal sealed class FakeSandboxFileSystem : ISandboxFileSystem
{
    /// <summary>Current file contents keyed by absolute sandbox path.</summary>
    public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

    /// <summary>Paths written, in write order (duplicates kept).</summary>
    public List<string> Writes { get; } = [];

    public Task<string?> ReadFileAsync(string path, CancellationToken cancellationToken) =>
        Task.FromResult(Files.TryGetValue(path, out var content) ? content : null);

    public Task WriteFileAsync(string path, string content, CancellationToken cancellationToken)
    {
        Files[path] = content;
        Writes.Add(path);
        return Task.CompletedTask;
    }
}
