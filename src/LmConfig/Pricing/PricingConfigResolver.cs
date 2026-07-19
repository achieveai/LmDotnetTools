using AchieveAi.LmDotnetTools.LmConfig.Models;
using AchieveAi.LmDotnetTools.LmCore.Models;

namespace AchieveAi.LmDotnetTools.LmConfig.Pricing;

/// <summary>
///     <see cref="IPricingResolver" /> backed by a model → <see cref="PricingConfig" /> map (e.g. sourced
///     from application config or OpenRouter's public pricing listing). Wires the previously-unused
///     <see cref="PricingConfig" /> rates into the conversation usage accounting layer through LmCore's
///     narrow resolver abstraction (#196), keeping the accounting core free of a direct LmConfig dependency.
/// </summary>
public sealed class PricingConfigResolver : IPricingResolver
{
    private readonly IReadOnlyDictionary<string, PricingConfig> _pricingByModel;
    private readonly string? _source;
    private readonly string? _version;

    /// <summary>Creates a resolver over a snapshot of per-model pricing.</summary>
    /// <param name="pricingByModel">Map of effective model id to its public pricing.</param>
    /// <param name="source">Optional catalog source recorded on resolved pricing for provenance.</param>
    /// <param name="version">Optional catalog version / effective date recorded for provenance.</param>
    public PricingConfigResolver(
        IReadOnlyDictionary<string, PricingConfig> pricingByModel,
        string? source = null,
        string? version = null)
    {
        ArgumentNullException.ThrowIfNull(pricingByModel);
        _pricingByModel = pricingByModel;
        _source = source;
        _version = version;
    }

    /// <inheritdoc />
    public ModelPricing? Resolve(string modelId)
    {
        if (string.IsNullOrEmpty(modelId) || !_pricingByModel.TryGetValue(modelId, out var config))
        {
            return null;
        }

        return new ModelPricing
        {
            ModelId = modelId,
            PromptPerMillion = (decimal)config.PromptPerMillion,
            CompletionPerMillion = (decimal)config.CompletionPerMillion,
            Source = _source,
            Version = _version,
        };
    }
}
