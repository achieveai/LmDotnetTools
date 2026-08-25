namespace AchieveAi.LmDotnetTools.LmEval;

/// <summary>
/// The complete option surface. Validated once at <see cref="JudgeGauntlet"/> construction.
/// </summary>
public sealed record HarnessOptions
{
    /// <summary>
    /// Ballots below this self-reported confidence are excluded from the tally. In [0,1], the range
    /// <see cref="Ballot.Confidence"/> is on.
    /// </summary>
    public double AbstainFloor { get; init; } = 0.34;

    /// <summary>
    /// Dispersion above which the verdict is flagged for human review, via
    /// <see cref="Verdict.DispersionAlarmed"/>. Null disables the alarm. Non-negative: it is
    /// compared against a population standard deviation, which never is.
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

    /// <summary>
    /// Throws unless every bound here is on the scale it is compared against.
    /// <para>
    /// <see cref="AbstainFloor"/> is the one that matters. Confidence is in [0,1] and the floor is
    /// compared straight against it, so writing the default as a percentage — <c>34</c> rather than
    /// <c>0.34</c> — puts EVERY ballot below the floor: every candidate becomes
    /// <see cref="VerdictOutcome.NoDecision"/> with a null score, and a whole corpus run produces
    /// nothing while reporting success. <c>-1</c> is the mirror image, silently disabling the
    /// filter. Neither fails anywhere near where it was configured, which is why it is checked
    /// once, here, at the boundary that already claims to validate options.
    /// </para>
    /// </summary>
    /// <param name="options">The options to validate.</param>
    /// <param name="paramName">Name of the caller's parameter, for the thrown exception.</param>
    /// <exception cref="ArgumentOutOfRangeException">A bound is NaN or off its scale.</exception>
    public static void Validate(HarnessOptions options, string paramName)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (
            double.IsNaN(options.AbstainFloor)
            || options.AbstainFloor < 0.0
            || options.AbstainFloor > 1.0
        )
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                options.AbstainFloor,
                $"{nameof(AbstainFloor)} is compared against a ballot's self-reported confidence, "
                    + "which is in [0,1], so the floor must be too. A floor above 1 excludes every "
                    + "ballot and turns a whole corpus run into no-decisions that report success; a "
                    + "floor below 0 disables the filter."
            );
        }

        if (
            options.DispersionAlarm is { } alarm
            && (double.IsNaN(alarm) || alarm < 0.0)
        )
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                alarm,
                $"{nameof(DispersionAlarm)} is compared against a population standard deviation, "
                    + "which is never negative, so a negative bound flags every verdict that has a "
                    + "dispersion at all. Null disables the alarm; that is the way to turn it off."
            );
        }
    }
}
