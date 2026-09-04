namespace CodeReviewDaemon.Sample.Workspace;

/// <summary>
/// One review-checkout slot address handed out by <see cref="ReviewSlotPool"/>. The pool owns only the
/// address and lease; repository creation and validation begin after the slot is mounted through the sandbox SDK.
/// </summary>
internal sealed record ReviewSlot(int Index, string HostPath, string StorePath, string ScratchPath);

internal interface IReviewSlotPool
{
    Task<ReviewSlot> LeaseAsync(CancellationToken cancellationToken);

    Task ReturnAsync(ReviewSlot slot, CancellationToken cancellationToken);

    /// <summary>
    /// Releases the lease WITHOUT putting the address back into circulation. Used when an address is unsafe to
    /// reuse, including an uncontained host path or a hosted workspace whose backend quiescence is unconfirmed.
    /// Retiring costs a directory name and nothing else: the gate is released either way, so the pool continues
    /// serving its full concurrency at a fresh address.
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
    private readonly HashSet<int> _quarantinedIndexes;
    private int _nextIndex;

    public ReviewSlotPool(
        int maxSlots,
        string? hostRoot,
        string scratchDirName,
        ILogger<ReviewSlotPool> logger,
        string slotDirPrefix = "slot-",
        IReadOnlyCollection<string>? quarantinedHostPaths = null
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
        _quarantinedIndexes = ParseQuarantinedIndexes(quarantinedHostPaths);
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
        catch (SlotAddressUnusableException ex)
        {
            // Every other failure here is about this attempt, so the index goes back on the free list and the next
            // lease retries it. A refusal is about the ADDRESS, and it will still be true on the next lease, so it
            // is retired instead — see the reasoning on IReviewSlotPool.RetireAsync.
            _logger.LogError(
                ex,
                "Retiring slot index {SlotIndex} at {HostPath}: its host paths could not be established as contained.",
                slot.Index,
                slot.HostPath
            );
            Retire(slot);
            throw;
        }
        catch
        {
            lock (_freeIndexesLock)
            {
                _freeIndexes.Push(index);
            }

            _gate.Release();
            throw;
        }
    }

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

    public Task RetireAsync(ReviewSlot slot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(slot);
        Retire(slot);
        return Task.CompletedTask;
    }

    private void Retire(ReviewSlot slot)
    {
        _logger.LogWarning(
            "Retiring slot index {SlotIndex} at {HostPath}. The address is not returned to the pool; concurrency "
                + "is unaffected and the next lease allocates a fresh one.",
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
            while (_freeIndexes.TryPop(out var recycled))
            {
                if (!_quarantinedIndexes.Contains(recycled))
                {
                    return recycled;
                }
            }

            while (_quarantinedIndexes.Contains(_nextIndex))
            {
                _nextIndex++;
            }

            return _nextIndex++;
        }
    }

    private HashSet<int> ParseQuarantinedIndexes(IReadOnlyCollection<string>? hostPaths)
    {
        var indexes = new HashSet<int>();
        if (hostPaths is null)
        {
            return indexes;
        }

        var root = Path.GetFullPath(_hostRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootPrefix = root + Path.DirectorySeparatorChar;
        foreach (var hostPath in hostPaths)
        {
            if (string.IsNullOrWhiteSpace(hostPath))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(hostPath);
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                LogIgnoredQuarantine(hostPath);
                continue;
            }

            var relativePath = fullPath[rootPrefix.Length..];
            var separatorIndex = relativePath.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
            var slotLeaf = separatorIndex < 0 ? relativePath : relativePath[..separatorIndex];
            if (!TryParseSlotIndex(slotLeaf, out var index))
            {
                LogIgnoredQuarantine(hostPath);
                continue;
            }

            if (separatorIndex >= 0)
            {
                _logger.LogWarning(
                    "Historical unresolved path {QuarantinedHostPath} is beneath current slot address {SlotHostPath}; "
                        + "quarantining the slot ancestor because the persisted path may identify a mounted descendant.",
                    hostPath,
                    Path.Combine(root, slotLeaf)
                );
            }

            _ = indexes.Add(index);
        }

        return indexes;
    }

    private bool TryParseSlotIndex(string leaf, out int index)
    {
        index = -1;
        return leaf.StartsWith(_slotDirPrefix, StringComparison.Ordinal)
            && int.TryParse(leaf.AsSpan(_slotDirPrefix.Length), out index)
            && index >= 0
            && string.Equals(leaf, SlotDirectoryName(index), StringComparison.Ordinal);
    }

    private void LogIgnoredQuarantine(string hostPath) =>
        _logger.LogError(
            "Ignoring historical unresolved slot path {QuarantinedHostPath}: it cannot collide with an address "
                + "issued by the current pool root {PoolRoot} and slot prefix {SlotPrefix}. The historical claim "
                + "remains unresolved in durable state.",
            hostPath,
            _hostRoot,
            _slotDirPrefix
        );

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
