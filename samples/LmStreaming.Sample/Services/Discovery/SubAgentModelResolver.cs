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

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !_catalog.TryGetCopilotModel(candidate, out var model))
            {
                continue;
            }

            if (model.Transport is CopilotModelTransport.Anthropic or CopilotModelTransport.Responses)
            {
                return model.Id;
            }
        }

        WarnOnce(
            $"unroutable-tier:{modelIntelligence.Value}",
            "Sub-agent model-intelligence tier {Tier} has no routable Copilot catalog candidate; "
                + "inheriting the parent model",
            modelIntelligence
        );
        return null;
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
