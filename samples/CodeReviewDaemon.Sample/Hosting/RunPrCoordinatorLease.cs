namespace CodeReviewDaemon.Sample.Hosting;

/// <summary>
/// The fail-closed coordinator-lease gate for <c>--run-pr</c> (task #81, security review round 1).
/// <see cref="Orchestration.PrOrchestrator.RunAsync"/> allocates from the same in-memory, per-process
/// review-slot pool the long-running daemon uses; running an operator-approved pilot PR without holding the
/// SAME OS-level coordinator lease the daemon holds risks two processes allocating the same slot
/// workspace directory at once. This never introduces new cross-process locking — it reuses
/// <see cref="WorkflowCoordinatorLease.Acquire"/> exactly as the daemon does — and it never crashes: a
/// held lease is reported as a clean, structured rejection so <c>--run-pr</c> can emit it on stdout and
/// exit non-zero instead of throwing.
/// <para>
/// Because the daemon and this command apply for the SAME lease, an operator must stop the running daemon
/// before using <c>--run-pr</c> for a pilot: the daemon holds this lease for its entire lifetime, so a
/// second process can never acquire it while the daemon is up.
/// </para>
/// </summary>
internal static class RunPrCoordinatorLease
{
    public static RunPrCoordinatorLeaseOutcome TryAcquire(string databasePath)
    {
        try
        {
            return RunPrCoordinatorLeaseOutcome.Acquired(WorkflowCoordinatorLease.Acquire(databasePath));
        }
        catch (InvalidOperationException ex)
        {
            return RunPrCoordinatorLeaseOutcome.Unavailable(ex.Message);
        }
    }
}

/// <summary>
/// The outcome of one <see cref="RunPrCoordinatorLease.TryAcquire"/> call. Disposing it (whether or not the
/// lease was acquired) releases the underlying OS handle exactly once.
/// </summary>
internal sealed class RunPrCoordinatorLeaseOutcome : IDisposable
{
    private readonly FileStream? _lease;

    private RunPrCoordinatorLeaseOutcome(FileStream? lease, string? failureReason)
    {
        _lease = lease;
        FailureReason = failureReason;
    }

    public static RunPrCoordinatorLeaseOutcome Acquired(FileStream lease) => new(lease, failureReason: null);

    public static RunPrCoordinatorLeaseOutcome Unavailable(string failureReason) => new(null, failureReason);

    public bool IsAcquired => _lease is not null;

    /// <summary>Non-null only when <see cref="IsAcquired"/> is false — the exact message from
    /// <see cref="WorkflowCoordinatorLease.Acquire"/>'s <see cref="InvalidOperationException"/>.</summary>
    public string? FailureReason { get; }

    public void Dispose() => _lease?.Dispose();
}
