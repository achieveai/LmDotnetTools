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
    private int _nextIndex;

    public ReviewSlotPool(
        int maxSlots,
        string? hostRoot,
        string scratchDirName,
        ILogger<ReviewSlotPool> logger,
        string slotDirPrefix = "slot-")
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
        catch (SlotHostPathRefusedException)
        {
            // Every other failure here is about this attempt, so the index goes back on the free list and the next
            // lease retries it. A refusal is about the ADDRESS, and it will still be true on the next lease: the
            // free list is a stack, so pushing a refused index back makes it the very next one handed out, and a
            // single planted junction under one slot would then refuse every lease the pool ever serves. Retiring
            // the index costs a directory name and nothing else — the gate is released either way, so concurrency
            // is unchanged and the next lease allocates a fresh address.
            _logger.LogError(
                "Retiring slot index {SlotIndex} at {HostPath}: its host paths could not be established as contained.",
                index,
                slot.HostPath);
            _gate.Release();
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
                throw new SlotHostPathRefusedException(
                    $"Refusing to lease slot {slot.Index}: '{refusal.Path}' — {refusal.Reason}. Not following it, "
                        + "and not removing it either.");
            }
        }
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
        var hostPath = Path.Combine(_hostRoot, SlotDirectoryName(index));
        return new ReviewSlot(index, hostPath, Path.Combine(hostPath, "store"), Path.Combine(hostPath, _scratchDirName));
    }
}
