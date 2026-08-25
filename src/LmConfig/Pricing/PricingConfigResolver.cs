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

    /// <summary>
    ///     Builds a resolver over the rates already carried by a loaded model catalog (#328).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the producer the type was missing. <see cref="PricingConfig" /> rates hang off
    ///         <c>AppConfig.Models[].Providers[]</c> (and their sub-providers), so building the flat
    ///         model-id map the constructor wants meant re-implementing LmConfig's own pricing precedence in
    ///         every host. Nobody did, which is why nothing ever constructed this resolver and
    ///         <c>UsageRecord.EstimatedPublicCostMicros</c> was always null.
    ///     </para>
    ///     <para>
    ///         A record reaching <see cref="Resolve" /> carries only a model name
    ///         (<c>UsageRecord.EffectiveModelId</c>) — never the provider that served it. So every name a
    ///         model answers to is indexed: the catalog id, each provider's <c>model_name</c>, and each
    ///         sub-provider's. Where one name is priced two different ways — or where one of the routes it
    ///         names published no rate at all — the name is <b>dropped</b> and <see cref="Resolve" /> returns
    ///         null. An absent estimate is visible and recoverable; a confident wrong one is neither, because
    ///         downstream it is summed, reported and believed.
    ///     </para>
    ///     <para>
    ///         The catalog format carries no effective date, so <paramref name="version" /> has to come from
    ///         the caller. Left null, a resolved <see cref="ModelPricing" /> records where the rate came from
    ///         but not how old it is — callers that need staleness detection must supply one.
    ///     </para>
    /// </remarks>
    /// <param name="appConfig">The loaded model catalog.</param>
    /// <param name="source">Optional catalog source recorded on resolved pricing for provenance.</param>
    /// <param name="version">Optional catalog version / effective date recorded for provenance.</param>
    public static PricingConfigResolver FromAppConfig(
        AppConfig appConfig,
        string? source = null,
        string? version = null)
    {
        ArgumentNullException.ThrowIfNull(appConfig);

        var candidates = new Dictionary<string, List<PricingConfig>>(StringComparer.OrdinalIgnoreCase);
        var unpriced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Offer(string alias, PricingConfig? pricing)
        {
            if (string.IsNullOrWhiteSpace(alias))
            {
                return;
            }

            if (pricing is null)
            {
                // A route that published no rate is an offer that can never agree with any other, so the
                // alias is dropped exactly as a conflicting one is. Skipping the offer instead would let
                // the alias resolve to whatever the *other* providers agree on — a confident number for a
                // request that may have been billed at the rate nobody declared.
                _ = unpriced.Add(alias);
                return;
            }

            if (!candidates.TryGetValue(alias, out var offered))
            {
                offered = [];
                candidates[alias] = offered;
            }

            offered.Add(pricing);
        }

        // Models / Providers / Pricing are declared required, but Microsoft.Extensions.Configuration.Binder
        // does not enforce `required` the way System.Text.Json does, so a catalog bound from an
        // IConfiguration section leaves any of them null (AppConfig.GetModel guards the same way). Defend
        // rather than throw on a host that registered LmConfig without a catalog, or with a provider entry
        // that omits `pricing`.
        foreach (var model in appConfig.Models ?? [])
        {
            foreach (var provider in model.Providers ?? [])
            {
                Offer(model.Id, provider.Pricing);
                Offer(provider.ModelName, provider.Pricing);

                foreach (var subProvider in provider.SubProviders ?? [])
                {
                    // Mirrors ProviderResolution.EffectivePricing: a sub-provider's own rates are what a
                    // request routed through it is billed at, so they are what its name resolves to.
                    Offer(model.Id, subProvider.Pricing);
                    Offer(subProvider.ModelName, subProvider.Pricing);
                }
            }
        }

        var unambiguous = new Dictionary<string, PricingConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var (alias, offered) in candidates)
        {
            if (unpriced.Contains(alias))
            {
                continue;
            }

            var first = offered[0];
            var agrees = offered.All(p =>
                p.PromptPerMillion == first.PromptPerMillion && p.CompletionPerMillion == first.CompletionPerMillion);

            if (agrees)
            {
                unambiguous[alias] = first;
            }
        }

        return new PricingConfigResolver(unambiguous, source, version);
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
