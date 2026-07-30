using System.Collections.Concurrent;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Models;

namespace LmStreaming.Sample.Services.Discovery;

/// <summary>
/// Resolves optional model-intelligence tiers against the discovered Copilot catalog.
/// </summary>
internal sealed class SubAgentModelResolver
{
    private readonly ProviderRegistry _catalog;
    private readonly SubAgentIntelligenceOptions _options;
    private readonly ILogger<SubAgentModelResolver> _logger;
    private readonly ConcurrentDictionary<string, byte> _loggedConditions = new(StringComparer.OrdinalIgnoreCase);

    // The sanctioned override set, computed once from the tier configuration against the discovered
    // catalog: an ordered, de-duplicated list for the Agent-tool menu and a case-insensitive membership
    // set for the runtime guard. Both hold canonical catalog ids. See BuildAllowedModelSet.
    private readonly IReadOnlyList<string> _allowedModelIds;
    private readonly HashSet<string> _allowedModelIdSet;

    public SubAgentModelResolver(
        ProviderRegistry catalog,
        SubAgentIntelligenceOptions options,
        ILogger<SubAgentModelResolver> logger
    )
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _catalog = catalog;
        _options = options;
        _logger = logger;
        (_allowedModelIds, _allowedModelIdSet) = BuildAllowedModelSet(catalog, options);
    }

    /// <summary>
    /// The Copilot model ids a sub-agent's <c>model</c> override may name, surfaced to the Agent tool
    /// descriptor so the controller/parent LLM picks a sanctioned id instead of inventing one or reaching
    /// for an arbitrary (possibly expensive) catalog model. This is the TIER-CONFIGURED allowed set — the
    /// distinct, routable Copilot models named across <c>SubAgentIntelligence:Tiers</c> — NOT the whole
    /// discovered catalog: an override is a knob to move a delegate between the SAME tiers the deployment
    /// already sanctions, so anything outside those tiers is deliberately unavailable. Empty when no tiers
    /// are configured (overrides disabled). Same set <see cref="IsKnownModel"/> accepts at runtime.
    /// </summary>
    internal IReadOnlyList<string> AvailableModelIds => _allowedModelIds;

    /// <summary>
    /// True when <paramref name="modelId"/> names a model in the tier-configured allowed set (see
    /// <see cref="AvailableModelIds"/>) — the runtime guard for a sub-agent's free-form <c>model</c>
    /// override, matched case-insensitively against the discovered catalog's canonical id. An override
    /// that fails this check is dropped by the manager (it falls back to the tier/parent model) instead of
    /// running an unsanctioned model or hard-failing at the provider. A real catalog id that no tier
    /// sanctions returns false on purpose: it kept sub-agents burning tokens on models the deployment
    /// never opted into. Anthropic-compat family ids are intentionally not accepted here — the same
    /// Copilot-only scope as tier routing (see <see cref="TryGetRoutableModel"/>); extend both together if
    /// that changes.
    /// </summary>
    internal bool IsKnownModel(string? modelId) =>
        !string.IsNullOrWhiteSpace(modelId)
        && _catalog.TryGetCopilotModel(modelId, out var model)
        && _allowedModelIdSet.Contains(model.Id);

    // Collapses the tier configuration into the sanctioned override set: every distinct candidate named
    // across all tiers that resolves to a ROUTABLE (Anthropic|Responses) Copilot catalog model, keyed by
    // its canonical catalog id (so casing/aliases normalize and a model listed in several tiers appears
    // once). Walking tiers weakest-first gives a stable, cheapest-first advertised order. This is the same
    // Copilot-only, routable filter TryGetRoutableModel applies to tier resolution, so the menu, the
    // runtime guard, and what a tier can actually resolve to never diverge.
    private static (IReadOnlyList<string> Ordered, HashSet<string> Set) BuildAllowedModelSet(
        ProviderRegistry catalog,
        SubAgentIntelligenceOptions options
    )
    {
        var ordered = new List<string>();
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tier in options.Tiers.OrderBy(pair => pair.Key))
        {
            foreach (var candidate in tier.Value)
            {
                if (string.IsNullOrWhiteSpace(candidate) || !catalog.TryGetCopilotModel(candidate, out var model))
                {
                    continue;
                }

                if (model.Transport is not (CopilotModelTransport.Anthropic or CopilotModelTransport.Responses))
                {
                    continue;
                }

                if (set.Add(model.Id))
                {
                    ordered.Add(model.Id);
                }
            }
        }

        return (ordered, set);
    }

    /// <summary>
    /// Returns an explicit model unchanged, otherwise the first routable candidate for the tier.
    /// A null result means the sub-agent should inherit its parent model.
    /// </summary>
    internal string? Resolve(string? explicitModel, int? modelIntelligence)
    {
        var normalizedModel = explicitModel?.Trim();
        if (
            !string.IsNullOrEmpty(normalizedModel)
            && !string.Equals(normalizedModel, "inherit", StringComparison.OrdinalIgnoreCase)
        )
        {
            if (modelIntelligence is not null)
            {
                InformationOnce(
                    $"ignored-tier:{modelIntelligence.Value}",
                    "Sub-agent explicit model {ModelId} overrides model-intelligence tier {Tier}; "
                        + "the tier was ignored",
                    normalizedModel,
                    modelIntelligence
                );
            }

            return normalizedModel;
        }

        if (modelIntelligence is null)
        {
            return null;
        }

        if (_options.Tiers.Count == 0)
        {
            WarnOnce(
                "empty-map",
                "Sub-agent model-intelligence tier {Tier} cannot be resolved because "
                    + "{SectionName}:Tiers is empty; inheriting the parent model",
                modelIntelligence,
                SubAgentIntelligenceOptions.SectionName
            );
            return null;
        }

        if (!_options.Tiers.TryGetValue(modelIntelligence.Value, out var candidates))
        {
            WarnOnce(
                $"missing-tier:{modelIntelligence.Value}",
                "Sub-agent model-intelligence tier {Tier} is not configured in "
                    + "{SectionName}:Tiers; inheriting the parent model",
                modelIntelligence,
                SubAgentIntelligenceOptions.SectionName
            );
            return null;
        }

        if (TryGetRoutableModel(candidates, out var routable))
        {
            return routable;
        }

        WarnOnce(
            $"unroutable-tier:{modelIntelligence.Value}",
            "Sub-agent model-intelligence tier {Tier} has no routable Copilot catalog candidate; "
                + "inheriting the parent model",
            modelIntelligence
        );
        return null;
    }

    /// <summary>
    /// Like <see cref="Resolve"/>, but when the requested tier is unconfigured or has no routable
    /// catalog candidate it CLIMBS to the next-higher configured tier (more capable) until one
    /// resolves or the ladder is exhausted. An explicit model still wins outright and a null tier
    /// still inherits the parent. This is the per-spawn entry point used when a workflow controller
    /// (or a JSON-repair fallback, via <c>ResolveClimbing(null, 0)</c> for the lowest available
    /// tier) requests a tier that may be unmapped in this deployment — climbing yields the nearest
    /// available model rather than silently inheriting the parent, which is exactly the gap the
    /// single-tier <see cref="Resolve"/> leaves.
    /// </summary>
    internal string? ResolveClimbing(string? explicitModel, int? modelIntelligence)
    {
        var normalizedModel = explicitModel?.Trim();
        if (
            !string.IsNullOrEmpty(normalizedModel)
            && !string.Equals(normalizedModel, "inherit", StringComparison.OrdinalIgnoreCase)
        )
        {
            return normalizedModel;
        }

        if (modelIntelligence is null)
        {
            return null;
        }

        // Walk only the CONFIGURED tiers at or above the requested one, weakest-first, so a request
        // for an unmapped tier lands on the nearest higher-capability tier that is actually routable.
        foreach (var tier in _options.Tiers.Keys.Where(key => key >= modelIntelligence.Value).OrderBy(key => key))
        {
            if (TryGetRoutableModel(_options.Tiers[tier], out var routable))
            {
                return routable;
            }
        }

        WarnOnce(
            $"climb-exhausted:{modelIntelligence.Value}",
            "Sub-agent model-intelligence tier {Tier} (and every higher configured tier) had no "
                + "routable Copilot catalog candidate; inheriting the parent model",
            modelIntelligence
        );
        return null;
    }

    // Returns the first routable model id among the tier's candidates, matched against the discovered
    // Copilot catalog ONLY (TryGetCopilotModel). Anthropic-compat family ids discovered via
    // AnthropicCompatProviders.DiscoverFromEnv (e.g. "deepseek-v4-pro") live in a separate catalog
    // reachable only through ProviderRegistry.TryGetAnthropicCompatModel, so such a candidate is
    // silently skipped and the caller falls through to the next entry/tier. Today that is harmless
    // (Copilot ids precede it in every configured tier), but to make an anthropic-compat model
    // selectable on its own, add a second lookup here that also consults TryGetAnthropicCompatModel
    // and returns its provider id when present.
    private bool TryGetRoutableModel(IReadOnlyList<string> candidates, out string modelId)
    {
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !_catalog.TryGetCopilotModel(candidate, out var model))
            {
                continue;
            }

            if (model.Transport is CopilotModelTransport.Anthropic or CopilotModelTransport.Responses)
            {
                modelId = model.Id;
                return true;
            }
        }

        modelId = string.Empty;
        return false;
    }

    private void WarnOnce(string condition, string message, params object?[] args)
    {
        if (_loggedConditions.TryAdd(condition, 0))
        {
            _logger.LogWarning(message, args);
        }
    }

    private void InformationOnce(string condition, string message, params object?[] args)
    {
        if (_loggedConditions.TryAdd(condition, 0))
        {
            _logger.LogInformation(message, args);
        }
    }
}
