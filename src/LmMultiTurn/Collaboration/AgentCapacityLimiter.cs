namespace AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;

/// <summary>
/// One agent's claim on the collaboration's root-wide agent budget, released exactly once.
/// </summary>
/// <remarks>
/// <para>
/// A lease rather than a counter because the claim has to outlive the method that took it: it is
/// acquired before any per-manager gate, queue, or lock on the spawn path, survives a restart of the
/// agent it belongs to, and is released when that agent leaves admitted state — which is a different
/// stack, on a different thread, possibly much later.
/// </para>
/// <para>
/// Release is idempotent for the same reason the sub-agent gate guard is: the paths that can release
/// a lease (normal completion, error, stop, dispose, an abandoned restart) overlap, and a second
/// release would hand a permit back that was never taken, letting the collaboration quietly exceed
/// its cap.
/// </para>
/// </remarks>
public sealed class AgentCapacityLease : IDisposable
{
    private readonly AgentCapacityLimiter _limiter;
    private int _released;

    internal AgentCapacityLease(AgentCapacityLimiter limiter, string agentId)
    {
        _limiter = limiter;
        AgentId = agentId;
    }

    /// <summary>The agent this lease was taken for.</summary>
    public string AgentId { get; }

    /// <summary>Whether the permit has already been handed back.</summary>
    public bool IsReleased => Volatile.Read(ref _released) != 0;

    /// <summary>
    /// Hands the permit back, at most once.
    /// </summary>
    /// <returns>True if this call released it; false if it was already released.</returns>
    public bool Release()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return false;
        }

        _limiter.ReturnPermit();
        return true;
    }

    /// <summary>Releases the permit, so a lease can be scoped with <c>using</c> where that fits.</summary>
    public void Dispose()
    {
        _ = Release();
    }
}

/// <summary>
/// The root-wide ceiling on simultaneously admitted agents, handed out as leases.
/// </summary>
/// <remarks>
/// <para>
/// This is the bound that per-manager concurrency gates cannot provide: each nested parent owns an
/// independent manager with an independent gate, so without a shared limiter a hierarchy of depth
/// <c>d</c> can admit the product of every gate rather than a fixed total.
/// </para>
/// <para>
/// Acquisition never blocks. A blocking acquire combined with the required
/// root-lease-before-manager-gate ordering would be a deadlock waiting for a workload to find it, so
/// exhaustion is reported as a plain null and turned into a recoverable result by the caller.
/// </para>
/// </remarks>
public sealed class AgentCapacityLimiter
{
    private int _inUse;

    /// <summary>Creates a limiter with a fixed ceiling.</summary>
    /// <param name="capacity">Largest number of simultaneously admitted agents.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is below one.</exception>
    public AgentCapacityLimiter(int capacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacity),
                capacity,
                "A collaboration must admit at least one agent."
            );
        }

        Capacity = capacity;
    }

    /// <summary>Largest number of simultaneously admitted agents.</summary>
    public int Capacity { get; }

    /// <summary>How many permits are currently held.</summary>
    public int InUse => Volatile.Read(ref _inUse);

    /// <summary>How many permits remain.</summary>
    public int Available => Capacity - InUse;

    /// <summary>
    /// Takes a permit for an agent if one is free, without ever waiting.
    /// </summary>
    /// <param name="agentId">The agent the permit is for.</param>
    /// <returns>A lease, or null when the collaboration is at capacity.</returns>
    /// <exception cref="ArgumentException"><paramref name="agentId"/> is blank.</exception>
    public AgentCapacityLease? TryAcquire(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        // Compare-and-swap rather than a lock: the contended case is two spawns racing for the last
        // permit, which resolves in one retry, and the uncontended case costs a single interlocked op.
        while (true)
        {
            var current = Volatile.Read(ref _inUse);
            if (current >= Capacity)
            {
                return null;
            }

            if (Interlocked.CompareExchange(ref _inUse, current + 1, current) == current)
            {
                return new AgentCapacityLease(this, agentId);
            }
        }
    }

    internal void ReturnPermit()
    {
        _ = Interlocked.Decrement(ref _inUse);
    }
}
