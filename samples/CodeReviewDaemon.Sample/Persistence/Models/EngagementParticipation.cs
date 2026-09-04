namespace CodeReviewDaemon.Sample.Persistence.Models;

/// <summary>
/// One action a round intends to take, before anything is sent (design §5.4).
/// <para>
/// A PLANNING shape, deliberately distinct from the persisted <see cref="ReviewAction"/> row. The row
/// carries an <c>ActionId</c>, a <c>PayloadSha256</c>, a send status and provider receipt/rejection JSON —
/// all of which only exist once the publication pipeline has taken ownership. A round that is still
/// deciding has none of them, so reusing the row here would force the gate to invent identity and status
/// for proposals it may be about to refuse.
/// </para>
/// <para>
/// It carries instead exactly the two links the gate needs: <see cref="CausalCommentIds"/> ties the
/// action to the new discussion that provoked it, and <see cref="Evidence"/> ties it to auditable source
/// records. On acceptance, <see cref="Kind"/> maps 1:1 onto the persisted row's kind and
/// <see cref="Evidence"/> onto the <c>sources</c> argument the store takes alongside it.
/// </para>
/// </summary>
/// <param name="Kind">What the action does.</param>
/// <param name="Body">The wording the agent chose.</param>
/// <param name="TargetRef">Provider-native ref this action continues from, when it continues one.</param>
/// <param name="Path">Diff path, for an inline finding.</param>
/// <param name="Line">Diff line, for an inline finding.</param>
/// <param name="CausalCommentIds">The new-discussion comments that caused this action to exist.</param>
/// <param name="Evidence">Audit source records backing it.</param>
internal sealed record PlannedReviewAction(
    ReviewActionKind Kind,
    string Body,
    string? TargetRef = null,
    string? Path = null,
    int? Line = null,
    IReadOnlyList<string>? CausalCommentIds = null,
    IReadOnlyList<AuditSourceReference>? Evidence = null
)
{
    /// <summary>Never null, so callers never branch on "no links" versus "an empty link list".</summary>
    public IReadOnlyList<string> CausalCommentIds { get; init; } = CausalCommentIds ?? [];

    /// <summary>Never null, for the same reason.</summary>
    public IReadOnlyList<AuditSourceReference> Evidence { get; init; } = Evidence ?? [];
}

/// <summary>
/// An observation as the round states it, before it is given a row. A DRAFT has no id, no sequence and no
/// timestamp on purpose: the append-only store assigns all three, so a round cannot address — and
/// therefore cannot rewrite — an observation that already exists.
/// <para>
/// <see cref="Evidence"/> has no counterpart on <see cref="RoundObservation"/> because the store does not
/// keep it on the row: <c>AppendRoundObservation</c> takes the sources as a separate argument and writes
/// them to <c>round_observation_source</c>. The draft therefore carries the pair the store wants, and the
/// sink below splits them.
/// </para>
/// </summary>
/// <param name="Kind">What sort of observation this is.</param>
/// <param name="Summary">One line an operator can read without opening the audit store.</param>
/// <param name="Evidence">Audit source records backing it; empty is legitimate for a no-action record.</param>
internal sealed record RoundObservationDraft(
    ObservationKind Kind,
    string Summary,
    IReadOnlyList<AuditSourceReference>? Evidence = null
)
{
    /// <summary>Never null.</summary>
    public IReadOnlyList<AuditSourceReference> Evidence { get; init; } = Evidence ?? [];
}

/// <summary>
/// The append-only sink a round writes its observations to. Deliberately one method: there is no update
/// and no delete to call, so "observations are immutable" is a property of the CONTRACT rather than a rule
/// the caller is trusted to follow. The store-backed implementation assigns each draft an id and the next
/// sequence, then calls <c>AppendRoundObservation</c> with the draft's evidence as its sources.
/// </summary>
internal interface IRoundObservationSink
{
    /// <summary>Appends <paramref name="observations"/> for <paramref name="roundId"/>.</summary>
    Task RecordAsync(
        long roundId,
        IReadOnlyList<RoundObservationDraft> observations,
        CancellationToken cancellationToken
    );
}
