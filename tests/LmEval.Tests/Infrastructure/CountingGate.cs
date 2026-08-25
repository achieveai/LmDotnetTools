namespace AchieveAi.LmDotnetTools.LmEval.Tests.Infrastructure;

/// <summary>
/// An <see cref="IGate"/> double that returns a fixed decision and counts how often it ran, so
/// "gates run in registration order and the first reject short-circuits" is asserted on the
/// counters rather than inferred from the verdict alone.
/// </summary>
internal sealed class CountingGate(string gateId, GateOutcome outcome, params string[] appliesTo)
    : IGate
{
    public string GateId { get; } = gateId;

    public IReadOnlySet<string> AppliesTo { get; } = appliesTo.ToHashSet(StringComparer.Ordinal);

    public int Calls { get; private set; }

    public ValueTask<GateDecision> EvaluateAsync(
        Candidate candidate,
        CancellationToken cancellationToken
    )
    {
        Calls++;
        return ValueTask.FromResult(new GateDecision(outcome, GateId, $"{GateId}:{outcome}"));
    }
}
