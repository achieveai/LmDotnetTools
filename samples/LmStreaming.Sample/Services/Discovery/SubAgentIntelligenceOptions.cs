namespace LmStreaming.Sample.Services.Discovery;

/// <summary>
/// Operator-supplied model-intelligence tier candidates.
/// </summary>
internal sealed class SubAgentIntelligenceOptions
{
    public const string SectionName = "SubAgentIntelligence";

    /// <summary>
    /// Ordered model candidates keyed by intelligence tier.
    /// </summary>
    public Dictionary<int, string[]> Tiers { get; init; } = [];
}
