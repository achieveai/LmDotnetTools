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
    /// Ordered model candidates keyed by intelligence tier. Always plain model ids: a configured
    /// <c>"model:effort"</c> candidate is split on load, its effort going to <see cref="ModelEfforts"/>.
    /// </summary>
    public Dictionary<int, string[]> Tiers { get; init; } = [];

    /// <summary>
    /// Reasoning effort per intelligence tier. A tier with no entry carries no effort of its own. Kept
    /// apart from <see cref="Tiers"/> so the model lists — which also feed the override allow-list and the
    /// Agent-tool model menu — stay plain model ids.
    /// </summary>
    public Dictionary<int, ReasoningEffort> Efforts { get; init; } = [];

    /// <summary>
    /// Reasoning effort for one candidate within one tier, written in configuration as
    /// <c>"gpt-5.6-terra:xhigh"</c>. It replaces the tier's <see cref="Efforts"/> entry when that candidate
    /// is the one the tier resolves to, so a fallback model can run at an effort that suits it rather than
    /// the primary's. Model ids match case-insensitively.
    /// </summary>
    public Dictionary<int, Dictionary<string, ReasoningEffort>> ModelEfforts { get; init; } = [];

    internal static SubAgentIntelligenceOptions Load(IConfiguration configuration, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        var (tiers, modelEfforts) = LoadTiers(configuration, logger);
        return new SubAgentIntelligenceOptions
        {
            Tiers = tiers,
            Efforts = LoadEfforts(configuration, logger),
            ModelEfforts = modelEfforts,
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

    private static (
        Dictionary<int, string[]> Tiers,
        Dictionary<int, Dictionary<string, ReasoningEffort>> ModelEfforts
    ) LoadTiers(IConfiguration configuration, ILogger logger)
    {
        var tiers = new Dictionary<int, string[]>();
        var modelEfforts = new Dictionary<int, Dictionary<string, ReasoningEffort>>();
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

            var efforts = new Dictionary<string, ReasoningEffort>(StringComparer.OrdinalIgnoreCase);
            var modelIds = candidates.Select(candidate => SplitEffort(candidate, efforts)).ToArray();

            if (!tiers.TryAdd(tier, modelIds))
            {
                logger.LogError(
                    "Ignoring duplicate normalized {SectionName}:Tiers key {TierKey}",
                    SectionName,
                    entry.Key
                );
                continue;
            }

            if (efforts.Count > 0)
            {
                modelEfforts[tier] = efforts;
            }
        }

        return (tiers, modelEfforts);
    }

    // "gpt-5.6-terra:xhigh" -> "gpt-5.6-terra", recording xhigh. Only a suffix that names an effort is split
    // off, so a candidate without one (or a model id that itself contains a colon) passes through whole.
    private static string SplitEffort(string candidate, Dictionary<string, ReasoningEffort> efforts)
    {
        var separator = string.IsNullOrWhiteSpace(candidate) ? -1 : candidate.LastIndexOf(':');
        if (separator <= 0 || SubAgentMarkdownParser.ParseEffortToken(candidate[(separator + 1)..]) is not { } effort)
        {
            return candidate;
        }

        var modelId = candidate[..separator].Trim();
        efforts[modelId] = effort;
        return modelId;
    }
}
