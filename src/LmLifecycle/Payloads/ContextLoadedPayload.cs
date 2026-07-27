using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmLifecycle.Payloads;

/// <summary>
/// One discovered context source that contributed to a rendered context block.
/// </summary>
public sealed record LifecycleContextSource
{
    /// <summary>
    /// What kind of discovery produced this source — a repository instruction file, a skill, an
    /// injected directory context, and so on. Open vocabulary.
    /// </summary>
    [JsonPropertyName("discovery_kind")]
    public string DiscoveryKind { get; set; } = string.Empty;

    /// <summary>A short display name for the source.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The source's location, normalized to forward slashes and made relative to the workspace root
    /// where one applies. Absent for sources that have no path.
    /// </summary>
    /// <remarks>
    /// Normalized rather than raw so the same file produces the same value regardless of the host
    /// operating system, which is what makes <see cref="DedupIdentity"/> comparable across hosts.
    /// </remarks>
    [JsonPropertyName("normalized_path")]
    public string? NormalizedPath { get; set; }

    /// <summary>
    /// The value used to decide two discoveries were the same source. Sources sharing it were
    /// collapsed into one entry.
    /// </summary>
    [JsonPropertyName("dedup_identity")]
    public string DedupIdentity { get; set; } = string.Empty;

    /// <summary>How many bytes of this source were rendered, after any truncation.</summary>
    [JsonPropertyName("rendered_byte_count")]
    public long RenderedByteCount { get; set; }

    /// <summary>
    /// Whether the source was cut short to fit. When <see langword="true"/>, the model saw less
    /// than the source contains.
    /// </summary>
    [JsonPropertyName("was_truncated")]
    public bool WasTruncated { get; set; }

    /// <summary>
    /// When this source entered the conversation. See <see cref="LifecycleContextPhases"/>. Open
    /// vocabulary.
    /// </summary>
    [JsonPropertyName("phase")]
    public string Phase { get; set; } = LifecycleContextPhases.Boot;
}

/// <summary>
/// Payload for <see cref="LifecycleEventTypes.ContextLoaded"/>.
/// </summary>
/// <remarks>
/// <para>
/// Emitted only from the immutable snapshot of a provider request, immediately before dispatch —
/// so it reports context the model actually received, not context that was merely discovered.
/// Discovery that is queued, cancelled, superseded, or rediscovered without being sent produces no
/// event.
/// </para>
/// <para>
/// <see cref="RenderedText"/> is present only for subscribers granted
/// <see cref="LifecycleCapabilities.ContentFull"/>. <see cref="RenderedHash"/> is always present, so
/// a subscriber without that capability can still tell whether two requests carried identical
/// context.
/// </para>
/// </remarks>
public sealed record ContextLoadedPayload
{
    /// <summary>The run whose request carried the context.</summary>
    [JsonPropertyName("run_id")]
    public string RunId { get; set; } = string.Empty;

    /// <summary>The turn whose request carried the context.</summary>
    [JsonPropertyName("generation_id")]
    public string GenerationId { get; set; } = string.Empty;

    /// <summary>
    /// The sources that contributed, after deduplication. An empty list means context rendering ran
    /// and produced nothing; absent means the producer did not report sources at all.
    /// </summary>
    [JsonPropertyName("sources")]
    public IReadOnlyList<LifecycleContextSource> Sources { get; set; } = [];

    /// <summary>
    /// SHA-256 of the exact rendered block as sent, over its UTF-8 bytes, in lowercase hex.
    /// </summary>
    [JsonPropertyName("rendered_hash")]
    public string RenderedHash { get; set; } = string.Empty;

    /// <summary>The byte length of the rendered block as sent.</summary>
    [JsonPropertyName("rendered_byte_count")]
    public long RenderedByteCount { get; set; }

    /// <summary>
    /// The rendered block itself. Present only when the subscriber holds
    /// <see cref="LifecycleCapabilities.ContentFull"/>; otherwise omitted.
    /// </summary>
    [JsonPropertyName("rendered_text")]
    public string? RenderedText { get; set; }
}
