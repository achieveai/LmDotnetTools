using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// One external comment inside a round's FROZEN activity window, or one ancestor pulled in to make such
/// a comment interpretable (design §5.4). Provider-neutral: GitHub review comments, GitHub issue
/// comments and ADO PR thread comments all normalize onto this shape.
/// </summary>
/// <param name="CommentId">Provider-native comment id, used to cite and correlate the comment.</param>
/// <param name="ProviderTargetId">
/// Host-supplied opaque publication target. Provider-qualified because Azure DevOps comment ids repeat across
/// threads. The model may pass this value back verbatim but must never construct one.
/// </param>
/// <param name="ParentCommentId">The comment this one directly replies to, when the provider models one.</param>
/// <param name="ThreadId">The provider thread this comment belongs to, when the provider models threads.</param>
/// <param name="Author">Who wrote it. Used for attribution in the prompt, never for correlation.</param>
/// <param name="Body">Exact wording as the provider returned it.</param>
/// <param name="Permalink">Canonical URL, when the provider issues one.</param>
/// <param name="Path">Diff path the comment is anchored to, for an inline comment.</param>
/// <param name="Line">Diff line the comment is anchored to, for an inline comment.</param>
/// <param name="Source">The audit record this comment was read from, so anything citing it is verifiable.</param>
internal sealed record DiscussionComment(
    string CommentId,
    string Author,
    string Body,
    AuditSourceReference Source,
    string? ProviderTargetId = null,
    string? ParentCommentId = null,
    string? ThreadId = null,
    string? Permalink = null,
    string? Path = null,
    int? Line = null
);

/// <summary>
/// A clarification question this PR is still waiting on (design §5.1), reduced to what a follow-up round
/// needs in order to recognize an answer: the exact wording, where it was asked, and the conclusion that
/// stays withheld until it is answered.
/// </summary>
/// <param name="QuestionId">
/// Stable question id — <see cref="ClarificationQuestion.Id"/> verbatim. A string, not a number, because
/// the store's ids are free-form (<c>q-1</c>, a guid): parsing one into a number would fail on the ids the
/// daemon actually mints, and re-formatting it would produce a token that matches nothing a human wrote.
/// </param>
/// <param name="Wording">Exactly what was asked.</param>
/// <param name="ProviderTargetRef">The comment the question was published as, when it has one.</param>
/// <param name="ProviderThreadId">The thread that comment opened, when the provider models threads.</param>
/// <param name="ProviderPermalink">Permalink to the published question, when it has one.</param>
/// <param name="ActionId">The typed action that published it, when it has one.</param>
/// <param name="WithheldConclusion">What the review declined to state until this is answered.</param>
internal sealed record OpenClarificationQuestion(
    string QuestionId,
    string Wording,
    string WithheldConclusion,
    string? ProviderTargetRef = null,
    string? ProviderThreadId = null,
    string? ProviderPermalink = null,
    string? ActionId = null
);

/// <summary>
/// The MECHANICAL reason a comment was collected as a candidate answer (design §5.3). Every member is
/// something a machine can decide by string/graph comparison alone. There is deliberately no member for
/// arrival order, thread closure, or reaction: those are the inferences §5.3 forbids, and a signal that
/// does not exist cannot be produced by accident.
/// </summary>
internal enum CandidateSignal
{
    /// <summary>The comment sits in the same provider thread the question was asked in.</summary>
    ThreadAncestry = 0,

    /// <summary>The comment directly replies to the comment the question was published as.</summary>
    DirectReply,

    /// <summary>The comment's text contains the question's stable id.</summary>
    QuestionIdReference,

    /// <summary>The comment's text contains the publishing action's id.</summary>
    ActionIdReference,

    /// <summary>The comment's text contains the question's permalink.</summary>
    Permalink,

    /// <summary>The comment's text mentions the question's provider ref (e.g. <c>#123</c>).</summary>
    Mention,
}

/// <summary>
/// A comment mechanically linked to a question. It asserts CORRELATION ONLY — that a machine could draw
/// a line between the two — and says nothing at all about whether the comment answers the question. That
/// judgement belongs to the discussion agent, and the executor will refuse an interpretation that cites
/// a comment which does not appear here.
/// <para>
/// Not <see cref="ClarificationCandidateAnswer"/>: that is the durable row written once the agent has
/// INTERPRETED a candidate, and it carries an interpretation summary. This is the mechanical link that
/// exists before any interpretation, and it must be able to exist for a comment the round then rejects.
/// </para>
/// </summary>
/// <param name="QuestionId">The open question this comment is linked to.</param>
/// <param name="CommentId">The linked comment.</param>
/// <param name="Signals">Every signal that fired, in enum order, never empty.</param>
internal sealed record CandidateAnswer(string QuestionId, string CommentId, IReadOnlyList<CandidateSignal> Signals);

/// <summary>
/// Everything a <c>DiscussionFollowUp</c> round is allowed to see (design §5.4). The list is closed on
/// purpose: no diff, no file inventory and no specialist output, because the head has not changed and
/// this round exists to engage with the discussion rather than to review the code again. Focused code
/// reads remain available as TOOLS during the turn — they are pulled when a response needs evidence,
/// not pushed into the prompt.
/// </summary>
/// <param name="RoundId">The engagement round this input was frozen for.</param>
/// <param name="HeadSha">The unchanged head. Present so the agent can state what it is (not) reviewing.</param>
/// <param name="NewComments">The frozen window of unconsumed external comments.</param>
/// <param name="AncestorContext">Older comments pulled in only to make the new ones interpretable.</param>
/// <param name="OpenQuestions">Questions still awaiting an answer.</param>
/// <param name="Candidates">Mechanically correlated candidate answers.</param>
/// <param name="PriorContextManifest">The context manifest the code-review round sourced, if any.</param>
/// <param name="PriorObservations">What earlier rounds recorded, so this one neither repeats nor contradicts them blindly.</param>
/// <param name="OmittedPriorObservationCount">Earlier observations intentionally left out of this bounded turn.</param>
/// <param name="OmittedPriorObservationByteCount">UTF-8 bytes in the omitted observation summaries.</param>
/// <param name="PriorObservationSource">Complete audit projection for the selected prior observations.</param>
internal sealed record DiscussionRoundInput(
    long RoundId,
    string HeadSha,
    IReadOnlyList<DiscussionComment> NewComments,
    IReadOnlyList<OpenClarificationQuestion> OpenQuestions,
    IReadOnlyList<CandidateAnswer> Candidates,
    IReadOnlyList<DiscussionComment>? AncestorContext = null,
    string? PriorContextManifest = null,
    IReadOnlyList<string>? PriorObservations = null,
    int OmittedPriorObservationCount = 0,
    int OmittedPriorObservationByteCount = 0,
    AuditSourceReference? PriorObservationSource = null
)
{
    /// <summary>Never null.</summary>
    public IReadOnlyList<DiscussionComment> AncestorContext { get; init; } = AncestorContext ?? [];

    /// <summary>Never null.</summary>
    public IReadOnlyList<string> PriorObservations { get; init; } = PriorObservations ?? [];
}
