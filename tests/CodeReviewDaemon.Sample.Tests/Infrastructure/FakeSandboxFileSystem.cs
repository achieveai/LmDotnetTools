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

    /// <summary>
    /// Optional fault injector for reads: when it returns a non-null exception for a path, that read
    /// throws it (models the boot-lifetime sandbox session 404-ing on a KB read). Default: no faults.
    /// </summary>
    public Func<string, Exception?>? ReadFault { get; set; }

    /// <summary>Optional fault injector for directory listings — same contract as <see cref="ReadFault"/>.</summary>
    public Func<string, Exception?>? ListFault { get; set; }

    /// <summary>Seeds a file's contents without recording a write (test setup convenience).</summary>
    public FakeSandboxFileSystem Seed(string path, string content)
    {
        Files[path] = content;
        return this;
    }

    public Task<string?> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        if (ReadFault?.Invoke(path) is { } fault)
        {
            throw fault;
        }

        return Task.FromResult(Files.TryGetValue(path, out var content) ? content : null);
    }

    public Task WriteFileAsync(string path, string content, CancellationToken cancellationToken)
    {
        Files[path] = content;
        Writes.Add(path);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListFilesAsync(string directory, CancellationToken cancellationToken)
    {
        if (ListFault?.Invoke(directory) is { } fault)
        {
            throw fault;
        }

        var prefix = directory.TrimEnd('/') + "/";
        // Mirror `ls -1A` / the production file systems: return the immediate child ENTRY names — a file
        // directly under the directory, or the first path segment of a deeper key (a subdirectory name),
        // deduplicated and sorted. The layered Knowledge Base regen relies on discovering scope
        // subdirectories (system/, <repo>/) this way, exactly as it would against the real gateway.
        IReadOnlyList<string> names =
        [
            .. Files.Keys
                .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(key => key[prefix.Length..])
                .Select(rest =>
                {
                    var slash = rest.IndexOf('/', StringComparison.Ordinal);
                    return slash < 0 ? rest : rest[..slash];
                })
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal),
        ];
        return Task.FromResult(names);
    }
}
