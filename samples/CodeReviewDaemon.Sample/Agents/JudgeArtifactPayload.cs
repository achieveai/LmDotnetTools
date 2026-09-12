namespace CodeReviewDaemon.Sample.Agents;

/// <summary>Historical judge payload; nullable provenance preserves unknown fields in old records.</summary>
internal sealed record JudgeArtifactPayload(
    int? Score,
    string Rationale,
    string VariantId,
    string? JudgeModelId,
    string? GeneratorModelId,
    bool? SelfGraded,
    int BallotCount
);
