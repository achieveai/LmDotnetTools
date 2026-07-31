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
            Directory.CreateDirectory(slot.HostPath);
            Directory.CreateDirectory(slot.ScratchPath);
            return slot;
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
