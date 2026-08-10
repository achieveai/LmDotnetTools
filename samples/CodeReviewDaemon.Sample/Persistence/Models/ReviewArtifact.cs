namespace CodeReviewDaemon.Sample.Persistence.Models;

/// <summary>
/// A schema-versioned, append-compatible review output (plan §14): the Review/Judge/Knowledge records
/// and — importantly — the B-variant's review output, which lives here in SQLite and is NEVER written
/// to the ReviewBot git repo.
/// </summary>
internal sealed record ReviewArtifact
{
    public long Id { get; init; }

    /// <summary>FK to the owning <c>review_run</c>.</summary>
    public required long ReviewRunId { get; init; }

    /// <summary>
    /// Schema version of <see cref="Payload"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What this does NOT do</b>, despite how it reads: it does not make readers ignore unknown fields or
    /// tolerate missing ones. <c>System.Text.Json</c> does both by default, at every version, whether or not
    /// anyone consults this number. Believing otherwise is the whole hazard — a reader who thinks the version
    /// is carrying that weight will not check whether a dropped field silently deserialized to blank, which is
    /// exactly what happened when <c>FileManifest</c> was removed.
    /// </para>
    /// <para>
    /// <b>What it does do:</b> it lets a reader refuse a payload written by a build newer than itself, in
    /// <c>DaemonReviewStageExecutor.TryReadArtifactPayload</c>. That is forward-safety and nothing else.
    /// </para>
    /// <para>
    /// <b>The bump rule</b> — the same one <c>WorkflowInstanceSnapshot.CurrentSchemaVersion</c> states, and it
    /// is narrower than "bump on any shape change". Do NOT bump for an additive optional field: bumping would
    /// make every new artifact unreadable to an older build for the sake of a field that older build would
    /// ignore anyway. DO bump when an existing field changes meaning, units, or nullability semantics — when
    /// an old reader would misinterpret rather than merely miss.
    /// </para>
    /// <para>
    /// Both shape changes so far were correctly left at 1 under that rule: <c>SubAgentCount</c> was additive
    /// and optional, and <c>FileManifest</c>'s removal dropped a field no reader ever consulted. So "the
    /// version never moved through two shape changes" is not evidence of neglect — it is the rule working. The
    /// real gap was that the rule was written nowhere and no reader consulted the field at all.
    /// </para>
    /// <para>
    /// A bump is a DEPLOY-ORDER constraint: ship readers before writers. The store outlives any one deploy.
    /// </para>
    /// </remarks>
    public required int ArtifactSchemaVersion { get; init; }

    /// <summary>e.g. <c>review</c>, <c>judge</c>, <c>knowledge</c>, <c>b-variant-review</c>.</summary>
    public required string ArtifactKind { get; init; }

    public required string Provider { get; init; }

    /// <summary>
    /// JSON payload. Readers get unknown-field tolerance from the serializer, not from
    /// <see cref="ArtifactSchemaVersion"/> — so a field REMOVED from the current record still parses cleanly
    /// and reads as the type's default. Where that default is indistinguishable from a legitimate value (an
    /// empty <c>Diff</c> is a real state; a null <c>int?</c> is not a count), the reader needs its own content
    /// check and must not lean on the version to notice.
    /// </summary>
    public required string Payload { get; init; }
}
