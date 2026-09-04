using CodeReviewDaemon.Sample.Persistence.Models;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// What one <c>DiscussionFollowUp</c> turn decided (design §5.4). <see cref="IsRelevant"/> is the
/// agent's own answer to "do I have anything to add"; a false there is a deliberate no-op, not a
/// failure, and the executor turns it into a durable record rather than a retry.
/// </summary>
/// <param name="IsRelevant">Whether the round has anything to say at all.</param>
/// <param name="QuestionInterpretations">Which open questions the round believes are now settled.</param>
/// <param name="Actions">What it proposes to do. The executor gates every one of them.</param>
/// <param name="Observations">What it wants recorded, whether or not it acts.</param>
/// <param name="BroadReviewNeeded">
/// The round believes a wider code review is now warranted. A FLAG, never an action: §5.4 requires the
/// need to be recorded, and forbids the round from silently turning itself into a code review on an
/// unchanged head.
/// </param>
/// <param name="BroadReviewReason">Why, when <paramref name="BroadReviewNeeded"/> is set.</param>
/// <param name="Prose">Whatever the turn wrote outside the structured block.</param>
internal sealed record DiscussionDecision(
    bool IsRelevant,
    IReadOnlyList<QuestionInterpretation> QuestionInterpretations,
    IReadOnlyList<PlannedReviewAction> Actions,
    IReadOnlyList<RoundObservationDraft> Observations,
    bool BroadReviewNeeded = false,
    string? BroadReviewReason = null,
    string Prose = ""
);

/// <summary>
/// The round's reading of one open question. It is a CLAIM, not a conclusion: the executor accepts it
/// only if every cited comment appears in the mechanically correlated candidate set and the
/// interpretation cites evidence (design §5.3).
/// </summary>
/// <param name="QuestionId">The question being interpreted, as <see cref="ClarificationQuestion.Id"/>.</param>
/// <param name="State">What the round believes the question's state now is.</param>
/// <param name="CandidateCommentIds">The correlated comments the round read to decide that.</param>
/// <param name="Evidence">The audit source records backing the interpretation.</param>
internal sealed record QuestionInterpretation(
    string QuestionId,
    ClarificationQuestionState State,
    IReadOnlyList<string> CandidateCommentIds,
    IReadOnlyList<AuditSourceReference> Evidence
);

/// <summary>
/// Parses the single <c>discussion-decision</c> fenced YAML block a follow-up turn appends to its prose.
/// <para>
/// A separate protocol from the synthesis turn's <c>review-actions</c> fence, deliberately. That fence
/// has three kinds and no notion of a question interpretation, a relevance flag or a broad-review need;
/// bending it to carry them would change a shipped, separately-owned contract in order to serve a
/// different round intent. One protocol per intent is the cheaper coupling.
/// </para>
/// <para>
/// Never throws on model CONTENT. Every content failure — no fence, two fences, malformed YAML, an
/// unknown kind, an unusable evidence ref — degrades to a decision plus a
/// <see cref="ObservationKind.Gap"/> observation, so an unreadable answer leaves a durable trace instead
/// of looking like a round that calmly decided to do nothing.
/// </para>
/// </summary>
internal static class DiscussionDecisionParser
{
    private const string OpenFenceMarker = "```discussion-decision";
    private const string CloseFenceMarker = "```";

    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Parses <paramref name="response"/>. A null <paramref name="response"/> stays a caller error — it
    /// is not malformed model content, it is a bug in whoever called this.
    /// </summary>
    public static DiscussionDecision Parse(string response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var lines = response.Split('\n');
        var openIndices = new List<int>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim() == OpenFenceMarker)
            {
                openIndices.Add(i);
            }
        }

        if (openIndices.Count == 0)
        {
            // No structured block at all: the turn said something in prose and proposed nothing. That is
            // a legitimate no-op, not a gap — there is nothing unreadable about it.
            return NoOp(response.Trim());
        }

        var openIdx = openIndices[0];
        var closeIdx = FindClosingIndex(lines, openIdx);
        var prose = ProseOutside(lines, openIdx, closeIdx);

        if (openIndices.Count > 1)
        {
            return Unreadable(prose, "the turn emitted more than one discussion-decision block");
        }

        if (closeIdx < 0)
        {
            return Unreadable(prose, "the discussion-decision block was never closed");
        }

        RawDecision? raw;
        try
        {
            raw = YamlDeserializer.Deserialize<RawDecision>(string.Join('\n', lines[(openIdx + 1)..closeIdx]));
        }
        catch (YamlException ex)
        {
            return Unreadable(prose, $"the discussion-decision block is not valid YAML: {ex.Message}");
        }

        if (raw is null)
        {
            return NoOp(prose);
        }

        var gaps = new List<RoundObservationDraft>();
        return new DiscussionDecision(
            raw.Relevant,
            ReadQuestions(raw.Questions, gaps),
            RejectActions(raw.Actions, gaps),
            [.. ReadObservations(raw.Observations, gaps), .. gaps],
            raw.BroadReviewNeeded,
            string.IsNullOrWhiteSpace(raw.BroadReviewReason) ? null : raw.BroadReviewReason,
            prose
        );
    }

    /// <summary>A readable turn that proposed nothing.</summary>
    private static DiscussionDecision NoOp(string prose) => new(false, [], [], [], Prose: prose);

    /// <summary>
    /// A turn whose structured block could not be read. Irrelevant (nothing may be acted on) but NOT
    /// silent: the gap is what tells an operator the difference between "chose to do nothing" and
    /// "tried to say something the daemon could not parse".
    /// </summary>
    private static DiscussionDecision Unreadable(string prose, string reason) =>
        new(false, [], [], [new RoundObservationDraft(ObservationKind.Gap, reason)], Prose: prose);

    private static IReadOnlyList<QuestionInterpretation> ReadQuestions(
        List<RawQuestion?>? raw,
        List<RoundObservationDraft> gaps
    )
    {
        if (raw is null)
        {
            return [];
        }

        var questions = new List<QuestionInterpretation>();
        foreach (var item in raw)
        {
            if (item is null)
            {
                gaps.Add(new RoundObservationDraft(ObservationKind.Gap, "a question interpretation was not a mapping"));
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.QuestionId))
            {
                gaps.Add(
                    new RoundObservationDraft(ObservationKind.Gap, "a question interpretation named no questionId")
                );
                continue;
            }

            var id = item.QuestionId.Trim();
            if (!TryParseName<ClarificationQuestionState>(item.State, out var state))
            {
                gaps.Add(
                    new RoundObservationDraft(
                        ObservationKind.Gap,
                        $"question {id} was given the unknown state '{item.State ?? "(missing)"}'"
                    )
                );
                continue;
            }

            questions.Add(
                new QuestionInterpretation(
                    id,
                    state,
                    item.Candidates ?? [],
                    ReadEvidence(item.Evidence, gaps, $"question {id}")
                )
            );
        }

        return questions;
    }

    private static IReadOnlyList<PlannedReviewAction> RejectActions(
        List<RawAction?>? raw,
        List<RoundObservationDraft> gaps
    )
    {
        if (raw is not null)
        {
            gaps.Add(
                new RoundObservationDraft(
                    ObservationKind.Gap,
                    "the discussion-decision block included publication actions; use the parent-only typed publication tools instead"
                )
            );
        }

        return [];
    }

    private static IReadOnlyList<RoundObservationDraft> ReadObservations(
        List<RawObservation?>? raw,
        List<RoundObservationDraft> gaps
    )
    {
        if (raw is null)
        {
            return [];
        }

        var observations = new List<RoundObservationDraft>();
        foreach (var item in raw)
        {
            // A null entry and an unreadable kind are DIFFERENT failures. Reporting a bare "-" as an
            // unknown kind sends an operator looking for a kind token that was never written, and hides
            // the shape error that actually happened.
            if (item is null)
            {
                gaps.Add(new RoundObservationDraft(ObservationKind.Gap, "an observation was not a mapping"));
                continue;
            }

            if (!TryParseName<ObservationKind>(item.Kind, out var kind))
            {
                gaps.Add(
                    new RoundObservationDraft(
                        ObservationKind.Gap,
                        $"an observation named the unknown kind '{item.Kind ?? "(missing)"}' and was dropped"
                    )
                );
                continue;
            }

            observations.Add(
                new RoundObservationDraft(
                    kind,
                    item.Summary ?? string.Empty,
                    ReadEvidence(item.Evidence, gaps, $"the {item.Kind} observation")
                )
            );
        }

        return observations;
    }

    /// <summary>
    /// Parses <paramref name="value"/> as an enum member NAME.
    /// <para>
    /// <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> on its own answers <c>true</c> for three
    /// things this protocol never offers: an undefined ordinal (<c>"99"</c> becomes
    /// <c>(ObservationKind)99</c>), a defined one nothing writes by number, and a comma list folded
    /// bitwise into an unrelated member (<c>"Open,Answered"</c> becomes <c>Answered</c>). Each of those
    /// reads downstream as a value the model never named — and one of them is stored. Requiring letters
    /// admits every member of both enums and nothing else.
    /// </para>
    /// </summary>
    private static bool TryParseName<TEnum>(string? value, out TEnum parsed)
        where TEnum : struct, Enum
    {
        parsed = default;
        var name = value?.Trim();
        return !string.IsNullOrEmpty(name) && name.All(char.IsLetter) && Enum.TryParse(name, true, out parsed);
    }

    /// <summary>
    /// Reads <c>"&lt;sourceRecordId&gt;:&lt;contentSha256&gt;"</c> refs. A ref that does not carry BOTH is
    /// dropped rather than coerced: an id with no hash, or a hash with no id, cannot be verified later,
    /// and a citation that cannot be checked is worse than an absent one because it reads as checked.
    /// <para>
    /// Every drop leaves a <see cref="ObservationKind.Gap"/> naming <paramref name="citedBy"/> and the ref.
    /// Dropping silently would make a cited item indistinguishable from an uncited one — the operator sees
    /// an action with no evidence and cannot tell whether the agent cited nothing or cited something the
    /// daemon could not use, which is the difference between an unsupported claim and a lost citation.
    /// </para>
    /// <para>
    /// Splits on the FIRST colon only, because an <c>audit_source_record</c> id is a free-form string
    /// while the hash is fixed-shape hex — so a colon inside the ref belongs to the id, never to the hash.
    /// </para>
    /// </summary>
    private static IReadOnlyList<AuditSourceReference> ReadEvidence(
        List<string?>? raw,
        List<RoundObservationDraft> gaps,
        string citedBy
    )
    {
        if (raw is null)
        {
            return [];
        }

        var evidence = new List<AuditSourceReference>();
        foreach (var item in raw)
        {
            var separator = item?.IndexOf(':') ?? -1;
            if (item is null || separator <= 0 || separator == item.Length - 1)
            {
                gaps.Add(
                    new RoundObservationDraft(
                        ObservationKind.Gap,
                        $"{citedBy} cited the unusable evidence ref '{item ?? "(null)"}', which names no "
                            + "source record and hash, and it was dropped"
                    )
                );
                continue;
            }

            evidence.Add(new AuditSourceReference(item[..separator], item[(separator + 1)..]));
        }

        return evidence;
    }

    /// <summary>
    /// Finds the line closing the block opened at <paramref name="afterIndex"/>. Indentation is ignored on
    /// both markers: a model that nests the fence under a list item or a quote indents the whole block, and
    /// a column-zero match would read that formatting choice as "the block was never closed".
    /// </summary>
    private static int FindClosingIndex(string[] lines, int afterIndex)
    {
        for (var i = afterIndex + 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == CloseFenceMarker)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The prose either side of the fence. An unterminated fence swallows everything after it.</summary>
    private static string ProseOutside(string[] lines, int openIdx, int closeIdx)
    {
        var kept = lines[..openIdx].ToList();
        if (closeIdx >= 0 && closeIdx + 1 < lines.Length)
        {
            kept.AddRange(lines[(closeIdx + 1)..]);
        }

        return string.Join('\n', kept).Trim();
    }

    /// <summary>
    /// Mutable YAML target. Numeric fields are <c>string?</c> for the same reason
    /// <see cref="Orchestration.ReviewActionsParser"/>'s are: typing them as numbers makes YamlDotNet
    /// throw while deserializing the WHOLE document the moment one item supplies a non-numeric value,
    /// turning "drop that one item" into an incorrect whole-block failure.
    /// </summary>
    private sealed class RawDecision
    {
        public bool Relevant { get; set; }

        public bool BroadReviewNeeded { get; set; }

        public string? BroadReviewReason { get; set; }

        public List<RawQuestion?>? Questions { get; set; }

        public List<RawAction?>? Actions { get; set; }

        public List<RawObservation?>? Observations { get; set; }
    }

    private sealed class RawQuestion
    {
        public string? QuestionId { get; set; }

        public string? State { get; set; }

        public List<string>? Candidates { get; set; }

        public List<string?>? Evidence { get; set; }
    }

    private sealed class RawAction
    {
        public string? Kind { get; set; }

        public string? Body { get; set; }

        public string? Ref { get; set; }

        public string? Path { get; set; }

        public string? Line { get; set; }

        public List<string>? Causes { get; set; }

        public List<string?>? Evidence { get; set; }
    }

    private sealed class RawObservation
    {
        public string? Kind { get; set; }

        public string? Summary { get; set; }

        public List<string?>? Evidence { get; set; }
    }
}
