namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

/// <summary>
/// The checks every <see cref="IRunLifecycleStore"/> implementation owes its callers, in one place
/// so the in-memory, file, and SQLite stores cannot drift apart on what they reject.
/// </summary>
internal static class RunLifecycleGuards
{
    /// <summary>
    /// Validates a state being recorded as started.
    /// </summary>
    /// <param name="state">The starting state.</param>
    /// <exception cref="ArgumentException">
    /// The state is missing a thread or run identifier, or claims to be terminal already.
    /// </exception>
    public static void ValidateStart(RunLifecycleState state)
    {
        if (string.IsNullOrEmpty(state.ThreadId))
        {
            throw new ArgumentException("A run lifecycle state requires a thread id.", nameof(state));
        }

        if (string.IsNullOrEmpty(state.RunId))
        {
            throw new ArgumentException("A run lifecycle state requires a run id.", nameof(state));
        }

        if (state.Phase != RunLifecyclePhase.Running)
        {
            throw new ArgumentException(
                $"A run recorded as started must be {nameof(RunLifecyclePhase.Running)}, not "
                    + $"{state.Phase}. Use {nameof(IRunLifecycleStore.TryMarkRunTerminalAsync)} to "
                    + "terminalize it.",
                nameof(state)
            );
        }
    }

    /// <summary>
    /// Finds a deferral by tool call id within a run's committed deferrals.
    /// </summary>
    /// <param name="state">The run to search.</param>
    /// <param name="toolCallId">The tool call to find.</param>
    /// <returns>Its index, or <c>-1</c> when the run has no such deferral.</returns>
    public static int IndexOfDeferral(RunLifecycleState state, string toolCallId)
    {
        for (var i = 0; i < state.DeferredToolCalls.Count; i++)
        {
            if (string.Equals(state.DeferredToolCalls[i].ToolCallId, toolCallId, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Decides what <see cref="IRunLifecycleStore.AttachDeferredChildRunAsync"/> should do with the
    /// record it found, so the stores cannot disagree about which child run stands.
    /// </summary>
    /// <param name="existing">The committed deferral.</param>
    /// <param name="childRunId">The child run the caller proposes.</param>
    /// <returns>
    /// The child run id the record names once the caller is done — null when the call cannot carry
    /// one — and whether reaching that state requires a write.
    /// </returns>
    public static (string? Standing, bool NeedsWrite) ClassifyChildRunAttach(
        DeferredToolCallRecord existing,
        string childRunId
    )
    {
        // An unresolved call has no continuation to name yet: naming one now would let a crash
        // recovery start a child for a result that never arrived.
        if (!existing.IsResolved)
        {
            return (null, false);
        }

        if (existing.ChildRunId == null)
        {
            return (childRunId, true);
        }

        // Already named, and the committed name stands whether it is this caller's or another's.
        // Reporting it back is what lets the caller adopt a continuation a dead process committed
        // to instead of starting a second one for the same result.
        return (existing.ChildRunId, false);
    }
}
