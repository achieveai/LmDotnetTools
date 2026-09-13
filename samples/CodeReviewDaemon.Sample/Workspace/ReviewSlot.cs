namespace CodeReviewDaemon.Sample.Workspace;

/// <summary>
/// One review-checkout slot address handed out by <see cref="ReviewSlotPool"/>. The pool owns only the
/// address and lease; repository creation and validation begin after the slot is mounted through the sandbox SDK.
/// </summary>
internal sealed record ReviewSlot(int Index, string HostPath, string StorePath, string ScratchPath);

internal interface IReviewSlotPool
{
    Task<ReviewSlot> LeaseAsync(CancellationToken cancellationToken);

    Task<ReviewSlot?> TryLeaseAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("This pool cannot try a workspace lease.");

    /// <summary>Waits for and leases an exact previously returned address without changing its contents.</summary>
    Task<ReviewSlot> LeasePreferredAsync(ReviewSlot slot, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This pool cannot lease a preferred workspace address.");

    Task<ReviewSlot?> TryLeasePreferredAsync(ReviewSlot slot, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This pool cannot try a preferred workspace address.");

    /// <summary>Reserves the exact persisted address after restart, without preparing, cleaning, or changing its contents.</summary>
    Task<ReviewSlot> RecoverLeaseAsync(ReviewSlot slot, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This pool cannot restore persisted workspace assignments.");

    Task ReturnAsync(ReviewSlot slot, CancellationToken cancellationToken);

    /// <summary>
    /// Releases the lease WITHOUT putting the address back into circulation, for a slot whose host paths could
    /// not be established as contained (<see cref="SlotAddressUnusableException"/>).
    /// <para>
    /// The distinction from <see cref="ReturnAsync"/> is the whole reason this exists. Returning is for a slot
    /// whose next lease might go differently, which is every ordinary failure. A refusal is not one: it is a
    /// statement about the ADDRESS, and it stays true until somebody looks at the disk. The free list is a
    /// stack, so returning a refused index makes it the very next one handed out — one planted entry would then
    /// consume a slot's worth of the pool's throughput on a run that cannot possibly prepare, indefinitely.
    /// Retiring costs a directory name and nothing else: the gate is released either way, so the pool goes on
    /// serving its full concurrency at a fresh address.
    /// </para>
    /// </summary>
    Task RetireAsync(ReviewSlot slot, CancellationToken cancellationToken);
}

/// <summary>
/// A bounded pool of stable workspace addresses. It deliberately does not inspect, clone, repair, or delete the
/// store: those operations require the run-bound <c>SandboxClient</c> session mounted over the leased address.
/// </summary>
internal sealed class ReviewSlotPool : IReviewSlotPool
{
    private readonly string _hostRoot;
    private readonly string _scratchDirName;
    private readonly string _slotDirPrefix;
    private readonly ILogger<ReviewSlotPool> _logger;
    private readonly SemaphoreSlim _gate;
    private readonly Lock _freeIndexesLock = new();
    private readonly Stack<int> _freeIndexes = new();
    private readonly HashSet<int> _activeIndexes = [];
    private readonly HashSet<int> _preferredIndexes = [];
    private TaskCompletionSource<bool> _leaseChanged = NewLeaseSignal();
    private int _nextIndex;

    public ReviewSlotPool(
        int maxSlots,
        string? hostRoot,
        string scratchDirName,
        ILogger<ReviewSlotPool> logger,
        string slotDirPrefix = "slot-"
    )
    {
        if (maxSlots < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSlots), maxSlots, "At least one slot is required.");
        }

        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchDirName);
        ArgumentException.ThrowIfNullOrWhiteSpace(slotDirPrefix);

        _hostRoot = hostRoot ?? Path.Combine(AppContext.BaseDirectory, "review-pool");
        _logger = logger;
        _scratchDirName = scratchDirName;
        _slotDirPrefix = slotDirPrefix;
        _gate = new SemaphoreSlim(maxSlots, maxSlots);
    }

    public string SlotDirectoryName(int index) => $"{_slotDirPrefix}{index}";

    public async Task<ReviewSlot> LeaseAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var index = TakeIndex();
        var slot = BuildSlot(index);
        try
        {
            GuardSlotPaths(slot);
            Directory.CreateDirectory(slot.HostPath);
            Directory.CreateDirectory(slot.ScratchPath);
            return slot;
        }
        catch (SlotAddressUnusableException)
        {
            // Every other failure here is about this attempt, so the index goes back on the free list and the next
            // lease retries it. A refusal is about the ADDRESS, and it will still be true on the next lease, so it
            // is retired instead — see the reasoning on IReviewSlotPool.RetireAsync.
            Retire(slot);
            throw;
        }
        catch
        {
            lock (_freeIndexesLock)
            {
                _activeIndexes.Remove(index);
                _freeIndexes.Push(index);
            }

            _gate.Release();
            throw;
        }
    }

    public async Task<ReviewSlot?> TryLeaseAsync(CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        var index = TakeIndex();
        var slot = BuildSlot(index);
        try
        {
            GuardSlotPaths(slot);
            Directory.CreateDirectory(slot.HostPath);
            Directory.CreateDirectory(slot.ScratchPath);
            return slot;
        }
        catch (SlotAddressUnusableException)
        {
            Retire(slot);
            throw;
        }
        catch
        {
            lock (_freeIndexesLock)
            {
                _activeIndexes.Remove(index);
                _freeIndexes.Push(index);
            }
            _gate.Release();
            throw;
        }
    }

    public async Task<ReviewSlot> RecoverLeaseAsync(ReviewSlot slot, CancellationToken cancellationToken)
    {
        ValidatePersistedSlot(slot);
        lock (_freeIndexesLock)
        {
            if (_preferredIndexes.Contains(slot.Index) || !_activeIndexes.Add(slot.Index))
            {
                throw new InvalidOperationException("Persisted slot is already leased or being recovered.");
            }
            _nextIndex = Math.Max(_nextIndex, slot.Index + 1);
            RemoveFreeIndex(slot.Index);
        }
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return slot;
        }
        catch
        {
            lock (_freeIndexesLock)
            {
                _activeIndexes.Remove(slot.Index);
                _freeIndexes.Push(slot.Index);
                SignalLeaseChanged();
            }
            throw;
        }
    }

    public async Task<ReviewSlot> LeasePreferredAsync(ReviewSlot slot, CancellationToken cancellationToken)
    {
        ValidatePersistedSlot(slot);
        lock (_freeIndexesLock)
        {
            if (!_preferredIndexes.Add(slot.Index))
            {
                throw new InvalidOperationException("Persisted slot already has a preferred lease waiter.");
            }
        }

        var permitHeld = false;
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            permitHeld = true;
            while (true)
            {
                Task changed;
                lock (_freeIndexesLock)
                {
                    if (!_activeIndexes.Contains(slot.Index))
                    {
                        _activeIndexes.Add(slot.Index);
                        _preferredIndexes.Remove(slot.Index);
                        _nextIndex = Math.Max(_nextIndex, slot.Index + 1);
                        RemoveFreeIndex(slot.Index);
                        permitHeld = false;
                        return slot;
                    }
                    changed = _leaseChanged.Task;
                }
                await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_freeIndexesLock)
            {
                _preferredIndexes.Remove(slot.Index);
            }
            if (permitHeld)
            {
                _gate.Release();
            }
        }
    }

    public async Task<ReviewSlot?> TryLeasePreferredAsync(ReviewSlot slot, CancellationToken cancellationToken)
    {
        ValidatePersistedSlot(slot);
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        lock (_freeIndexesLock)
        {
            if (_activeIndexes.Contains(slot.Index) || _preferredIndexes.Contains(slot.Index))
            {
                _gate.Release();
                return null;
            }
            _activeIndexes.Add(slot.Index);
            _nextIndex = Math.Max(_nextIndex, slot.Index + 1);
            RemoveFreeIndex(slot.Index);
            return slot;
        }
    }

    public Task ReturnAsync(ReviewSlot slot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(slot);
        lock (_freeIndexesLock)
        {
            if (!_activeIndexes.Remove(slot.Index))
            {
                throw new InvalidOperationException("Cannot return a slot that this pool does not hold.");
            }
            _freeIndexes.Push(slot.Index);
            SignalLeaseChanged();
        }

        _gate.Release();
        return Task.CompletedTask;
    }

    public Task RetireAsync(ReviewSlot slot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(slot);
        Retire(slot);
        return Task.CompletedTask;
    }

    private void Retire(ReviewSlot slot)
    {
        lock (_freeIndexesLock)
        {
            if (!_activeIndexes.Remove(slot.Index))
            {
                throw new InvalidOperationException("Cannot retire a slot that this pool does not hold.");
            }
            SignalLeaseChanged();
        }
        _logger.LogError(
            "Retiring slot index {SlotIndex} at {HostPath}: its host paths could not be established as contained. "
                + "The address is not returned to the pool; concurrency is unaffected and the next lease allocates a "
                + "fresh one.",
            slot.Index,
            slot.HostPath
        );
        _gate.Release();
    }

    /// <summary>
    /// Refuses the slot unless the three addresses the daemon is about to create and hand out are contained.
    /// <para>
    /// <see cref="Directory.CreateDirectory(string)"/> is what makes this necessary: given a name that is already
    /// a junction it does not fail and does not create anything, it succeeds and returns the REDIRECTED target.
    /// The lease then hands that address to the sandbox mount, and every later guard is looking at the wrong tree
    /// — the clone, the review agent's writes, and the wipe all land wherever the junction points. Because the
    /// pool never opens the store itself, this call site is the earliest point at which the redirection is
    /// visible at all, and the last one before the address escapes into the rest of the run.
    /// </para>
    /// <para>
    /// The order matters as much as the check. All three are tested, because a slot directory can be perfectly
    /// contained while one name inside it is not — the store is the address the clone writes and the wipe
    /// deletes, and the scratch path is created here and later cleared and re-created by
    /// <see cref="ReviewSlotPreparer"/>. But the host path is tested FIRST, because testing a child means
    /// resolving a path that runs THROUGH the slot directory: if that directory is itself a junction, the guard
    /// reads whatever is at the far end and reports the offending entry as an address outside the pool. The
    /// refusal message is the only account anyone gets of what stopped the lease, and one naming a path the
    /// operator will not find under the slot sends them hunting for the wrong link.
    /// </para>
    /// <para>
    /// The pool ROOT is the ANCHOR of that chain rather than a link in it, and is deliberately not checked. It is
    /// the operator's own configured workspace path, and a deployment that deliberately puts the pool behind a
    /// junction is a normal deployment, not an attack — refusing there would break it. That is the same residual
    /// <see cref="ReviewSlotPreparer"/>'s wipe accepts above its own root. What the wipe does NOT cover is the
    /// span between the two: its root is the store, so the slot directory holding it falls in the wipe's
    /// unchecked-ancestor gap and no walk ever looks at it. Everything from the anchor down is the daemon's own,
    /// created here and writable by the review agent, so a link at any of the three is nobody's configuration.
    /// </para>
    /// </summary>
    private static void GuardSlotPaths(ReviewSlot slot)
    {
        ReadOnlySpan<string> paths = [slot.HostPath, slot.StorePath, slot.ScratchPath];
        foreach (var path in paths)
        {
            if (HostPathGuard.Check(path) is { } refusal)
            {
                throw new SlotAddressUnusableException(
                    $"Refusing to lease slot {slot.Index}: '{refusal.Path}' — {refusal.Reason}. Not following it, "
                        + "and not removing it either."
                );
            }
        }
    }

    private int TakeIndex()
    {
        lock (_freeIndexesLock)
        {
            var reserved = new Stack<int>();
            while (_freeIndexes.TryPeek(out var free) && _preferredIndexes.Contains(free))
            {
                reserved.Push(_freeIndexes.Pop());
            }
            var index = _freeIndexes.Count > 0 ? _freeIndexes.Pop() : _nextIndex++;
            while (reserved.TryPop(out var value))
            {
                _freeIndexes.Push(value);
            }
            _activeIndexes.Add(index);
            return index;
        }
    }

    private void ValidatePersistedSlot(ReviewSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        if (slot.Index < 0 || slot.Index == int.MaxValue)
        {
            throw new SlotAddressUnusableException("Persisted slot index is invalid.");
        }
        var expected = BuildSlot(slot.Index);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (
            !string.Equals(Path.GetFullPath(slot.HostPath), Path.GetFullPath(expected.HostPath), comparison)
            || !string.Equals(Path.GetFullPath(slot.StorePath), Path.GetFullPath(expected.StorePath), comparison)
            || !string.Equals(Path.GetFullPath(slot.ScratchPath), Path.GetFullPath(expected.ScratchPath), comparison)
        )
        {
            throw new SlotAddressUnusableException("Persisted slot does not belong to the configured pool.");
        }
        GuardSlotPaths(slot);
    }

    private void RemoveFreeIndex(int index)
    {
        var remaining = _freeIndexes.Where(value => value != index).Reverse().ToArray();
        _freeIndexes.Clear();
        foreach (var value in remaining)
        {
            _freeIndexes.Push(value);
        }
    }

    private void SignalLeaseChanged()
    {
        var signal = _leaseChanged;
        _leaseChanged = NewLeaseSignal();
        signal.TrySetResult(true);
    }

    private static TaskCompletionSource<bool> NewLeaseSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private ReviewSlot BuildSlot(int index)
    {
        var hostPath = Path.Combine(_hostRoot, SlotDirectoryName(index));
        return new ReviewSlot(
            index,
            hostPath,
            Path.Combine(hostPath, "store"),
            Path.Combine(hostPath, _scratchDirName)
        );
    }
}
