using System.Text.Json;
using System.Text.Json.Serialization;
using CodeReviewDaemon.Sample.Workspace.Git;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>Stable historical artifact identities and decoding settings; no fixed-stage execution policy.</summary>
internal static class ReviewArtifactKinds
{
    public const string ContextArtifactKind = "review-context";
    public const int ContextArtifactSchemaVersion = 3;
    public const string ReviewArtifactKind = "review";
    public const int ReviewArtifactSchemaVersion = 1;
    public const string FindingsArtifactKind = "review-findings";
    public const int FindingsArtifactSchemaVersion = 1;
    public const string ProvisionalReviewArtifactKind = "review-provisional";
    public const string SynthesisRequestArtifactKind = "review-synthesis-request";
    public const string S2SModality = "s2s";
    public const string InProcessModality = "in-process";
    public const string ProvisionalTurn = "provisional";
    public const string SynthesisTurn = "synthesis";
    public const string PushReviewBotOperation = "push-reviewbot";
    public const string VariantReviewArtifactKind = "b-variant-review";
    public const int VariantReviewArtifactSchemaVersion = 1;
    public const string JudgeArtifactKind = "judge";
    public const int JudgeArtifactSchemaVersion = 2;
    internal static readonly JsonSerializerOptions PayloadOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Compatibility classifier used only when reading the historical sentinel metric.</summary>
    internal static bool IsNoNewFindingsSentinel(string? reviewText) =>
        reviewText is not null
        && reviewText.TrimStart().StartsWith("No new findings", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The persisted PR diff/context (kind <c>review-context</c>). <see cref="FileManifest"/> is the
/// newline-joined tracked-file list of the head checkout (bounded); <see cref="CheckoutRoot"/> is the absolute
/// dir the reviewed repo is checked out in (the manifest and changed paths are relative to it), and
/// <see cref="StoreRoot"/> is the cross-repo store root when the reviewed repo was checked out as a store
/// submodule (else null). <see cref="ChangedPaths"/> is the newline-joined <c>git diff --name-only</c> listing
/// for the same range: <see cref="Diff"/> is capped, so on a large PR its later headers are gone and it is NOT
/// a complete record of what changed — anything that ranks or routes by changed file must read this instead.
/// All are null/empty on older artifacts.
/// <para>
/// NOTE on what reaches the reviewer: <see cref="ChangedPaths"/> is injected into the review brief;
/// <see cref="Diff"/> and <see cref="FileManifest"/> are NOT (see <c>BuildReviewInput</c>) — the reviewer reads
/// the patch and the tree from the checkout instead. Both are still persisted here because they are the run's
/// record of what was reviewed and are what the Knowledge Base ranking reads, and <see cref="Diff"/> remains
/// the degraded brief when a resumed older artifact carries no <see cref="ChangedPaths"/>.
/// </para>
/// <para>
/// <see cref="UncomparableReason"/> is written only on the one run shape that has one — a pair of commits
/// preparation established share no ancestor at all — and is omitted from the JSON entirely when null, so an
/// ordinary artifact serializes to exactly the bytes it did before the property existed. It is what makes a
/// v2 row distinguishable from a v1 one: see <c>ReviewArtifactKinds.ContextArtifactSchemaVersion</c>.
/// </para>
/// <para>
/// <see cref="MergeBaseSha"/> (v3, issue #647) is the commit id <see cref="MergeBaseResolver"/> resolved
/// base...head against on this run's checkout — populated on all three construction sites, including the
/// direct/non-pooled path, which resolves it explicitly for this purpose (the pooled paths get it for free
/// from <c>PreparedCheckout.MergeBaseSha</c>). Null and omitted from the JSON on any run whose merge base was
/// not <see cref="MergeBaseOutcome.Resolved"/>, and on any artifact written before this field existed.
/// </para></summary>
internal sealed record ContextArtifactPayload(
    string PrId,
    string BaseSha,
    string HeadSha,
    string Diff,
    string? FileManifest = null,
    string? CheckoutRoot = null,
    string? StoreRoot = null,
    string? ChangedPaths = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? UncomparableReason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MergeBaseSha = null
);

/// <summary>The persisted primary review output (kind <c>review</c>). <see cref="ThreadId"/> is the conversation
/// thread the review ran on — on the S2S path the LmStreaming-minted id the Posted stage turns into the posted
/// deep-link; on the in-process path the daemon's own <c>review-run-*</c> id (never linked). Null on older
/// artifacts written before the field existed.
/// <para>
/// The same shape is reused for the <c>review-provisional</c> checkpoint, where the trailing fields carry the
/// Reviewed stage's ORIGINAL start and absolute deadline (so a resumed lifecycle continues on the budget it was
/// granted instead of buying itself a fresh one on every restart), the identity a resume must still match, and
/// whether the provisional turn actually finished. They are appended and nullable/defaulted, so old rows still
/// deserialize here and an older reader still sees the shape it expects — the authoritative <c>review</c>
/// artifact leaves them all at their defaults.
/// </para>
/// <para>
/// <see cref="ProvisionalComplete"/> is what makes an EMPTY <see cref="ReviewText"/> unambiguous. The mint-time
/// checkpoint has no review text yet because no turn has run; a provisional turn that legitimately answered
/// with nothing looks identical on the text alone. The flag says which, so an empty payload is never mistaken
/// for a completed provisional (nor a completed one re-driven as if it had never happened).
/// </para></summary>
internal sealed record ReviewArtifactPayload(
    string ReviewText,
    string? RunId,
    string VariantId,
    string? ThreadId = null,
    DateTimeOffset? ReviewedStartedAtUtc = null,
    DateTimeOffset? ReviewedDeadlineUtc = null,
    ReviewLifecycleIdentity? Lifecycle = null,
    bool ProvisionalComplete = false,
    CommentFetchOutcome? CommentFetch = null
);

/// <summary>
/// How the existing-comment lookup ended for one run — the four exits
/// the historical executor can return, kept apart because on main
/// three of them used to collapse into the same brief: a genuinely clean PR, a provider fetch that threw, and
/// a provider with no publisher wired all left <c>reviewInput</c> byte-identical, and the method returned only
/// that string, so nothing downstream — not the brief, not a log, not a persisted field — could tell them
/// apart (issue #576).
/// <para>
/// <see cref="Ok"/> and <see cref="Empty"/> are both healthy; <see cref="Failed"/> and <see cref="NoPublisher"/>
/// are both defects, and different ones — the first points at the provider, the second at the daemon's own
/// wiring. Collapsed together, the rate of either is unmeasurable: a fleet where the fetch never fails and a
/// fleet where nobody looks produce identical numbers.
/// </para>
/// <para>
/// Serialized BY NAME (<see cref="JsonStringEnumConverter{TEnum}"/>) rather than by ordinal, because the
/// consumer is a person opening a stored artifact months later. <c>"commentFetch": 2</c> tells them nothing and
/// silently re-points if a member is ever inserted; <c>"commentFetch": "Failed"</c> survives both.
/// </para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CommentFetchOutcome>))]
internal enum CommentFetchOutcome
{
    /// <summary>The fetch succeeded and returned at least one comment; the brief carries the thread list.</summary>
    Ok,

    /// <summary>The fetch succeeded and returned nothing — a genuinely comment-free PR. Healthy.</summary>
    Empty,

    /// <summary>The provider call threw. The review proceeded without a dedup list and was told so.</summary>
    Failed,

    /// <summary>No comment reader is registered for the run's provider, so no lookup was attempted.</summary>
    NoPublisher,
}

/// <summary>
/// Everything about a Reviewed lifecycle that must still hold for a checkpoint of it to be resumable: WHERE it
/// runs (<see cref="Modality"/>, <see cref="LocalThreadId"/>, <see cref="WorkspaceId"/>), WHAT runs it
/// (<see cref="ModelId"/>, <see cref="ToolAssisted"/>) and WHICH context it was built from
/// (<see cref="ContextGeneration"/>). Compared by value, so adding a field automatically tightens the check.
/// <para>
/// <see cref="WorkspaceId"/> is the load-bearing one. The hosted workspace of a pooled review is named after
/// the SLOT this process leased, and slots are re-assigned from an in-memory pool that resets on restart — so
/// the same run can come back holding a slot whose checkout is a different PR entirely. Resuming the old
/// conversation there would synthesize a review of that other PR and post it here, silently.
/// <see cref="ContextGeneration"/> is the id of the latest <c>review-context</c> artifact, which changes
/// only when the ContextReady stage mints a genuinely NEW artifact — a real base/head move — after which the
/// checkpointed conversation is reviewing a diff this run is no longer about. A same-head re-entry (a
/// crash/restart before the prior ContextReady attempt's artifact-then-state writes both landed) reuses that
/// same artifact id rather than minting a new one, so a checkpoint built against it stays valid.
/// </para>
/// </summary>
internal sealed record ReviewLifecycleIdentity(
    string Modality,
    string LocalThreadId,
    string? WorkspaceId,
    string? ModelId,
    bool ToolAssisted,
    long ContextGeneration
);

/// <summary>
/// An S2S synthesis turn the review host accepted but has not answered yet (kind
/// <c>review-synthesis-request</c>). <see cref="InputId"/> is the host-minted id a resumed review polls
/// instead of re-sending the turn, <see cref="ParentThreadId"/> is the hosted conversation it was accepted on
/// (a request from any OTHER conversation is stale and must not be rejoined), and <see cref="ReviewRunId"/> is
/// the daemon review run it belongs to — carried in the payload so a checkpoint is self-describing in logs and
/// exports, where the row's foreign key is not in view. Named for the daemon run to keep it distinct from the
/// PROVIDER run id that <see cref="ReviewArtifactPayload.RunId"/> carries.
/// </summary>
internal sealed record SynthesisRequestPayload(string InputId, string ReviewRunId, string ParentThreadId);
