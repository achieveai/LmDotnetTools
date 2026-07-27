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
