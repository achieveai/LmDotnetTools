namespace AchieveAi.LmDotnetTools.LmEval;

/// <summary>
/// One dimension of a rubric, with explicit anchors. Anchors are mandatory: an unanchored integer
/// scale is where score clustering and inter-judge disagreement come from.
/// </summary>
public sealed record RubricCriterion
{
    /// <summary>Stable identity of this criterion. Judges key their scores on it.</summary>
    public required string CriterionId { get; init; }

    /// <summary>What this dimension measures, in the judge's own prompt.</summary>
    public required string Description { get; init; }

    /// <summary>
    /// Score value -&gt; what that value means. At minimum the floor, midpoint and ceiling must be
    /// described.
    /// </summary>
    public required IReadOnlyDictionary<int, string> Anchors { get; init; }

    /// <summary>Relative weight of this criterion within the rubric. Defaults to 1.0.</summary>
    public double Weight { get; init; } = 1.0;
}

/// <summary>
/// A versioned, anchored scoring contract. Scores from different rubric versions are never pooled.
/// </summary>
public sealed record Rubric
{
    /// <summary>Stable identity of this rubric across versions.</summary>
    public required string RubricId { get; init; }

    /// <summary>Bumped on ANY text change. Scores from different rubric versions are never pooled.</summary>
    public required string RubricVersion { get; init; }

    /// <summary>The task type this rubric scores. Must match the candidate's task type.</summary>
    public required string TaskType { get; init; }

    /// <summary>Inclusive floor of the scoring scale.</summary>
    public required int MinScore { get; init; }

    /// <summary>Inclusive ceiling of the scoring scale.</summary>
    public required int MaxScore { get; init; }

    /// <summary>The scored dimensions, in a fixed presentation order.</summary>
    public required IReadOnlyList<RubricCriterion> Criteria { get; init; }

    /// <summary>
    /// Score at or above which the candidate is acceptable. Also the straddle boundary: two judges
    /// on opposite sides of it genuinely disagree, whereas two judges on the same side agree on the
    /// decision even when their numbers differ.
    /// </summary>
    public required int PassThreshold { get; init; }

    /// <summary>
    /// When true the judge's response schema puts reasoning before the score, so the model cannot
    /// emit a score it has not yet justified.
    /// </summary>
    public bool RequireReasoningBeforeScore { get; init; } = true;

    /// <summary>Sum of every criterion weight. Used to normalise a ballot's weighted score.</summary>
    public double TotalCriterionWeight => Criteria.Sum(c => c.Weight);
}
