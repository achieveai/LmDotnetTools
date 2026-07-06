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
internal sealed class ReviewSlotPool
{
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
