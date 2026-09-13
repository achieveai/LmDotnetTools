namespace CodeReviewDaemon.Sample.Agents;

/// <summary>Historical comparison review artifact, retained for corpus readers.</summary>
internal sealed record VariantReviewArtifactPayload(string VariantId, string ModelId, string ReviewText, string? RunId);
