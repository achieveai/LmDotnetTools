using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmSampleShared.Discovery;

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

    /// <summary>
    /// Reasoning effort per intelligence tier. A tier with no entry carries no effort of its own. Kept
    /// apart from <see cref="Tiers"/> so the model lists — which also feed the override allow-list and the
    /// Agent-tool model menu — stay plain model ids.
    /// </summary>
    public Dictionary<int, ReasoningEffort> Efforts { get; init; } = [];

    internal static SubAgentIntelligenceOptions Load(IConfiguration configuration, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        return new SubAgentIntelligenceOptions
        {
            Tiers = LoadTiers(configuration, logger),
            Efforts = LoadEfforts(configuration, logger),
        };
    }

    private static Dictionary<int, ReasoningEffort> LoadEfforts(IConfiguration configuration, ILogger logger)
    {
        var efforts = new Dictionary<int, ReasoningEffort>();
        foreach (var entry in configuration.GetSection(SectionName).GetSection(nameof(Efforts)).GetChildren())
        {
            if (!TryParseTier(entry.Key, nameof(Efforts), logger, out var tier))
            {
                continue;
            }

            if (SubAgentMarkdownParser.ParseEffortToken(entry.Value) is not { } effort)
            {
                logger.LogError(
                    "Ignoring invalid {SectionName}:Efforts:{Tier} value {Effort}; use low, medium, high, "
                        + "extra-high or xhigh. The tier keeps no effort of its own.",
                    SectionName,
                    tier,
                    entry.Value
                );
                continue;
            }

            if (!efforts.TryAdd(tier, effort))
            {
                logger.LogError(
                    "Ignoring duplicate normalized {SectionName}:Efforts key {TierKey}",
                    SectionName,
                    entry.Key
                );
            }
        }

        return efforts;
    }

    private static bool TryParseTier(string key, string mapName, ILogger logger, out int tier)
    {
        if (int.TryParse(key, out tier) && tier is >= 0 and <= 6)
        {
            return true;
        }

        logger.LogError(
            "Ignoring invalid {SectionName}:{MapName} key {TierKey}; tier keys must be integers from 0 through 6",
            SectionName,
            mapName,
            key
        );
        return false;
    }

    private static Dictionary<int, string[]> LoadTiers(IConfiguration configuration, ILogger logger)
    {
        var tiers = new Dictionary<int, string[]>();
        foreach (var entry in configuration.GetSection(SectionName).GetSection(nameof(Tiers)).GetChildren())
        {
            if (!TryParseTier(entry.Key, nameof(Tiers), logger, out var tier))
            {
                continue;
            }

            string[] candidates;
            try
            {
                candidates = entry.Get<string[]>() ?? [];
            }
            catch (InvalidOperationException ex)
            {
                logger.LogError(ex, "Ignoring invalid {SectionName}:Tiers:{Tier} mapping", SectionName, tier);
                continue;
            }

            // A tier mapped to an EMPTY array is a misconfiguration, not a configuration. Configuration
            // binding materialises `"3": []` as a PRESENT key holding a zero-length array, so keeping it
            // would make Tiers.Count non-zero while no tier can resolve anything — which is exactly how the
            // shipped stub of seven empty arrays disabled the "Tiers is empty" diagnostic in
            // SubAgentModelResolver.Resolve (it fired zero times across every host log) and left only the
            // downstream "no routable candidate" warning, which names a symptom instead of the cause.
            // Dropping the key restores that diagnostic and changes no routing outcome: an absent tier and a
            // tier with no candidates both fall through to the inherited parent model.
            if (candidates.All(string.IsNullOrWhiteSpace))
            {
                logger.LogError(
                    "Ignoring empty {SectionName}:Tiers:{Tier} mapping; a tier configured with no model "
                        + "candidates cannot resolve and is treated as UNCONFIGURED. Give it at least one "
                        + "model id, or remove the key.",
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

        return tiers;
    }
}
