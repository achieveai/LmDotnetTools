namespace AchieveAi.LmDotnetTools.LmEval;

/// <summary>The outcome of validating a rubric, with every problem found rather than the first.</summary>
/// <param name="IsValid">True when no problem was found.</param>
/// <param name="Errors">One stable, human-readable line per problem.</param>
public sealed record RubricValidationResult(bool IsValid, IReadOnlyList<string> Errors);

/// <summary>
/// Checks a rubric against the drafting rules the scoring contract depends on.
/// <para>
/// The load-bearing rule is the anti-verbosity one: <b>a rubric must not reward length.</b> A
/// criterion whose text asks for a "comprehensive" or "thorough" answer, with no anchor capping
/// what that means, is an instruction to prefer the longer candidate — which is precisely the
/// failure a length gate and a score-versus-length regression are there to catch after the fact.
/// Catching it at drafting time is cheaper than catching it in the data. The check is crude on
/// purpose: it is aimed at the common drafting mistake, not at adversarial phrasing.
/// </para>
/// <para>
/// The remaining rules are the structural contract of §2.5: anchors describing at least the floor,
/// midpoint and ceiling; unique criterion ids; positive weights; a pass threshold inside the scale.
/// </para>
/// </summary>
public static class RubricValidator
{
    /// <summary>
    /// Phrases that reward volume. Their presence is not itself an error — it is an error only when
    /// no anchor caps what they mean.
    /// </summary>
    public static readonly IReadOnlyList<string> RewardForVolumeTerms =
    [
        "comprehensive",
        "thorough",
        "detailed",
        "in depth",
        "in-depth",
        "exhaustive",
    ];

    /// <summary>
    /// Phrases an anchor can use to cap a reward-for-volume term — a bound on what counts, rather
    /// than an invitation to write more.
    /// </summary>
    public static readonly IReadOnlyList<string> CappingTerms =
    [
        "no more than",
        "at most",
        "without repeating",
        "without restating",
        "redundant",
        "repetition",
        "length is not",
        "regardless of length",
        "penalise",
        "penalize",
    ];

    /// <summary>Validates one rubric, returning every problem it found.</summary>
    /// <param name="rubric">The rubric to validate.</param>
    public static RubricValidationResult Validate(Rubric rubric)
    {
        ArgumentNullException.ThrowIfNull(rubric);

        var errors = new List<string>();

        if (rubric.MaxScore <= rubric.MinScore)
        {
            errors.Add($"the scale [{rubric.MinScore},{rubric.MaxScore}] is empty: MaxScore must exceed MinScore");
        }

        if (rubric.PassThreshold < rubric.MinScore || rubric.PassThreshold > rubric.MaxScore)
        {
            errors.Add(
                $"PassThreshold {rubric.PassThreshold} is outside the scale " + $"[{rubric.MinScore},{rubric.MaxScore}]"
            );
        }

        if (rubric.Criteria.Count == 0)
        {
            errors.Add("a rubric must carry at least one criterion");
        }

        var duplicates = rubric
            .Criteria.GroupBy(c => c.CriterionId, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);
        errors.AddRange(duplicates.Select(id => $"criterion id '{id}' appears more than once"));

        foreach (var criterion in rubric.Criteria)
        {
            errors.AddRange(ValidateCriterion(criterion, rubric));
        }

        return new RubricValidationResult(errors.Count == 0, errors);
    }

    private static IEnumerable<string> ValidateCriterion(RubricCriterion criterion, Rubric rubric)
    {
        if (criterion.Weight <= 0)
        {
            yield return $"criterion '{criterion.CriterionId}' has a non-positive weight "
                + $"{criterion.Weight}; a criterion that cannot move the score is not a criterion";
        }

        // §2.5 — at minimum the floor, midpoint and ceiling must be described. An unanchored
        // integer scale is where score clustering and inter-judge disagreement come from.
        var midpoint = (rubric.MinScore + rubric.MaxScore) / 2;
        foreach (var required in new[] { rubric.MinScore, midpoint, rubric.MaxScore }.Distinct())
        {
            if (!criterion.Anchors.ContainsKey(required))
            {
                yield return $"criterion '{criterion.CriterionId}' has no anchor describing " + $"score {required}";
            }
        }

        var volumeTerm = RewardForVolumeTerms.FirstOrDefault(term =>
            criterion.Description.Contains(term, StringComparison.OrdinalIgnoreCase)
        );
        if (
            volumeTerm is not null
            && !criterion.Anchors.Values.Any(anchor =>
                CappingTerms.Any(cap => anchor.Contains(cap, StringComparison.OrdinalIgnoreCase))
            )
        )
        {
            yield return $"criterion '{criterion.CriterionId}' rewards volume ('{volumeTerm}') with "
                + "no anchor capping it, so it instructs the judge to prefer the longer candidate";
        }
    }
}
