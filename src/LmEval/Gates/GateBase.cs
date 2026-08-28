namespace AchieveAi.LmDotnetTools.LmEval.Gates;

/// <summary>
/// Shared plumbing for the starter gates: the task-type applicability set and the synchronous
/// decision shape. Gates are pure predicates, so the async surface is a formality every one of them
/// would otherwise have to spell out identically.
/// </summary>
public abstract class GateBase : IGate
{
    /// <summary>Creates a gate applying to the given task types; empty means all of them.</summary>
    /// <param name="gateId">Stable identity, recorded on every decision.</param>
    /// <param name="appliesTo">Task types this gate applies to; empty means all.</param>
    protected GateBase(string gateId, IEnumerable<string>? appliesTo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gateId);
        GateId = gateId;
        AppliesTo = (appliesTo ?? []).ToHashSet(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public string GateId { get; }

    /// <inheritdoc />
    public IReadOnlySet<string> AppliesTo { get; }

    /// <inheritdoc />
    public ValueTask<GateDecision> EvaluateAsync(Candidate candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Evaluate(candidate));
    }

    /// <summary>
    /// The deterministic predicate. Its reason reaches persistence, so it must carry stable,
    /// non-sensitive text — measurements and bounds, never the candidate's own content.
    /// </summary>
    /// <param name="candidate">The candidate to evaluate.</param>
    protected abstract GateDecision Evaluate(Candidate candidate);
}
