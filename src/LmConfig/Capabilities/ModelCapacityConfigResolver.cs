using AchieveAi.LmDotnetTools.LmConfig.Models;
using AchieveAi.LmDotnetTools.LmCore.Models;

namespace AchieveAi.LmDotnetTools.LmConfig.Capabilities;

/// <summary>
///     <see cref="IModelCapacityResolver" /> backed by a loaded model catalog's <see cref="TokenLimits" />
///     (#681). The per-generation context observation needs a window to turn a token count into a
///     utilization; this is where the catalog's <c>token_limits</c> reach it through LmCore's narrow
///     abstraction, keeping the loop free of an LmConfig dependency.
/// </summary>
/// <remarks>
///     <para>
///         Indexed the way <see cref="Pricing.PricingConfigResolver.FromAppConfig" /> is: an observation
///         carries only the effective model id — the catalog id or a provider's <c>model_name</c>, depending
///         on which layer resolved it — so every name a model answers to is indexed. A name two models claim
///         with different limits is dropped: an absent window reads as "unknown" and shows no gauge, while a
///         confident wrong one drives a compaction decision.
///     </para>
///     <para>
///         A zero limit is what the configuration binder leaves when <c>token_limits</c> is omitted, and
///         reads as unknown rather than as a zero-sized window.
///     </para>
/// </remarks>
public sealed class ModelCapacityConfigResolver : IModelCapacityResolver
{
    private readonly IReadOnlyDictionary<string, ModelCapacity> _capacityByModel;

    /// <summary>Creates a resolver over a snapshot of per-model capacities.</summary>
    /// <param name="capacityByModel">Map of effective model id to its capacity.</param>
    public ModelCapacityConfigResolver(IReadOnlyDictionary<string, ModelCapacity> capacityByModel)
    {
        ArgumentNullException.ThrowIfNull(capacityByModel);
        _capacityByModel = capacityByModel;
    }

    /// <summary>Builds a resolver over the limits carried by a loaded model catalog.</summary>
    /// <param name="appConfig">The loaded model catalog.</param>
    public static ModelCapacityConfigResolver FromAppConfig(AppConfig appConfig)
    {
        ArgumentNullException.ThrowIfNull(appConfig);

        var candidates = new Dictionary<string, List<ModelCapacity>>(StringComparer.OrdinalIgnoreCase);

        void Offer(string alias, ModelCapacity capacity)
        {
            if (string.IsNullOrWhiteSpace(alias))
            {
                return;
            }

            if (!candidates.TryGetValue(alias, out var offered))
            {
                offered = [];
                candidates[alias] = offered;
            }

            offered.Add(capacity);
        }

        // Models / Providers are declared required, but the configuration binder does not enforce it
        // (see PricingConfigResolver.FromAppConfig), so a catalog bound from a section may leave them null.
        foreach (var model in appConfig.Models ?? [])
        {
            if (model.Capabilities?.TokenLimits is not { MaxContextTokens: > 0 } limits)
            {
                continue;
            }

            var capacity = new ModelCapacity(
                limits.MaxContextTokens,
                limits.MaxOutputTokens > 0 ? limits.MaxOutputTokens : null
            );
            Offer(model.Id, capacity);
            foreach (var provider in model.Providers ?? [])
            {
                Offer(provider.ModelName, capacity);
                foreach (var subProvider in provider.SubProviders ?? [])
                {
                    Offer(subProvider.ModelName, capacity);
                }
            }
        }

        var unambiguous = new Dictionary<string, ModelCapacity>(StringComparer.OrdinalIgnoreCase);
        foreach (var (alias, offered) in candidates)
        {
            var first = offered[0];
            if (offered.All(c => c == first))
            {
                unambiguous[alias] = first;
            }
        }

        return new ModelCapacityConfigResolver(unambiguous);
    }

    /// <inheritdoc />
    public ModelCapacity? Resolve(string modelId) =>
        !string.IsNullOrEmpty(modelId) && _capacityByModel.TryGetValue(modelId, out var capacity) ? capacity : null;
}
