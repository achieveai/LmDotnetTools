namespace AchieveAi.LmDotnetTools.LmEval;

/// <summary>
/// The complete option surface. Validated once at <see cref="JudgeGauntlet"/> construction.
/// </summary>
public sealed record HarnessOptions
{
    /// <summary>Ballots below this self-reported confidence are excluded from the tally.</summary>
    public double AbstainFloor { get; init; } = 0.34;

    /// <summary>
    /// Dispersion above which the verdict is flagged for human review. Null disables the alarm.
    /// </summary>
    public double? DispersionAlarm { get; init; }

    /// <summary>
    /// Optional stronger model that decides a straddle. Null means straddles terminate as
    /// <see cref="VerdictOutcome.Split"/>.
    /// <para>
    /// "Stronger" is not enforced here: it needs an ordering over model strength, and that ordering
    /// is a cascade concern. Configuring a weak arbiter is a configuration mistake this slice does
    /// not detect.
    /// </para>
    /// </summary>
    public IJudge? ArbiterJudge { get; init; }
}
