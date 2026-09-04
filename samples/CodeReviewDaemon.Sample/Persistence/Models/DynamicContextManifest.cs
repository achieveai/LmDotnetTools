using System.Text.Json.Serialization;

namespace CodeReviewDaemon.Sample.Persistence.Models;

internal enum DynamicContextGapState
{
    Linked,
    NoneLinked,
    Failed,
    Unavailable,
    Truncated,
}

internal sealed record DynamicContextBootstrap(
    long EngagementRoundId,
    string RepoRef,
    string PrId,
    string BaseSha,
    string HeadSha,
    string MergeBaseSha,
    string WorkspacePath,
    IReadOnlyList<string> ChangedPaths,
    IReadOnlyList<string> LinkedWorkItemRefs,
    IReadOnlyList<string> DiscussionRefs,
    IReadOnlyList<string> OpenQuestionRefs,
    IReadOnlyList<string> KnowledgeBasePaths,
    long PriorObservationBoundary
);

internal sealed record DynamicContextClaimDraft(string ClaimId, string Text, IReadOnlyList<string> Citations);

internal sealed record DynamicContextClaim(
    string ClaimId,
    string Text,
    IReadOnlyList<string> Citations,
    IReadOnlyList<AuditSourceReference> SourceRecordRefs
);

internal sealed record DynamicContextGap(
    string Scope,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] DynamicContextGapState State,
    bool IsRequired,
    string? Detail
);

internal sealed record DynamicContextManifestDraft(
    int Version,
    long EngagementRoundId,
    IReadOnlyList<DynamicContextClaimDraft> Claims,
    IReadOnlyList<DynamicContextGap> Gaps
);

internal sealed record DynamicContextManifest(
    int Version,
    long EngagementRoundId,
    string ThreadId,
    string GathererAgentId,
    string GathererTemplate,
    int ScopedReadCount,
    IReadOnlyList<DynamicContextClaim> Claims,
    IReadOnlyList<DynamicContextGap> Gaps
)
{
    public const int SchemaVersion = 1;
}

internal sealed record DynamicContextGatheringResult(
    DynamicContextManifestDraft SemanticManifest,
    string? RunId,
    string? ThreadId
);
