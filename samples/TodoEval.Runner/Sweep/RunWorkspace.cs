namespace TodoEval.Runner.Sweep;

/// <summary>
/// The working directory one run is given: a fresh copy of its task's <c>fixtures/</c> tree under the
/// sweep's workspaces root, which the host mounts as that conversation's sandbox workspace.
/// </summary>
/// <remarks>
/// The directory NAME is load-bearing. The host stores a workspace as a single sanitized leaf under
/// its gateway's <c>WorkspaceBasePath</c> (<c>FileWorkspaceStore.SanitizeDirectory</c> lowercases and
/// strips path separators), so a run key with its slashes intact would land somewhere neither side
/// could predict. <see cref="LeafFor"/> therefore folds the key into one segment the host's sanitizer
/// leaves untouched, which makes "the directory the runner filled" and "the directory the agent
/// mounted" the same directory by construction.
/// <para>
/// Nothing is ever deleted here. A finished run's workspace IS the evidence the J1 checker judges,
/// and a failed run's workspace is the only way to see what the agent actually did.
/// </para>
/// </remarks>
internal static class RunWorkspace
{
    /// <summary>
    /// The single-segment directory leaf for a run key: separators folded to <c>-</c>, lowercased, and
    /// anything the host's sanitizer would strip removed HERE, so this leaf survives it unchanged.
    /// </summary>
    public static string LeafFor(string runKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runKey);

        var folded = runKey.Replace('/', '-').Replace('\\', '-').Trim().ToLowerInvariant();
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
        var sanitized = new string([.. folded.Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '-' : c)])
            .Replace("..", string.Empty)
            .Trim('-');

        return sanitized.Length > 0
            ? sanitized
            : throw new InvalidOperationException(
                $"Run key '{runKey}' has no characters that can name a workspace directory."
            );
    }

    /// <summary>
    /// Creates <c>{workspacesRoot}/{leaf}</c> and copies the task's fixtures into it, returning the
    /// absolute path. Only <c>fixtures/</c> is copied, which is what keeps the task's <c>hidden/</c>
    /// answer keys and checker tests out of the tree the agent can read.
    /// </summary>
    /// <exception cref="InvalidOperationException">The directory already holds a previous run's files.</exception>
    public static string Prepare(string workspacesRoot, string runKey, string fixturesDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacesRoot);

        var path = Path.GetFullPath(Path.Combine(workspacesRoot, LeafFor(runKey)));

        // A non-empty directory means this run key already ran and its evidence is still there.
        // Overwriting it would silently judge a mixture of two runs, so it is a harness error.
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
        {
            throw new InvalidOperationException(
                $"Workspace '{path}' for run '{runKey}' already exists and is not empty. A sweep never reuses "
                    + "a workspace: move or delete the previous run's directory before re-running this cell."
            );
        }

        CopyTree(fixturesDir, path);
        return path;
    }

    private static void CopyTree(string sourceDir, string destinationDir)
    {
        _ = Directory.CreateDirectory(destinationDir);
        foreach (var directory in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            _ = Directory.CreateDirectory(Path.Combine(destinationDir, Path.GetRelativePath(sourceDir, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destinationDir, Path.GetRelativePath(sourceDir, file)), overwrite: true);
        }
    }
}
