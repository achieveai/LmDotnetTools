namespace AchieveAi.LmDotnetTools.LmEval;

/// <summary>One judge's opinion on one candidate. A Ballot is a claim, not a decision.</summary>
public sealed record Ballot
{
    /// <summary>Stable identity of the judge that cast this ballot.</summary>
    public required string JudgeId { get; init; }

    /// <summary>The model that cast it.</summary>
    public required string ModelId { get; init; }

    /// <summary>
    /// Recorded so aggregation can enforce family disjointness and detect generator/judge collision.
    /// </summary>
    public required string ModelFamily { get; init; }

    /// <summary>Per-criterion integer scores, keyed by <see cref="RubricCriterion.CriterionId"/>.</summary>
    public required IReadOnlyDictionary<string, int> CriterionScores { get; init; }

    /// <summary>
    /// The rubric-weighted average of <see cref="CriterionScores"/>, on the rubric's own scale.
    /// Because it is normalised by total weight it stays within
    /// [<see cref="Rubric.MinScore"/>, <see cref="Rubric.MaxScore"/>] and is directly comparable to
    /// <see cref="Rubric.PassThreshold"/>. A criterion the judge did not score makes the ballot
    /// invalid — it is a schema violation, not a zero.
    /// </summary>
    public required double WeightedScore { get; init; }

    /// <summary>The judge's justification, produced before the score.</summary>
    public required string Reasoning { get; init; }

    /// <summary>
    /// Self-reported confidence in [0,1]. Below <see cref="HarnessOptions.AbstainFloor"/> the
    /// ballot is recorded but excluded from the tally.
    /// </summary>
    public required double Confidence { get; init; }

    /// <summary>True when the judge declined to score. An abstention is DISTINCT from a zero.</summary>
    public required bool Abstained { get; init; }

    /// <summary>Why the judge abstained. Stable, non-sensitive text only.</summary>
    public string? AbstainReason { get; init; }

    /// <summary>
    /// The reliability weight aggregation applied, recorded so a past verdict stays auditable after
    /// the weights are refitted. <b>Null as a judge returns it</b> — a judge cannot know its own
    /// weight, and the snapshot does not exist until the aggregator runs. The invariant is:
    /// non-null on every ballot in <see cref="Verdict.Ballots"/>, null on every one in
    /// <see cref="Verdict.ExcludedBallots"/>.
    /// </summary>
    public double? AppliedWeight { get; init; }
}

/// <summary>Per-invocation input the harness supplies, not the judge implementation.</summary>
public sealed record JudgeContext
{
    /// <summary>
    /// An independently produced reference answer, when the corpus has one. The single largest
    /// accuracy lever available to a judge.
    /// </summary>
    public string? Reference { get; init; }
}

/// <summary>Something that turns a (candidate, rubric) pair into a <see cref="Ballot"/>.</summary>
public interface IJudge
{
    /// <summary>Stable identity of this judge, recorded on every ballot and fault.</summary>
    string JudgeId { get; }

    /// <summary>The model this judge runs on.</summary>
    string ModelId { get; }

    /// <summary>
    /// The model's family. Panel disjointness and generator-family exclusion are both decided on
    /// this value, compared case-insensitively.
    /// </summary>
    string ModelFamily { get; }

    /// <summary>Scores one candidate against one rubric.</summary>
    Task<Ballot> JudgeAsync(
        Candidate candidate,
        Rubric rubric,
        JudgeContext context,
        CancellationToken cancellationToken
    );
}
