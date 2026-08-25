namespace AchieveAi.LmDotnetTools.LmEval;

/// <summary>
/// What a gate concluded. Three-valued on purpose: a two-valued gate forces an infrastructure
/// failure to be encoded either as a pass (silently unchecked) or a reject (a false negative that
/// looks like a real finding).
/// </summary>
public enum GateOutcome
{
    /// <summary>The candidate cleared this gate.</summary>
    Pass,

    /// <summary>The candidate failed outright. The first reject short-circuits; no judge runs.</summary>
    Reject,

    /// <summary>The gate could not run. Neither a pass nor a reject.</summary>
    Inconclusive,
}

/// <summary>
/// One gate's decision, with a mandatory human-readable reason.
/// <para>
/// <paramref name="Reason"/> reaches persistence, so it is held to the same rail as every other
/// persisted diagnostic string: stable, non-sensitive text only — never a raw payload, a candidate
/// excerpt, or an exception message.
/// </para>
/// </summary>
/// <param name="Outcome">What the gate concluded.</param>
/// <param name="GateId">Which gate concluded it.</param>
/// <param name="Reason">Stable, non-sensitive text explaining the outcome.</param>
public sealed record GateDecision(GateOutcome Outcome, string GateId, string Reason)
{
    /// <summary>True when the candidate cleared this gate.</summary>
    public bool IsPass => Outcome == GateOutcome.Pass;

    /// <summary>The candidate cleared this gate.</summary>
    public static GateDecision Pass(string gateId, string reason) =>
        new(GateOutcome.Pass, gateId, reason);

    /// <summary>The candidate failed outright. Short-circuits the gauntlet before any judge runs.</summary>
    public static GateDecision Reject(string gateId, string reason) =>
        new(GateOutcome.Reject, gateId, reason);

    /// <summary>
    /// The gate could not run (a tool missing, a checkout absent). NOT a pass and NOT a reject: it
    /// escalates to the judge and is recorded, so an infrastructure failure can never be mistaken
    /// for a clean bill of health.
    /// </summary>
    public static GateDecision Inconclusive(string gateId, string reason) =>
        new(GateOutcome.Inconclusive, gateId, reason);
}

/// <summary>
/// A deterministic, LLM-free predicate over a <see cref="Candidate"/>. It costs no tokens, which
/// is why it runs first: the cheapest way to reject an outright failure is to never ask a model
/// about it.
/// </summary>
public interface IGate
{
    /// <summary>Stable identity of this gate, recorded on every decision it makes.</summary>
    string GateId { get; }

    /// <summary>Task types this gate applies to; empty means all.</summary>
    IReadOnlySet<string> AppliesTo { get; }

    /// <summary>Evaluates the candidate. Must not call a model and must not perform network I/O.</summary>
    ValueTask<GateDecision> EvaluateAsync(Candidate candidate, CancellationToken cancellationToken);
}
