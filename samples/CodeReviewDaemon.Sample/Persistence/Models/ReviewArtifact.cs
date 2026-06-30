namespace CodeReviewDaemon.Sample.Persistence.Models;

/// <summary>
/// A schema-versioned, append-compatible review output (plan §14): the Review/Judge/Knowledge records
/// and — importantly — the B-variant's review output, which lives here in SQLite and is NEVER written
/// to the ReviewBot git repo. <see cref="ArtifactSchemaVersion"/> lets readers ignore unknown fields
/// and tolerate missing older fields as the payload shape evolves.
/// </summary>
internal sealed record ReviewArtifact
{
    public long Id { get; init; }

    /// <summary>FK to the owning <c>review_run</c>.</summary>
    public required long ReviewRunId { get; init; }

    /// <summary>Schema version of <see cref="Payload"/> (append-compatible; readers ignore unknowns).</summary>
    public required int ArtifactSchemaVersion { get; init; }

    /// <summary>e.g. <c>review</c>, <c>judge</c>, <c>knowledge</c>, <c>b-variant-review</c>.</summary>
    public required string ArtifactKind { get; init; }

    public required string Provider { get; init; }

    /// <summary>JSON payload; readers must ignore unknown fields and tolerate missing older fields.</summary>
    public required string Payload { get; init; }
}
