using System.Text;

namespace AchieveAi.LmDotnetTools.LmEval.Judges;

/// <summary>
/// Renders the user turn a <see cref="RubricJudge"/> sends.
/// <para>
/// Two bias controls are enforced here by construction rather than by instruction. First,
/// <b>nothing that identifies the generator is rendered</b> — not
/// <see cref="Candidate.ModelId"/>, not <see cref="Candidate.GeneratorFamily"/>, not
/// <see cref="Candidate.VariantId"/>, not <see cref="Candidate.Metadata"/> — because
/// self-preference is causally tied to self-recognition, so removing the cue is cheaper and more
/// reliable than defending against the effect. Second, the response schema puts
/// <c>reasoning</c> before <c>scores</c> when
/// <see cref="Rubric.RequireReasoningBeforeScore"/> is set, so a model streaming the object cannot
/// emit a score it has not yet justified.
/// </para>
/// </summary>
public static class RubricPromptRenderer
{
    /// <summary>Renders the judging turn for one candidate under one rubric.</summary>
    /// <param name="candidate">The candidate to judge.</param>
    /// <param name="rubric">The rubric to judge it against.</param>
    /// <param name="context">Per-invocation input, carrying the reference answer when there is one.</param>
    public static string Render(Candidate candidate, Rubric rubric, JudgeContext context)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(rubric);
        ArgumentNullException.ThrowIfNull(context);

        var builder = new StringBuilder();

        _ = builder.AppendLine("## Task").AppendLine(candidate.TaskInput).AppendLine();
        _ = builder.AppendLine("## Output under judgement").AppendLine(candidate.Content).AppendLine();

        if (!string.IsNullOrWhiteSpace(context.Reference))
        {
            _ = builder
                .AppendLine("## Independently produced answer for comparison")
                .AppendLine(context.Reference)
                .AppendLine();
        }

        _ = builder.AppendLine(
            $"## Rubric {rubric.RubricId} v{rubric.RubricVersion} — score each criterion "
                + $"as an integer from {rubric.MinScore} to {rubric.MaxScore}"
        );

        // Criterion order is the rubric's own, fixed under a versioned rubric. It is the one
        // residual ordering a pointwise judge could develop a preference over.
        foreach (var criterion in rubric.Criteria)
        {
            _ = builder.AppendLine().AppendLine($"### {criterion.CriterionId}");
            _ = builder.AppendLine(criterion.Description);
            foreach (var anchor in criterion.Anchors.OrderBy(a => a.Key))
            {
                _ = builder.AppendLine($"- {anchor.Key}: {anchor.Value}");
            }
        }

        _ = builder.AppendLine().AppendLine("## Reply format");
        _ = builder.AppendLine("Reply with a single JSON object and nothing else:");
        _ = builder.AppendLine(SchemaLine(rubric));
        _ = builder.AppendLine(
            rubric.RequireReasoningBeforeScore
                ? "State your reasoning before you score. Set \"confidence\" to how much you trust your "
                    + "own reading, and set \"abstain\" to true instead of guessing when you cannot judge."
                : "Set \"confidence\" to how much you trust your own reading, and set \"abstain\" to true "
                    + "instead of guessing when you cannot judge."
        );

        return builder.ToString();
    }

    private static string SchemaLine(Rubric rubric)
    {
        var scores = string.Join(
            ", ",
            rubric.Criteria.Select(c => $"\"{c.CriterionId}\": <int {rubric.MinScore}-{rubric.MaxScore}>")
        );
        var scoresField = $"\"scores\": {{{scores}}}";
        const string ReasoningField = "\"reasoning\": \"<why, in one or two sentences>\"";
        const string Tail = "\"confidence\": <0.0-1.0>, \"abstain\": false";

        return rubric.RequireReasoningBeforeScore
            ? $"{{{ReasoningField}, {scoresField}, {Tail}}}"
            : $"{{{scoresField}, {ReasoningField}, {Tail}}}";
    }
}
