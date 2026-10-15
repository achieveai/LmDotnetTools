using System.Globalization;
using AchieveAi.LmDotnetTools.Sandbox.Command;

namespace CodeReviewDaemon.Sample.Workspace;

/// <summary>A linked worktree inside the shared review workspace, never a separate Gateway mount.</summary>
internal sealed record ReviewSlot(string RepositoryName, int Index)
{
    public string Name => $"{RepositoryName}-{Index.ToString(CultureInfo.InvariantCulture)}";
    public string WorktreeRelativePath => $".worktrees/{Name}";
    public string SourceRelativePath => $"{WorktreeRelativePath}/repos/{RepositoryName}";
    public string WorktreeRoot => $"/workspace/{WorktreeRelativePath}";
    public string SourceRoot => $"/workspace/{SourceRelativePath}";

    public void Validate()
    {
        RepositoryNameRules.Validate(RepositoryName, nameof(RepositoryName));
        ArgumentOutOfRangeException.ThrowIfNegative(Index);
        if (WorkspaceRelativePath.Normalize(WorktreeRelativePath, nameof(WorktreeRelativePath)) != WorktreeRelativePath)
            throw new ArgumentException("The review slot must be a canonical worktree path.");
    }
}

internal sealed class ReviewSlotClaim : IAsyncDisposable
{
    private int _state;
    private readonly ILogger _logger;

    public ReviewSlotClaim(ReviewSlotPool pool, ReviewSlot slot, ILogger logger)
    {
        Pool = pool;
        Slot = slot;
        _logger = logger;
    }

    public ReviewSlot Slot { get; }
    public ReviewSlotPool Pool { get; }

    public void Transfer()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            throw new InvalidOperationException("Review slot claim was already transferred or returned.");
        _logger.LogDebug("Review slot claim transferred {SlotName}", Slot.Name);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _state, 2, 0) == 0)
        {
            await Pool.ReturnAsync(Slot, CancellationToken.None).ConfigureAwait(false);
            _logger.LogDebug("Untransferred review slot claim returned {SlotName}", Slot.Name);
        }
    }
}

internal interface IReviewSlotPool
{
    IReadOnlyList<ReviewSlot> Slots { get; }
    Task<ReviewSlot> LeaseAsync(string repositoryName, CancellationToken cancellationToken);
    Task<ReviewSlot?> TryLeaseAsync(string repositoryName, CancellationToken cancellationToken);
    Task<ReviewSlot> LeasePreferredAsync(ReviewSlot slot, CancellationToken cancellationToken);
    Task<ReviewSlot?> TryLeasePreferredAsync(ReviewSlot slot, CancellationToken cancellationToken);
    Task<ReviewSlot> RecoverLeaseAsync(ReviewSlot slot, CancellationToken cancellationToken);
    Task ReturnAsync(ReviewSlot slot, CancellationToken cancellationToken);
    Task RetireAsync(ReviewSlot slot, CancellationToken cancellationToken);
}

/// <summary>
/// Repository-aware bounded leases. One lock protects availability, ownership and retirement together; there
/// is no separate semaphore count to drift from the free list. Retired addresses remain unavailable.
/// </summary>
internal sealed class ReviewSlotPool : IReviewSlotPool
{
    private readonly Lock _gate = new();
    private readonly HashSet<ReviewSlot> _active = [];
    private readonly HashSet<ReviewSlot> _retired = [];
    private readonly Dictionary<string, string> _repositories = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<ReviewSlotPool> _logger;
    private TaskCompletionSource _changed = NewSignal();

    public ReviewSlotPool(
        IEnumerable<string> repositoryNames,
        int slotsPerRepository,
        ILogger<ReviewSlotPool> logger,
        IReadOnlyDictionary<string, int>? slotsByRepository = null
    )
    {
        ArgumentNullException.ThrowIfNull(repositoryNames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slotsPerRepository);
        if (slotsPerRepository > 6)
            throw new ArgumentOutOfRangeException(
                nameof(slotsPerRepository),
                "review-setup supports slots 0 through 5."
            );
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        foreach (var name in repositoryNames)
        {
            RepositoryNameRules.Validate(name, nameof(repositoryNames));
            if (!_repositories.TryAdd(name, name))
                throw new ArgumentException(
                    "Configured source repository names must be unique.",
                    nameof(repositoryNames)
                );
        }
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, count) in slotsByRepository ?? new Dictionary<string, int>())
        {
            if (!_repositories.ContainsKey(name) || !counts.TryAdd(name, count))
                throw new ArgumentException(
                    "Slot overrides must name unique enabled repositories.",
                    nameof(slotsByRepository)
                );
            if (count is < 1 or > 6)
                throw new ArgumentOutOfRangeException(
                    nameof(slotsByRepository),
                    "review-setup supports 1 through 6 slots per repository."
                );
        }
        Slots = _repositories
            .Values.SelectMany(name =>
                Enumerable
                    .Range(0, counts.GetValueOrDefault(name, slotsPerRepository))
                    .Select(index => new ReviewSlot(name, index))
            )
            .ToArray();
    }

    public ReviewSlotPool(string repositoryName, int slotsPerRepository, ILogger<ReviewSlotPool> logger)
        : this([repositoryName], slotsPerRepository, logger) { }

    public IReadOnlyList<ReviewSlot> Slots { get; }

    internal ReviewSlotClaim? TryClaim(string repositoryName, ReviewSlot? preferred = null)
    {
        var canonical = CanonicalRepository(repositoryName);
        if (preferred is not null && (preferred.RepositoryName != canonical || !Slots.Contains(preferred)))
            throw new SlotAddressUnusableException("Preferred review slot is not configured for this repository.");
        lock (_gate)
        {
            var slot = preferred is null ? TakeAvailable(canonical) : TakePreferred(preferred);
            if (slot is null)
                return null;
            _logger.LogDebug("Review slot claimed {SlotName}", slot.Name);
            return new ReviewSlotClaim(this, slot, _logger);
        }
    }

    private ReviewSlot? TakePreferred(ReviewSlot slot)
    {
        if (_active.Contains(slot) || _retired.Contains(slot))
            return null;
        _active.Add(slot);
        return slot;
    }

    public async Task<ReviewSlot> LeaseAsync(string repositoryName, CancellationToken cancellationToken)
    {
        var canonical = CanonicalRepository(repositoryName);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task changed;
            lock (_gate)
            {
                if (TakeAvailable(canonical) is { } slot)
                    return slot;
                changed = _changed.Task;
            }
            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<ReviewSlot?> TryLeaseAsync(string repositoryName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var canonical = CanonicalRepository(repositoryName);
        lock (_gate)
            return Task.FromResult(TakeAvailable(canonical));
    }

    public async Task<ReviewSlot> LeasePreferredAsync(ReviewSlot slot, CancellationToken cancellationToken)
    {
        ValidateAddress(slot);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task changed;
            lock (_gate)
            {
                if (_retired.Contains(slot))
                    throw new SlotAddressUnusableException("The requested review slot is quarantined.");
                if (_active.Add(slot))
                    return slot;
                changed = _changed.Task;
            }
            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<ReviewSlot?> TryLeasePreferredAsync(ReviewSlot slot, CancellationToken cancellationToken)
    {
        ValidateAddress(slot);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            return Task.FromResult<ReviewSlot?>(!_retired.Contains(slot) && _active.Add(slot) ? slot : null);
    }

    public Task<ReviewSlot> RecoverLeaseAsync(ReviewSlot slot, CancellationToken cancellationToken)
    {
        ValidateAddress(slot);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_retired.Contains(slot) || !_active.Add(slot))
                throw new InvalidOperationException("Persisted review slot is already leased or quarantined.");
            return Task.FromResult(slot);
        }
    }

    public Task ReturnAsync(ReviewSlot slot, CancellationToken cancellationToken)
    {
        ValidateAddress(slot);
        lock (_gate)
        {
            if (!_active.Remove(slot))
                throw new InvalidOperationException("Cannot return a slot that this pool does not hold.");
            SignalChanged();
        }
        _logger.LogDebug("Review slot returned {SlotName}", slot.Name);
        return Task.CompletedTask;
    }

    public Task RetireAsync(ReviewSlot slot, CancellationToken cancellationToken)
    {
        ValidateAddress(slot);
        lock (_gate)
        {
            if (!_active.Remove(slot))
                throw new InvalidOperationException("Cannot retire a slot that this pool does not hold.");
            _retired.Add(slot);
            SignalChanged();
        }
        _logger.LogError("Quarantined review slot {SlotName}; it will not be reused in this process.", slot.Name);
        return Task.CompletedTask;
    }

    private ReviewSlot? TakeAvailable(string repositoryName)
    {
        var slot = Slots.FirstOrDefault(value =>
            value.RepositoryName == repositoryName && !_active.Contains(value) && !_retired.Contains(value)
        );
        if (slot is not null)
            _active.Add(slot);
        return slot;
    }

    private string CanonicalRepository(string name) =>
        _repositories.TryGetValue(name, out var canonical)
            ? canonical
            : throw new SlotAddressUnusableException("Repository has no configured review slots.");

    private void ValidateAddress(ReviewSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        if (!Slots.Contains(slot))
            throw new SlotAddressUnusableException("Persisted slot does not belong to the configured repository pool.");
    }

    private void SignalChanged()
    {
        var previous = _changed;
        _changed = NewSignal();
        previous.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal static class RepositoryNameRules
{
    public static string FromEnabledRepo(string enabledRepo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(enabledRepo);
        var normalized = enabledRepo.Trim().TrimEnd('/');
        var name = normalized[(normalized.LastIndexOf('/') + 1)..];
        Validate(name, nameof(enabledRepo));
        return name;
    }

    public static void Validate(string repositoryName, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryName, paramName);
        if (
            repositoryName is "." or ".."
            || repositoryName.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
            )
        )
            throw new ArgumentException(
                "Repository name must contain only ASCII letters, digits, '-', '_' or '.'.",
                paramName
            );
    }
}
