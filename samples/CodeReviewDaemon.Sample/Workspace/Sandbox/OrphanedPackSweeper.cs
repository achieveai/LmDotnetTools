namespace CodeReviewDaemon.Sample.Workspace.Sandbox;

/// <summary>
/// Removes the temporary pack files git abandons when a fetch is killed mid-write.
/// <para>
/// This exists because of a real outage. <see cref="HostGitCommandRunner"/> kills the whole process tree
/// when a command exceeds its idle timeout or duration ceiling, and a <c>git fetch</c> killed while
/// <c>index-pack</c> is writing leaves its <c>tmp_pack_*</c> behind. Nothing reclaims it inside any
/// window that matters: <c>git gc</c> only prunes stale temp files past <c>gc.pruneExpire</c>, which
/// defaults to TWO WEEKS, and <see cref="ReviewSlotPreparer"/> configures <c>gc.autoPackLimit</c>,
/// <c>gc.autoDetach</c> and <c>gc.cruftPacks</c> but no prune expiry. On a repository whose packs run to
/// 5.45 GB each, every timeout-kill leaks multiple gigabytes permanently. Measured: 35 orphans totalling
/// 245.35 GB in one submodule's pack directory, which grew the WSL disk image until it filled the host
/// volume and the filesystem remounted read-only. A single surviving orphan was 2.98 GB on its own.
/// </para>
/// <para>
/// The identification is by CONSTRUCTION rather than by heuristic, because the obvious heuristic does not
/// work. An age threshold — "delete temp packs untouched for longer than the idle timeout" — deletes
/// NOTHING on this path: the file we just killed was being written microseconds ago, so it is always the
/// youngest thing in the directory. Instead the caller snapshots the temp packs present BEFORE starting a
/// pack-writing command, and only files that appeared while that command ran are removed.
/// </para>
/// <para>
/// That leaves exactly one way to delete a file some other fetch is still writing: a second fetch into the
/// SAME object store, started after our snapshot. It cannot happen — the caller holds the per-store lock
/// for the duration, which is the same invariant <c>gc.autoDetach=false</c> relies on. The scope is
/// narrowed twice more for cheapness rather than safety: only pack-writing verbs snapshot at all, and only
/// the object stores reachable from the command's own working directory are ever examined.
/// </para>
/// </summary>
internal static class OrphanedPackSweeper
{
    /// <summary>
    /// The temp files <c>index-pack</c> writes. Both are abandoned by the same kill, and an orphaned
    /// <c>tmp_idx_*</c> is small but has no more right to survive than the pack it indexes.
    /// </summary>
    private static readonly string[] S_tempPrefixes = ["tmp_pack_", "tmp_idx_"];

    /// <summary>
    /// Directories never descended into while looking for nested object stores. <c>objects</c> is the one
    /// that matters: on the live NOVA store it holds 970,000 loose objects across the 256-way fan-out, and
    /// a naive recursive enumeration would read every one of them on a path that runs during a failure.
    /// The others are skipped for the same reason at smaller scale.
    /// </summary>
    private static readonly string[] S_skipDirectories = ["objects", "refs", "logs", "hooks", "info"];

    /// <summary>Bounds the walk for nested submodule object stores. Submodule git directories nest one
    /// level per path segment (<c>modules/repos/Nova/modules/…</c>), so a handful of levels covers every
    /// real layout while a cycle through a symlink cannot run away.</summary>
    private const int MaxModuleDepth = 12;

    /// <summary>
    /// Records the temp packs already present, so a later sweep can tell OUR abandoned write from one that
    /// was already lying there. Returns an empty set for anything it cannot read: a snapshot that failed is
    /// indistinguishable from a store with no temp packs, and treating it as "everything is pre-existing"
    /// is the safe direction — it sweeps nothing rather than sweeping a stranger's file.
    /// </summary>
    public static IReadOnlySet<string> Snapshot(string? workingDirectory)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var packDirectory in PackDirectories(workingDirectory))
            {
                foreach (var file in TempPackFiles(packDirectory))
                {
                    _ = seen.Add(file);
                }
            }
        }
        catch (Exception)
        {
            // Best-effort by design. A snapshot that throws must not fail the fetch it was taken for, and
            // returning what was collected so far only ever makes the later sweep more conservative.
        }

        return seen;
    }

    /// <summary>
    /// Deletes temp packs that appeared since <paramref name="preexisting"/> was taken. Returns what it
    /// reclaimed so the caller can say so out loud — a silent sweep is indistinguishable from a sweep that
    /// never ran, and the whole defect being fixed here was invisible for exactly that reason.
    /// </summary>
    public static (int Files, long Bytes) SweepNew(
        string? workingDirectory,
        IReadOnlySet<string> preexisting,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(preexisting);
        ArgumentNullException.ThrowIfNull(logger);

        var files = 0;
        var bytes = 0L;

        try
        {
            foreach (var packDirectory in PackDirectories(workingDirectory))
            {
                foreach (var file in TempPackFiles(packDirectory))
                {
                    if (preexisting.Contains(file))
                    {
                        continue;
                    }

                    if (TryDelete(file, logger, out var size))
                    {
                        files++;
                        bytes += size;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Never fail a run over housekeeping: the command that triggered this has already failed, and
            // turning a recoverable timeout into an unhandled exception would be a worse defect than the
            // leak. Logged rather than swallowed silently so a sweep that never works is discoverable.
            logger.LogWarning(
                ex,
                "Sweeping abandoned git temp packs under '{WorkingDirectory}' failed; "
                    + "any orphaned tmp_pack_* files there are still on disk.",
                workingDirectory);
        }

        return (files, bytes);
    }

    /// <summary>
    /// Deletes one temp pack. The size is read BEFORE the delete because it is unreadable afterwards, and a
    /// reclaim figure is the only evidence this ran at all.
    /// </summary>
    private static bool TryDelete(string path, ILogger logger, out long size)
    {
        size = 0;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return false;
            }

            size = info.Length;

            // git creates these 0444. On Linux the unlink permission comes from the DIRECTORY, so the
            // read-only bit does not block deletion there — but it does on Windows, and clearing it costs
            // one syscall against a failure that would otherwise be silent and platform-specific.
            if (info.IsReadOnly)
            {
                info.IsReadOnly = false;
            }

            info.Delete();
            return true;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not delete abandoned git temp pack '{Path}'.", path);
            size = 0;
            return false;
        }
    }

    /// <summary>Temp pack files directly inside one pack directory. Not recursive: git writes them beside
    /// the packs they will become, never below.</summary>
    private static IEnumerable<string> TempPackFiles(string packDirectory)
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFiles(packDirectory);
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            foreach (var prefix in S_tempPrefixes)
            {
                if (name.StartsWith(prefix, StringComparison.Ordinal))
                {
                    yield return entry;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Every <c>objects/pack</c> directory reachable from the command's working directory — the repository's
    /// own, plus one per submodule under <c>.git/modules</c>. The leak was found in a submodule store, not
    /// the top-level one, so walking the modules tree is the point of this rather than a generalisation.
    /// </summary>
    private static IEnumerable<string> PackDirectories(string? workingDirectory)
    {
        var gitDirectory = ResolveGitDirectory(workingDirectory);
        if (gitDirectory is null)
        {
            yield break;
        }

        foreach (var candidate in ObjectStoreRoots(gitDirectory))
        {
            var pack = Path.Combine(candidate, "objects", "pack");
            if (Directory.Exists(pack))
            {
                yield return pack;
            }
        }
    }

    /// <summary>The git directory itself, then every submodule git directory beneath its <c>modules</c>
    /// tree. Depth-bounded and pruned at <see cref="S_skipDirectories"/> so the 256-way loose-object fan-out
    /// is never entered.</summary>
    private static IEnumerable<string> ObjectStoreRoots(string gitDirectory)
    {
        yield return gitDirectory;

        var modules = Path.Combine(gitDirectory, "modules");
        if (!Directory.Exists(modules))
        {
            yield break;
        }

        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((modules, 0));

        while (pending.Count > 0)
        {
            var (current, depth) = pending.Pop();
            if (depth > MaxModuleDepth)
            {
                continue;
            }

            string[] children;
            try
            {
                children = Directory.GetDirectories(current);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (S_skipDirectories.Contains(name, StringComparer.Ordinal))
                {
                    continue;
                }

                // A directory holding `objects` IS a git directory; it may still contain a nested
                // `modules` of its own, so it is yielded AND descended.
                if (Directory.Exists(Path.Combine(child, "objects")))
                {
                    yield return child;
                }

                pending.Push((child, depth + 1));
            }
        }
    }

    /// <summary>
    /// Resolves the git directory for a working directory without shelling out to git — the process we
    /// would ask has just been killed, and a sweep that needs a working git to find the mess a broken git
    /// left is no use on the one path it exists for.
    /// </summary>
    private static string? ResolveGitDirectory(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return null;
        }

        var dotGit = Path.Combine(workingDirectory, ".git");

        if (Directory.Exists(dotGit))
        {
            return dotGit;
        }

        // A submodule or linked worktree has `.git` as a FILE holding `gitdir: <path>`, which is precisely
        // the shape the leaking store had.
        if (File.Exists(dotGit))
        {
            try
            {
                foreach (var line in File.ReadAllLines(dotGit))
                {
                    const string Marker = "gitdir:";
                    if (!line.StartsWith(Marker, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var target = line[Marker.Length..].Trim();
                    if (target.Length == 0)
                    {
                        continue;
                    }

                    var resolved = Path.IsPathRooted(target)
                        ? target
                        : Path.GetFullPath(Path.Combine(workingDirectory, target));

                    return Directory.Exists(resolved) ? resolved : null;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        // A bare repository, or the git directory handed in directly.
        return Directory.Exists(Path.Combine(workingDirectory, "objects")) ? workingDirectory : null;
    }
}
