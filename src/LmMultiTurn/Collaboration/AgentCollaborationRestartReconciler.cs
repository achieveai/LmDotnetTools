namespace AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;

/// <summary>
/// What one reconciliation pass found: which persisted agents this process is running, which it is not,
/// and which obligations died with the ones it is not.
/// </summary>
/// <param name="Rebound">
/// Agents named by the persisted set that are registered and live here, ordered as the set listed them.
/// </param>
/// <param name="Invalidated">Agents this pass turned into directory tombstones.</param>
/// <param name="AbandonedObligations">
/// Message identifiers whose target is in <paramref name="Invalidated"/> — obligations nobody can ever
/// answer now.
/// </param>
public readonly record struct RestartReconciliationReport(
    IReadOnlyList<string> Rebound,
    IReadOnlyList<string> Invalidated,
    IReadOnlyList<string> AbandonedObligations
)
{
    /// <summary>A pass that found nothing to do.</summary>
    public static RestartReconciliationReport Empty { get; } = new([], [], []);

    /// <summary>Whether this pass changed anything.</summary>
    public bool IsEmpty => Rebound.Count == 0 && Invalidated.Count == 0 && AbandonedObligations.Count == 0;

    /// <summary>
    /// One log line naming every bucket and its members.
    /// </summary>
    /// <remarks>
    /// This is the whole user-visible output of a restart. Nothing is pushed to the model and no turn is
    /// started, so if an operator cannot see what happened here, nobody can. Identifiers and counts
    /// only: names, roles and descriptions are model-authored text, and a log line is the wrong place to
    /// discover that. One line, because a multi-line trace is one a grep pulls a third of.
    /// </remarks>
    public string ToOperatorTrace()
    {
        if (IsEmpty)
        {
            return "Collaboration restart: nothing to reconcile.";
        }

        return "Collaboration restart: "
            + $"rebound {Rebound.Count} [{string.Join(", ", Rebound)}]; "
            + $"not live {Invalidated.Count} [{string.Join(", ", Invalidated)}]; "
            + $"abandoned {AbandonedObligations.Count} [{string.Join(", ", AbandonedObligations)}].";
    }
}

/// <summary>
/// Applies a persisted <see cref="AgentIdentityBindingSet"/> to a freshly built collaboration, so the
/// agents a previous process was talking to refuse as gone instead of as imaginary.
/// </summary>
/// <remarks>
/// <para>
/// The entire reconciliation is directory writes. There is no second lookup path, no probe a caller has
/// to remember to make, and no once-only field on a tool response: an agent that did not survive becomes
/// a tombstone in <see cref="AgentCollaborationDirectory"/>, and every existing caller inherits
/// <see cref="AgentDirectoryFailureCodes.TargetNotLive"/> from the <see cref="AgentCollaborationDirectory.Resolve"/>
/// it was already going through.
/// </para>
/// <para>
/// Nothing is delivered and no turn is started — see <see cref="Reconcile"/> for why that is a property
/// of this design rather than an omission.
/// </para>
/// </remarks>
public static class AgentCollaborationRestartReconciler
{
    /// <summary>
    /// Reconciles <paramref name="binding"/> against <paramref name="bundle"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Run this AFTER the agents this process owns have registered themselves — in particular after the
    /// root loop's constructor. Ordering matters in one direction only: a live registration always beats
    /// a tombstone in <see cref="AgentCollaborationDirectory.Resolve"/>, so reconciling early would still
    /// be corrected by the later registration, whereas reconciling against an empty directory reports the
    /// live root as a casualty in the operator trace.
    /// </para>
    /// <para>
    /// <b>Nothing is notified.</b> #673's delivery-failure notice tells a waiting SENDER that its message
    /// died, and after a restart that set is empty by construction: every sender named in the persisted
    /// set is either an agent that did not survive either — there is nobody left to tell — or the root
    /// itself, whose write endpoint enqueues on the loop's input channel and therefore starts a turn. A
    /// hydration that woke the model to announce its own restart would spend a model run before the user
    /// had typed a word, on every restart. The operator trace and the
    /// <see cref="AgentDirectoryFailureCodes.TargetNotLive"/> refusal the root gets the moment it tries
    /// to use one of these names carry the same information at the moment it is actually needed.
    /// </para>
    /// </remarks>
    /// <param name="bundle">The freshly built collaboration to reconcile into.</param>
    /// <param name="binding">The persisted set, or null when nothing was persisted.</param>
    /// <returns>What the pass found, for the operator trace.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="bundle"/> is null.</exception>
    public static RestartReconciliationReport Reconcile(
        AgentCollaborationBundle bundle,
        AgentIdentityBindingSet? binding
    )
    {
        ArgumentNullException.ThrowIfNull(bundle);

        // The scope half of every (scope, agent id) lookup, checked once here so the loop below cannot
        // apply a foreign root's rows one at a time. Sub-agent ids are ordinals minted per root, so a
        // set from another conversation names agents that do not exist here under ids and names that
        // do — the most plausible-looking wrong answer this code could give.
        if (
            binding is null
            || binding.IsEmpty
            || !string.Equals(binding.CollaborationId, bundle.CollaborationId, StringComparison.Ordinal)
        )
        {
            return RestartReconciliationReport.Empty;
        }

        var rebound = new List<string>();
        var invalidated = new List<string>();

        foreach (var record in binding.Agents)
        {
            // Live wins, whatever the row says. The row's status describes what the agent was doing when
            // the previous process ended; only this directory knows what is running now.
            if (bundle.Directory.FindById(record.AgentId) is { IsLive: true })
            {
                rebound.Add(record.AgentId);
                continue;
            }

            if (bundle.Directory.MarkInvalidated(record))
            {
                invalidated.Add(record.AgentId);
            }
        }

        // Only obligations owed BY an agent this pass just retired. One owed by a live agent is still
        // owed; one whose target was already tombstoned was already reported on an earlier pass.
        var abandoned = binding
            .OpenObligations.Where(obligation => invalidated.Contains(obligation.ToAgentId, StringComparer.Ordinal))
            .Select(obligation => obligation.MessageId)
            .ToList();

        return new RestartReconciliationReport(rebound, invalidated, abandoned);
    }
}
