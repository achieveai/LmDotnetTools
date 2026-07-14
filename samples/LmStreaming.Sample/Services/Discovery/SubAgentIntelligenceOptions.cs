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

    internal static SubAgentIntelligenceOptions Load(
        IConfiguration configuration,
        ILogger logger
    )
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        var tiers = new Dictionary<int, string[]>();
        foreach (var entry in configuration.GetSection(SectionName).GetSection(nameof(Tiers)).GetChildren())
        {
            if (!int.TryParse(entry.Key, out var tier) || tier is < 0 or > 6)
            {
                logger.LogError(
                    "Ignoring invalid {SectionName}:Tiers key {TierKey}; tier keys must be integers from 0 through 6",
                    SectionName,
                    entry.Key
                );
                continue;
            }

            string[] candidates;
            try
            {
                candidates = entry.Get<string[]>() ?? [];
            }
            catch (InvalidOperationException ex)
            {
                logger.LogError(
                    ex,
                    "Ignoring invalid {SectionName}:Tiers:{Tier} mapping",
                    SectionName,
                    tier
                );
                continue;
            }

            if (!tiers.TryAdd(tier, candidates))
            {
                logger.LogError(
                    "Ignoring duplicate normalized {SectionName}:Tiers key {TierKey}",
                    SectionName,
                    entry.Key
                );
            }
        }

        return new SubAgentIntelligenceOptions { Tiers = tiers };
    }
}
