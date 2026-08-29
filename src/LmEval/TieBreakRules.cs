using System.Diagnostics.CodeAnalysis;

namespace AchieveAi.LmDotnetTools.LmEval;

/// <summary>
/// The closed vocabulary of <see cref="Verdict.TieBreakRule"/>, and the only formatter and parser
/// for its one parameterised member.
/// <para>
/// It is public because the rule is a <b>persisted</b> field that downstream readers segment on: the
/// eval runner derives its headline straddle-rate diagnostic from it, and the experiment record
/// stores it verbatim. A vocabulary that only the component writing it can name forces every reader
/// to hardcode the literals, and a hardcoded literal is how a reader and a writer drift apart
/// without a compiler noticing.
/// </para>
/// </summary>
public static class TieBreakRules
{
    /// <summary>Recorded when both counted ballots landed on the same side of the threshold.</summary>
    public const string Consensus = "consensus";

    /// <summary>Recorded on a straddle nobody resolved.</summary>
    public const string SplitUnresolved = "split:unresolved";

    /// <summary>Recorded when exactly one ballot was counted.</summary>
    public const string SingleJudge = "single-judge";

    /// <summary>Recorded when no ballot survived the abstain filter, or no judge was eligible.</summary>
    public const string NoDecision = "no-decision";

    /// <summary>Recorded when a gate short-circuited the candidate before any judge ran.</summary>
    public const string GateReject = "gate-reject";

    /// <summary>The prefix every arbiter-resolved rule carries.</summary>
    private const string ArbiterPrefix = "arbiter:";

    /// <summary>
    /// Formats the rule recorded when the arbiter decided a straddle. The arbiter's family is part
    /// of the string because the deciding voice, and which family it came from, is the whole reason
    /// an arbiter is preferred over a third peer judge.
    /// </summary>
    /// <param name="judgeId">The arbiter's stable id.</param>
    /// <param name="modelFamily">The arbiter's model family.</param>
    public static string Arbiter(string judgeId, string modelFamily) => $"{ArbiterPrefix}{judgeId}:{modelFamily}";

    /// <summary>True when this rule records an arbiter-resolved straddle.</summary>
    /// <param name="tieBreakRule">The rule recorded on a verdict.</param>
    public static bool IsArbiterResolved(string? tieBreakRule) =>
        tieBreakRule is not null && tieBreakRule.StartsWith(ArbiterPrefix, StringComparison.Ordinal);

    /// <summary>
    /// True when this rule records a <b>straddle</b> — the panel ran and its two counted ballots
    /// landed on opposite sides of the pass threshold. That covers both arms: the arbiter resolved
    /// it, or nobody did. It is deliberately NOT the same question as "did the verdict end up
    /// Split", because an arbiter-resolved straddle ends up Pass or Fail while still having been a
    /// genuine disagreement — and the straddle rate is a measure of disagreement, not of outcome.
    /// </summary>
    /// <param name="tieBreakRule">The rule recorded on a verdict.</param>
    public static bool IsStraddle(string? tieBreakRule) =>
        string.Equals(tieBreakRule, SplitUnresolved, StringComparison.Ordinal) || IsArbiterResolved(tieBreakRule);

    /// <summary>
    /// Recovers the arbiter's identity from a rule <see cref="Arbiter"/> produced. Returns false
    /// for every other member of the vocabulary.
    /// </summary>
    /// <param name="tieBreakRule">The rule recorded on a verdict.</param>
    /// <param name="judgeId">The arbiter's id, when this rule is an arbiter rule.</param>
    /// <param name="modelFamily">The arbiter's family, when this rule is an arbiter rule.</param>
    public static bool TryParseArbiter(
        string? tieBreakRule,
        [NotNullWhen(true)] out string? judgeId,
        [NotNullWhen(true)] out string? modelFamily
    )
    {
        judgeId = null;
        modelFamily = null;

        if (!IsArbiterResolved(tieBreakRule))
        {
            return false;
        }

        var body = tieBreakRule![ArbiterPrefix.Length..];

        // The family is the LAST segment, not the second: a judge id is host-supplied and may
        // itself contain a colon, whereas the formatter appends exactly one family on the end.
        // Splitting from the left would silently truncate such an id and mislabel its family.
        var separator = body.LastIndexOf(':');
        if (separator <= 0 || separator == body.Length - 1)
        {
            return false;
        }

        judgeId = body[..separator];
        modelFamily = body[(separator + 1)..];
        return true;
    }
}
