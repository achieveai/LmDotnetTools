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
    /// restart. On restart <c>_nextIndex</c> resets to 0 and the pool would otherwise treat the old non-empty
    /// <c>slot-0/store</c> as a warm clone and reuse the tainted store. The ctor scans for these and RETIRES
    /// those indexes (it does NOT delete the dirs — this daemon adopts a persistent external gateway, so a
    /// session can outlive the restart and still be mounted; deleting would race live sandbox work). A retired
    /// index is never handed out again, so the next lease allocates a different slot.</summary>
    private const string QuarantineMarkerName = ".quarantined";

    private readonly string _hostRoot;
    private readonly string _scratchDirName;
    private readonly Func<ReviewSlot, CancellationToken, Task> _ensureStoreClonedAsync;
    private readonly ILogger<ReviewSlotPool> _logger;
    private readonly SemaphoreSlim _gate;
    private readonly Lock _freeIndexesLock = new();
    private readonly Stack<int> _freeIndexes = new();

    /// <summary>Slot indexes that must never be handed out: tombstoned by a prior process (found by the ctor
    /// scan) or quarantined this process. Guarded by <see cref="_freeIndexesLock"/>.</summary>
    private readonly HashSet<int> _retiredIndexes = [];
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

        // Startup reconcile (durable quarantine): find slot dirs a PRIOR process tombstoned and RETIRE those
        // indexes so they are never re-leased. We do NOT delete/reset the dirs here: this daemon adopts an
        // already-running gateway (AutoSpawn=false), so a sandbox session whose teardown failed can still be
        // mounted after the daemon restarts — touching the store would race live work and recreate the very
        // corruption the quarantine prevents. The dirs leak until an operator (or a future liveness-gated
        // reaper) reclaims them; capacity is unaffected because the pool allocates fresh indexes instead.
        ReconcileQuarantinedSlots();
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

            if (!Directory.Exists(slot.StorePath) || IsDirectoryEmpty(slot.StorePath))
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
    /// possibly-still-mounted store. Durable quarantine is part of the success contract: the
    /// <see cref="QuarantineMarkerName"/> tombstone is written FIRST, and the permit is released (capacity
    /// preserved — the next lease allocates a different index) ONLY if that write succeeds. If the tombstone
    /// cannot be persisted the call fails CLOSED — the permit is NOT released — because after a restart the
    /// in-memory retirement is gone and an unmarked non-empty store would be reused as warm while its session
    /// may still be live; consuming the permit is the safe choice over that reuse.
    /// </summary>
    public Task QuarantineAsync(ReviewSlot slot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(slot);

        // Retire the index in-process regardless (so it is never handed out again this process), then persist
        // the tombstone that carries the retirement across a restart.
        lock (_freeIndexesLock)
        {
            _retiredIndexes.Add(slot.Index);
        }

        var durable = TryWriteQuarantineMarker(slot);
        if (!durable)
        {
            // Fail closed: without a durable tombstone we cannot guarantee the store won't be reused as warm
            // after a restart, so do NOT release the permit — the slot is fully retired and its capacity is
            // consumed until a restart. A marker write failing means the pool root is unwritable (a serious
            // disk fault), so shrinking capacity here is both rare and the safe direction.
            _logger.LogError(
                "Quarantine of review slot {Index} at {StorePath} could not be made durable (tombstone write "
                    + "failed); retiring the slot AND its permit rather than risk reusing a possibly-live store.",
                slot.Index, slot.StorePath);
            return Task.CompletedTask;
        }

        _logger.LogWarning(
            "Quarantining review slot {Index} at {StorePath}: index retired (not reused) because its session "
                + "teardown could not be confirmed; the tombstone retires it across a restart too.",
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
            if (_freeIndexes.Count > 0)
            {
                // Returned indexes had a CONFIRMED teardown, so they are never retired — safe to recycle.
                return _freeIndexes.Pop();
            }

            // Skip any retired index (tombstoned by a prior process, or quarantined this process) so a
            // quarantined slot dir / possibly-live store is never handed back out.
            while (_retiredIndexes.Contains(_nextIndex))
            {
                _nextIndex++;
            }

            return _nextIndex++;
        }
    }

    private ReviewSlot BuildSlot(int index)
    {
        var hostPath = Path.Combine(_hostRoot, $"slot-{index}");
        return new ReviewSlot(index, hostPath, Path.Combine(hostPath, "store"), Path.Combine(hostPath, _scratchDirName));
    }

    private static string QuarantineMarkerPath(ReviewSlot slot) =>
        Path.Combine(slot.HostPath, QuarantineMarkerName);

    /// <summary>Persists the quarantine tombstone. Returns <c>true</c> only when the marker is durably on disk;
    /// the caller fails closed on <c>false</c> (does not release the permit) rather than risk a cross-restart
    /// reuse of an unmarked tainted store.</summary>
    private bool TryWriteQuarantineMarker(ReviewSlot slot)
    {
        try
        {
            Directory.CreateDirectory(slot.HostPath);
            File.WriteAllText(QuarantineMarkerPath(slot), string.Empty);
            return File.Exists(QuarantineMarkerPath(slot));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Quarantine tombstone write failed for review slot {Index} at {HostPath}.",
                slot.Index, slot.HostPath);
            return false;
        }
    }

    /// <summary>Startup reconcile: scans for <c>slot-*</c> dirs a prior process tombstoned and RETIRES those
    /// indexes so they are never re-leased. It does NOT delete/reset the dirs — this daemon adopts a persistent
    /// external gateway, so a session whose teardown failed can still be mounted after a restart, and touching
    /// the store would race live work. The retired dirs leak on disk (bounded by an operator / future
    /// liveness-gated reaper); capacity is unaffected because fresh indexes are allocated instead.</summary>
    private void ReconcileQuarantinedSlots()
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

                var name = Path.GetFileName(dir);
                if (int.TryParse(name.AsSpan("slot-".Length), out var index))
                {
                    _retiredIndexes.Add(index);
                    _logger.LogWarning(
                        "Retiring quarantined review slot {Index} left by a prior process: {Dir} (left on disk; "
                            + "its gateway session may still be mounted, so it is not reclaimed here).",
                        index, dir);
                }
            }
        }
        catch (Exception ex)
        {
            // Best-effort scan. A tombstone we fail to observe here can still be reused as warm — but the write
            // side fails closed (a quarantine that could not be made durable never released its permit), so the
            // common path stays safe; this only degrades a partially-unreadable pool root.
            _logger.LogWarning(ex, "Quarantined-slot reconcile scan failed under {HostRoot}.", _hostRoot);
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
