namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// Counts CONSECUTIVE, identically-worded prepare failures for one pooled slot address, and says when the
/// caller should escalate to a re-clone REGARDLESS of how <see cref="Workspace.Git.GitFailureClassifier"/>
/// classified the failure (issue #582, fix 3). <see cref="DaemonReviewStageExecutor.PrepareWithRecoveryAsync"/>'s
/// existing recovery ladder reclones only on a TYPE match (<c>SlotNeedsRecloneException</c> /
/// <c>SlotCorruptException</c>); a message shape the classifier has not been taught surfaces a plain
/// <c>InvalidOperationException</c> instead, which that filter does not catch, so nothing ever repairs it — that
/// is exactly what let a corrupt submodule gitdir wedge every mcqdb review for 38 hours even though
/// <see cref="Workspace.SlotHygiene"/>'s recovery machinery was otherwise working correctly. This is the
/// backstop for the classifier's inevitable next gap: it does not need to know WHY prepare failed, only that
/// the SAME address failed the SAME way <see cref="MaxConsecutiveFailures"/> times running.
/// <para>
/// Keyed by store root rather than run id on purpose. A run-scoped tracker (like <see cref="RetryGovernor"/>)
/// resets on every new commit, so it can never see a condition that outlives any single run's retry budget by
/// recurring across DIFFERENT runs leased onto the same wedged slot — which is precisely the mcqdb shape: 30,700
/// log lines were spread across six different review runs over two days, none of which individually retried
/// more than its own governed budget.
/// </para>
/// <para>
/// "Identical" is judged on the failure's message text so a streak actually proves a STUCK condition: a slot
/// that fails for two DIFFERENT reasons in a row (a transient network blip, then something unrelated) has not
/// demonstrated anything a destructive re-clone should be spent on, so a differing message resets the streak
/// rather than accumulating toward it.
/// </para>
/// </summary>
internal sealed class SlotPrepareFailureEscalator
{
    /// <summary>
    /// Small on purpose: this is a BACKSTOP for a gap in classifier coverage, not the primary recovery path —
    /// that remains <see cref="DaemonReviewStageExecutor.PrepareWithRecoveryAsync"/>'s type-filtered catch,
    /// which already re-clones on the FIRST classified-corrupt failure. Three identical repeats is enough to
    /// tell a stuck condition from ordinary noise while still being cheap in the currency that actually matters
    /// here — failed review runs — before the destructive re-clone fires.
    /// </summary>
    internal const int MaxConsecutiveFailures = 3;

    private sealed class State
    {
        public string? LastMessage;
        public int ConsecutiveCount;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, State> _states = new(
        StringComparer.Ordinal
    );

    /// <summary>
    /// Records a prepare failure for <paramref name="storeRoot"/> and returns whether the caller should now
    /// escalate to a re-clone regardless of classification. A message that differs from the slot's last
    /// recorded failure restarts the streak at one — a changing symptom has not shown the stuck condition this
    /// backstop targets.
    /// </summary>
    public bool RecordFailureAndShouldEscalate(string storeRoot, string message)
    {
        var state = _states.GetOrAdd(storeRoot, static _ => new State());
        lock (state)
        {
            if (!string.Equals(state.LastMessage, message, StringComparison.Ordinal))
            {
                state.LastMessage = message;
                state.ConsecutiveCount = 0;
            }

            state.ConsecutiveCount++;
            if (state.ConsecutiveCount < MaxConsecutiveFailures)
            {
                return false;
            }

            // Escalating clears the streak: the re-clone the caller is about to run is itself a repair attempt,
            // so a FUTURE failure (if any) starts counting fresh instead of re-escalating on every single
            // subsequent prepare forever.
            state.ConsecutiveCount = 0;
            state.LastMessage = null;
            return true;
        }
    }

    /// <summary>Clears a slot's streak after a successful prepare. The condition this backstop watches for is
    /// a RUN of failures, and a success ends any run in progress.</summary>
    public void RecordSuccess(string storeRoot) => _states.TryRemove(storeRoot, out _);
}
