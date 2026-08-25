using AchieveAi.LmDotnetTools.LmEval.Running;

namespace AchieveAi.LmDotnetTools.LmEval.Gates;

/// <summary>
/// Rejects a candidate whose content falls outside a configured length band.
/// <para>
/// This is the cheapest available defence against verbosity bias: a "repetitive list" attack —
/// restating content at greater length with nothing new — fooled two of three frontier judges more
/// than nine times out of ten. A candidate outside the band never reaches a model at all, so the
/// bias has nothing to act on.
/// </para>
/// </summary>
public sealed class LengthBoundsGate : GateBase, IConfigurationFingerprint
{
    /// <summary>The stable id this gate records on every decision.</summary>
    public const string Id = "length-bounds";

    private readonly int _minimumLength;
    private readonly int _maximumLength;

    /// <summary>Creates the gate over an inclusive band.</summary>
    /// <param name="minimumLength">Inclusive floor, in characters.</param>
    /// <param name="maximumLength">Inclusive ceiling, in characters.</param>
    /// <param name="appliesTo">Task types this gate applies to; empty means all.</param>
    public LengthBoundsGate(
        int minimumLength,
        int maximumLength,
        IEnumerable<string>? appliesTo = null
    )
        : base(Id, appliesTo)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumLength);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumLength, minimumLength);
        _minimumLength = minimumLength;
        _maximumLength = maximumLength;
    }

    /// <summary>
    /// The band, which is the whole of this gate's score-affecting configuration. Two gates
    /// sharing <see cref="Id"/> over different bands reject different candidates and so produce
    /// different pass rates; the band is what tells them apart.
    /// </summary>
    public string? ConfigurationFingerprint => $"min={_minimumLength};max={_maximumLength}";

    /// <inheritdoc />
    protected override GateDecision Evaluate(Candidate candidate)
    {
        var length = candidate.Content.Length;
        return length < _minimumLength || length > _maximumLength
            ? GateDecision.Reject(
                Id,
                $"content length {length} is outside the band [{_minimumLength},{_maximumLength}]"
            )
            : GateDecision.Pass(
                Id,
                $"content length {length} is inside the band [{_minimumLength},{_maximumLength}]"
            );
    }
}
