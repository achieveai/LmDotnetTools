namespace CodeReviewDaemon.Sample.Workspace;

/// <summary>
/// One host-side review-checkout slot handed out by <see cref="ReviewSlotPool"/>: a persistent store
/// clone at <see cref="StorePath"/> plus a scratch working tree at <see cref="ScratchPath"/>, both
/// under <see cref="HostPath"/>. Later tasks (prepare/review/commit) operate on the paths; this record
/// is purely the addressing contract.
/// </summary>
/// <param name="Index">The slot's stable 0-based identity within the pool (recycled on return).</param>
/// <param name="HostPath">`&lt;hostRoot&gt;/slot-{Index}` — the slot's root directory.</param>
/// <param name="StorePath">`&lt;HostPath&gt;/store` — the warm store clone, cloned once and reused
/// across leases.</param>
/// <param name="ScratchPath">`&lt;HostPath&gt;/&lt;scratchDirName&gt;` — the per-review working tree.
/// Wiping it between leases is a later task's responsibility.</param>
internal sealed record ReviewSlot(int Index, string HostPath, string StorePath, string ScratchPath);

/// <summary>
/// The narrow lease/return seam <see cref="ReviewSlotPool"/> exposes to the executor, mirroring the
/// <c>IReviewSessionProvisioner</c>/<c>ISandboxSessionSource</c> seams so the pooled-review wiring stays
/// verifiable against a fake without standing up the real host-side pool.
/// </summary>
internal interface IReviewSlotPool
{
    Task<ReviewSlot> LeaseAsync(CancellationToken cancellationToken);

    Task ReturnAsync(ReviewSlot slot, CancellationToken cancellationToken);

    /// <summary>
    /// Retires a leased slot WITHOUT returning its index to the free list, then releases its permit. Used
    /// when the slot's sandbox session could not be confirmed torn down: the store may still be mounted, so
    /// reusing this index (and its store) would race the surviving session against the next lease's
    /// clean-on-entry. Retiring the index makes the next lease allocate a FRESH slot directory + clone, so
    /// capacity is preserved (the permit is released) while the tainted store is never handed out again.
    /// </summary>
    Task QuarantineAsync(ReviewSlot slot, CancellationToken cancellationToken);

    /// <summary>
    /// Discards a leased slot's store and re-clones it from scratch — the recovery escalation when the warm
    /// store is corrupt (a stale lock that survived cleaning, a broken object, a half-inited submodule). The
    /// caller keeps the same leased slot; only its store contents are replaced.
    /// </summary>
    Task RecloneStoreAsync(ReviewSlot slot, CancellationToken cancellationToken);
}

/// <summary>
/// A warm host-side pool of <see cref="ReviewSlot"/>s (design task 5): the daemon leases a slot, uses
/// its store clone for a review, then returns it — the store is cloned once per slot and never
/// re-cloned, so repeated reviews avoid the cost of a fresh clone.
/// </summary>
/// <remarks>
/// A <see cref="SemaphoreSlim"/> gate bounds concurrent leases to <c>maxSlots</c>: <see cref="LeaseAsync"/>
/// awaits a permit before handing out a slot, and <see cref="ReturnAsync"/> releases it, so a lease
/// beyond the configured capacity blocks until a slot is returned. A lock-guarded free list of
/// previously-returned slot indices is checked first on lease; only when it is empty does the pool
/// allocate a new index (capped by the invariant that at most <c>maxSlots</c> indices are ever
/// outstanding at once). A slot's store directory is populated lazily — only the first lease of a given
/// index invokes the clone callback; a returned-and-re-leased slot already has its store on disk and
/// skips it entirely.
/// </remarks>
internal sealed class ReviewSlotPool : IReviewSlotPool
{
    /// <summary>Tombstone file dropped in a quarantined slot's dir so the quarantine survives a daemon
    /// restart: on restart <c>_nextIndex</c> resets to 0 and the pool would otherwise treat the old
    /// non-empty <c>slot-0/store</c> as a warm clone and reuse the tainted store. The ctor reaps these and
    /// <see cref="LeaseAsync"/> re-clones over any that survive.</summary>
    private const string QuarantineMarkerName = ".quarantined";

    private readonly string _hostRoot;
    private readonly string _scratchDirName;
    private readonly Func<ReviewSlot, CancellationToken, Task> _ensureStoreClonedAsync;
    private readonly ILogger<ReviewSlotPool> _logger;
    private readonly SemaphoreSlim _gate;
    private readonly Lock _freeIndexesLock = new();
    private readonly Stack<int> _freeIndexes = new();
    private int _nextIndex;

    public ReviewSlotPool(
        int maxSlots,
        string? hostRoot,
        string scratchDirName,
        Func<ReviewSlot, CancellationToken, Task> ensureStoreClonedAsync,
        ILogger<ReviewSlotPool> logger)
    {
        if (maxSlots < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSlots), maxSlots, "At least one slot is required.");
        }

        ArgumentNullException.ThrowIfNull(ensureStoreClonedAsync);
        ArgumentNullException.ThrowIfNull(logger);

        _hostRoot = hostRoot ?? Path.Combine(AppContext.BaseDirectory, "review-pool");
        _scratchDirName = scratchDirName;
        _ensureStoreClonedAsync = ensureStoreClonedAsync;
        _logger = logger;
        _gate = new SemaphoreSlim(maxSlots, maxSlots);

        // Startup reconcile (durable-quarantine): reap any slot dirs a PRIOR process tombstoned. This is the
        // one moment it is unconditionally safe to delete them — a fresh process holds no sandbox mounts — so
        // it both bounds cross-restart quarantine disk AND guarantees a restarted daemon never reuses a
        // quarantined store as warm (the dir is gone → the next lease re-clones).
        ReapStaleQuarantinedSlots();
    }

    /// <summary>
    /// Awaits a free permit, then hands out a slot — a recycled one from the free list if any is
    /// available, otherwise the next unallocated index. Clones the slot's store on first use only.
    /// </summary>
    public async Task<ReviewSlot> LeaseAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        var index = TakeIndex();
        var slot = BuildSlot(index);
        try
        {
            Directory.CreateDirectory(slot.HostPath);
            Directory.CreateDirectory(slot.ScratchPath);

            // A surviving tombstone (the ctor reap could not remove it) means this dir was quarantined by a
            // prior process: its store may be the tainted one and must NEVER be reused as warm. Wipe it so the
            // clone below rebuilds from scratch, then drop the marker. (In-process, a quarantined index is
            // retired and never re-leased, so this only fires across a restart, where no mount survives.)
            var quarantined = File.Exists(QuarantineMarkerPath(slot));
            if (quarantined)
            {
                _logger.LogWarning(
                    "Discarding quarantined store for review slot {Index} at {StorePath} before reuse.",
                    slot.Index, slot.StorePath);
                TryResetStore(slot.StorePath);
                TryDelete(QuarantineMarkerPath(slot));
            }

            if (quarantined || !Directory.Exists(slot.StorePath) || IsDirectoryEmpty(slot.StorePath))
            {
                _logger.LogInformation("Cloning store for review slot {Index} at {StorePath}", slot.Index, slot.StorePath);
                await _ensureStoreClonedAsync(slot, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _logger.LogDebug("Reusing warm store for review slot {Index} at {StorePath}", slot.Index, slot.StorePath);
            }

            return slot;
        }
        catch
        {
            // Setup failed after acquiring the permit + index (directory IO, or the clone callback threw
            // or was cancelled). A partially-populated store must NOT be left behind: the next lease of this
            // recycled index would see a non-empty StorePath, treat it as a warm clone, and skip re-cloning —
            // yielding a corrupt, incomplete checkout. Wipe it (best-effort) so a later lease re-clones from
            // scratch. Then recycle BOTH the index and the permit so a transient clone/IO failure cannot
            // permanently consume pool capacity — otherwise repeated failures exhaust the pool until a daemon
            // restart.
            TryResetStore(slot.StorePath);

            lock (_freeIndexesLock)
            {
                _freeIndexes.Push(index);
            }

            _gate.Release();
            throw;
        }
    }

    /// <summary>
    /// Returns a slot's index to the free list and releases its permit, making it re-leasable. Scratch
    /// wiping is a later task's responsibility — this only makes the index available again.
    /// </summary>
    public Task ReturnAsync(ReviewSlot slot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(slot);

        lock (_freeIndexesLock)
        {
            _freeIndexes.Push(slot.Index);
        }

        _gate.Release();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Retires a leased slot on an unconfirmed session teardown: unlike <see cref="ReturnAsync"/> it does NOT
    /// push the index onto the free list, so no future lease reuses this slot's directory or its
    /// possibly-still-mounted store (which would let the next lease's clean-on-entry race the surviving
    /// session). The permit IS released, so capacity is preserved — the next lease finds the free list empty
    /// and allocates a fresh index (a new <c>slot-{N}</c> dir + clone). A <see cref="QuarantineMarkerName"/>
    /// tombstone is dropped in the slot dir so the quarantine survives a restart (the ctor reap +
    /// <see cref="LeaseAsync"/> guard discard the tainted store rather than reusing it as warm), bounding the
    /// leaked disk to a restart rather than leaving it indefinitely.
    /// </summary>
    public Task QuarantineAsync(ReviewSlot slot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(slot);

        // Persist the tombstone BEFORE releasing the permit, so a crash between here and the next lease still
        // leaves the durable marker. Best-effort: if the write fails, in-process index retirement still keeps
        // this slot out of reuse for the current process (the marker only matters across a restart).
        TryWriteQuarantineMarker(slot);

        _logger.LogWarning(
            "Quarantining review slot {Index} at {StorePath}: index retired (not reused) because its session "
                + "teardown could not be confirmed; the next lease allocates a fresh slot and the tombstone "
                + "forces a re-clone should a restart land on this dir.",
            slot.Index,
            slot.StorePath);
        _gate.Release();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Discards the slot's store and re-clones it — the recovery escalation when the warm store is corrupt.
    /// The lease is retained; only the store contents are replaced. Mirrors the first-lease clone path.
    /// </summary>
    public async Task RecloneStoreAsync(ReviewSlot slot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(slot);

        _logger.LogWarning(
            "Re-cloning corrupt store for review slot {Index} at {StorePath}", slot.Index, slot.StorePath);
        TryResetStore(slot.StorePath);
        Directory.CreateDirectory(slot.HostPath);
        await _ensureStoreClonedAsync(slot, cancellationToken).ConfigureAwait(false);
    }

    private int TakeIndex()
    {
        lock (_freeIndexesLock)
        {
            return _freeIndexes.Count > 0 ? _freeIndexes.Pop() : _nextIndex++;
        }
    }

    private ReviewSlot BuildSlot(int index)
    {
        var hostPath = Path.Combine(_hostRoot, $"slot-{index}");
        return new ReviewSlot(index, hostPath, Path.Combine(hostPath, "store"), Path.Combine(hostPath, _scratchDirName));
    }

    private static string QuarantineMarkerPath(ReviewSlot slot) =>
        Path.Combine(slot.HostPath, QuarantineMarkerName);

    /// <summary>Best-effort tombstone drop so a quarantine survives a restart. Failure is tolerated: the
    /// in-process index retirement already keeps the slot out of reuse for the current process.</summary>
    private void TryWriteQuarantineMarker(ReviewSlot slot)
    {
        try
        {
            Directory.CreateDirectory(slot.HostPath);
            File.WriteAllText(QuarantineMarkerPath(slot), string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Best-effort quarantine tombstone write failed for review slot {Index} at {HostPath}.",
                slot.Index, slot.HostPath);
        }
    }

    /// <summary>Startup reconcile: reaps every <c>slot-*</c> dir a prior process tombstoned. Safe only here —
    /// a fresh process holds no sandbox mounts — so it both bounds cross-restart quarantine disk and prevents a
    /// restarted daemon from reusing a quarantined store as warm.</summary>
    private void ReapStaleQuarantinedSlots()
    {
        try
        {
            if (!Directory.Exists(_hostRoot))
            {
                return;
            }

            foreach (var dir in Directory.EnumerateDirectories(_hostRoot, "slot-*"))
            {
                if (!File.Exists(Path.Combine(dir, QuarantineMarkerName)))
                {
                    continue;
                }

                _logger.LogInformation("Reaping quarantined review slot dir left by a prior process: {Dir}", dir);
                TryResetStore(dir);
            }
        }
        catch (Exception ex)
        {
            // Reap is best-effort — any tombstone that survives is still honoured by the LeaseAsync guard
            // (re-clone over it), so a failed reap never causes a tainted-store reuse, only leaked disk.
            _logger.LogWarning(ex, "Best-effort quarantined-slot reap failed under {HostRoot}.", _hostRoot);
        }
    }

    /// <summary>Best-effort delete of a single file (the quarantine tombstone) — tolerates absence + locks.</summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort; a surviving tombstone is re-honoured on the next lease.
        }
    }

    /// <summary>Best-effort wipe of a slot's store directory after a failed lease, so partial contents are
    /// never mistaken for a warm clone on the next lease. Read-only attributes are cleared first (a git
    /// object store leaves read-only files) — mirrors <c>ReviewSessionProvisioner.ClearReadOnly</c>.</summary>
    private void TryResetStore(string storePath)
    {
        try
        {
            if (!Directory.Exists(storePath))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(storePath, "*", SearchOption.AllDirectories))
            {
                var attributes = File.GetAttributes(file);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
            }

            Directory.Delete(storePath, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Best-effort store reset failed for review slot at {StorePath}.", storePath);
        }
    }

    private static bool IsDirectoryEmpty(string path) => !Directory.EnumerateFileSystemEntries(path).Any();
}
