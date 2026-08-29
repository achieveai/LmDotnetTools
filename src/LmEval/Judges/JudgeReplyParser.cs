using System.Text.Json;

namespace AchieveAi.LmDotnetTools.LmEval.Judges;

/// <summary>
/// Turns a judge model's reply into a <see cref="Ballot"/>.
/// <para>
/// The canonical shape is <c>{"reasoning": string, "scores": {criterionId: int}, "confidence":
/// double}</c>, with reasoning first so a model streaming the object cannot emit a score it has not
/// yet justified. A single-criterion rubric additionally accepts the flat
/// <c>{"score": int, "rationale": string}</c> form, which is what Revobot's v1.0 judge prompt asks
/// for; on a multi-criterion rubric a bare scalar does not say which dimension it scored, so it is
/// a schema violation rather than a guess.
/// </para>
/// <para>
/// <b>Anything it cannot read becomes an abstention, never a zero.</b> That distinction is the
/// point: a malformed reply and a genuinely worst-possible candidate are different facts, and
/// conflating them silently corrupts every aggregate computed downstream.
/// </para>
/// </summary>
public static class JudgeReplyParser
{
    /// <summary>Recorded when the judge said, in the schema, that it declined to score.</summary>
    public const string DeclinedReason = "declined";

    /// <summary>Recorded when no JSON object could be read out of the reply at all.</summary>
    public const string UnparseableReason = "unparseable";

    /// <summary>Recorded when JSON was read but did not carry a score for every criterion.</summary>
    public const string SchemaViolationReason = "schema-violation";

    /// <summary>
    /// Parses one reply. Never throws on malformed input — an unreadable reply is an abstention
    /// carrying the raw text as its reasoning, which is the only form in which a caller can still
    /// see what the model actually said.
    /// </summary>
    /// <param name="reply">The judge model's raw reply text.</param>
    /// <param name="rubric">The rubric the reply was asked to score against.</param>
    /// <param name="judgeId">Identity to stamp on the ballot.</param>
    /// <param name="modelId">Model to stamp on the ballot.</param>
    /// <param name="modelFamily">Model family to stamp on the ballot.</param>
    public static Ballot Parse(string reply, Rubric rubric, string judgeId, string modelId, string modelFamily)
    {
        ArgumentNullException.ThrowIfNull(reply);
        ArgumentNullException.ThrowIfNull(rubric);

        var raw = reply.Trim();
        var json = ExtractJsonSpan(raw);
        if (json.Length == 0)
        {
            return Abstention(judgeId, modelId, modelFamily, raw, UnparseableReason);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return Abstention(judgeId, modelId, modelFamily, raw, UnparseableReason);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Abstention(judgeId, modelId, modelFamily, raw, UnparseableReason);
            }

            // The reasoning falls back to the raw text, which is what makes the abstain path
            // reproduce the legacy rationale exactly: a reply carrying a rationale but no score
            // keeps its rationale, and a reply carrying neither keeps the raw text.
            var reasoning = ReadString(root, "reasoning") ?? ReadString(root, "rationale") ?? raw;

            if (root.TryGetProperty("abstain", out var abstain) && abstain.ValueKind == JsonValueKind.True)
            {
                return Abstention(judgeId, modelId, modelFamily, reasoning, DeclinedReason);
            }

            if (!TryReadScores(root, rubric, out var scores))
            {
                return Abstention(judgeId, modelId, modelFamily, reasoning, SchemaViolationReason);
            }

            return new Ballot
            {
                JudgeId = judgeId,
                ModelId = modelId,
                ModelFamily = modelFamily,
                CriterionScores = scores,
                WeightedScore = WeightedScore(scores, rubric),
                Reasoning = reasoning,
                Confidence = ReadConfidence(root),
                Abstained = false,
            };
        }
    }

    /// <summary>
    /// The JSON span of <paramref name="text"/>: a fenced block's body if present, otherwise the
    /// substring between the first <c>{</c> and last <c>}</c>, otherwise empty. Models wrap their
    /// answer in prose and code fences whatever the prompt says, so this runs before any parse.
    /// </summary>
    public static string ExtractJsonSpan(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.Trim();

        var fenceStart = trimmed.IndexOf("```", StringComparison.Ordinal);
        if (fenceStart >= 0)
        {
            var bodyStart = trimmed.IndexOf('\n', fenceStart);
            var fenceEnd = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (bodyStart > 0 && fenceEnd > bodyStart)
            {
                trimmed = trimmed[(bodyStart + 1)..fenceEnd].Trim();
            }
        }

        var open = trimmed.IndexOf('{');
        var close = trimmed.LastIndexOf('}');
        return open >= 0 && close > open ? trimmed[open..(close + 1)] : string.Empty;
    }

    /// <summary>
    /// Every criterion must carry an integer score. A criterion the judge did not score makes the
    /// whole ballot invalid — a partially-answered ballot is a schema violation, and admitting it
    /// with the gaps zeroed would enter a real worst-case score the judge never gave.
    /// </summary>
    private static bool TryReadScores(JsonElement root, Rubric rubric, out IReadOnlyDictionary<string, int> scores)
    {
        scores = new Dictionary<string, int>();

        if (root.TryGetProperty("scores", out var scoreObject) && scoreObject.ValueKind == JsonValueKind.Object)
        {
            var parsed = new Dictionary<string, int>(rubric.Criteria.Count, StringComparer.Ordinal);
            foreach (var criterion in rubric.Criteria)
            {
                if (
                    !scoreObject.TryGetProperty(criterion.CriterionId, out var value)
                    || !TryReadInt(value, out var score)
                )
                {
                    return false;
                }

                parsed[criterion.CriterionId] = score;
            }

            scores = parsed;
            return true;
        }

        // The flat single-criterion form. On a multi-criterion rubric a scalar does not name the
        // dimension it scored, so it is refused rather than assigned to the first criterion.
        if (
            rubric.Criteria.Count == 1
            && root.TryGetProperty("score", out var flat)
            && TryReadInt(flat, out var flatScore)
        )
        {
            scores = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                [rubric.Criteria[0].CriterionId] = flatScore,
            };
            return true;
        }

        return false;
    }

    private static bool TryReadInt(JsonElement element, out int value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value);
    }

    private static string? ReadString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    /// <summary>
    /// Self-reported confidence, defaulting to full confidence when absent so a judge that answers
    /// the legacy schema is counted rather than silently dropped below the abstain floor.
    /// </summary>
    private static double ReadConfidence(JsonElement root) =>
        root.TryGetProperty("confidence", out var element)
        && element.ValueKind == JsonValueKind.Number
        && element.TryGetDouble(out var confidence)
            ? Math.Clamp(confidence, 0.0, 1.0)
            : 1.0;

    private static double WeightedScore(IReadOnlyDictionary<string, int> scores, Rubric rubric)
    {
        var totalWeight = rubric.TotalCriterionWeight;
        return totalWeight <= 0
            ? scores.Values.Average()
            : rubric.Criteria.Sum(c => c.Weight * scores[c.CriterionId]) / totalWeight;
    }

    private static Ballot Abstention(
        string judgeId,
        string modelId,
        string modelFamily,
        string reasoning,
        string reason
    ) =>
        new()
        {
            JudgeId = judgeId,
            ModelId = modelId,
            ModelFamily = modelFamily,
            CriterionScores = new Dictionary<string, int>(),
            WeightedScore = 0.0,
            Reasoning = reasoning,
            Confidence = 0.0,
            Abstained = true,
            AbstainReason = reason,
        };
}
