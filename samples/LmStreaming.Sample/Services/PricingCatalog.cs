using AchieveAi.LmDotnetTools.LmConfig.Models;
using AchieveAi.LmDotnetTools.LmConfig.Pricing;
using AchieveAi.LmDotnetTools.LmConfig.Services;
using AchieveAi.LmDotnetTools.LmCore.Models;

namespace LmStreaming.Sample.Services;

/// <summary>
///     Composes this host's public-pricing catalog from configuration and registers it, so a usage record
///     produced by a run on this host carries an estimated public cost (#378).
///     <para>
///         This host is where the cost is actually stamped. The review daemon runs no agent loop of its own —
///         every review is driven over S2S into a conversation <b>here</b>, so
///         <c>UsageLedger.WithEstimatedCost</c> resolves against whatever <see cref="IPricingResolver" />
///         THIS process registered. Before #378 that was an appsettings-backed resolver over a
///         <c>Pricing:Models</c> section that no shipped configuration defined, so every review run's
///         <c>UsageRecord.EstimatedPublicCostMicros</c> was null.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         Configuration shape (<c>Pricing</c>), all optional:
///     </para>
///     <code>
///     "Pricing": {
///       "Version": "2026-08-01",
///       "Models": {
///         "&lt;catalog id&gt;": {
///           "PromptPerMillion": 3.0,
///           "CompletionPerMillion": 15.0,
///           "Aliases": [ "&lt;another name the same model answers to&gt;" ]
///         }
///       }
///     }
///     </code>
///     <para>
///         <b>Nothing is shipped in this repository's appsettings.</b> Rates are an operational fact with an
///         expiry date, not a code constant: they are re-negotiated, they differ per account, and a wrong one
///         is worse than an absent one because it is summed, reported and believed. An operator supplies them
///         (OpenRouter's public listing is a reasonable source) in <c>appsettings.&lt;Environment&gt;.json</c>
///         or via environment variables. With no section configured the catalog is empty and every cost
///         resolves null — "unavailable", which is the honest state and exactly the behaviour before #378.
///     </para>
///     <para>
///         <b>Cover the names the models actually answer to.</b> A usage record carries only
///         <c>UsageRecord.EffectiveModelId</c> — never the provider that served it — so
///         <see cref="PricingConfigResolver.FromAppConfig" /> indexes the catalog id and every provider
///         <c>model_name</c>. <c>Aliases</c> is how a host declares the other names one model is stamped
///         with; a catalog that misses the name a run is actually stamped with yields a null cost and looks
///         identical to no configuration at all.
///     </para>
///     <para>
///         <b>Version.</b> The catalog format carries no effective date, and nothing in this repository
///         refreshes a configured catalog — a host's rates are exactly as current as its config. So a cost
///         resolved without a version records where the rate came from but not how old it is.
///         <c>Pricing:Version</c> is that date, stamped onto every <see cref="ModelPricing" /> this host
///         resolves. LmConfig's own <c>AddLmConfig</c> cannot supply it (it has no way to know), which is
///         why the authoritative resolver is registered here rather than left to that call's <c>TryAdd</c>.
///     </para>
///     <para>
///         What a partly-priced catalog does downstream: a conversation total is a STRICT fold
///         (<c>ConversationUsageAggregate</c> line 146, #377), so a single unpriced model nulls the whole
///         conversation's cost rather than under-reporting it. Per-model subtotals keep the lenient fold,
///         since pricing is uniform within one model. The practical consequence for this section is that
///         pricing SOME of the models a host runs still leaves conversation totals null — an all-or-nothing
///         boundary, and the honest one.
///     </para>
/// </remarks>
public static class PricingCatalog
{
    /// <summary>Configuration section this host reads its rates from.</summary>
    public const string SectionName = "Pricing";

    /// <summary>Provenance stamped on every rate resolved from that section.</summary>
    public const string Source = "appsettings:Pricing";

    /// <summary>Provider name used for the synthetic catalog entries a configured rate is expressed as.</summary>
    private const string ConfiguredProviderName = "configured";

    /// <summary>
    ///     Registers the configured catalog with LmConfig and, over it, the authoritative
    ///     <see cref="IPricingResolver" /> carrying this host's catalog version. Registration order matters:
    ///     <c>AddLmConfig</c> fills its resolver in with <c>TryAdd</c> precisely so a host that knows better
    ///     can override it, and the version is the thing only the host knows.
    /// </summary>
    public static IServiceCollection AddConfiguredPricing(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var catalog = BuildCatalog(configuration);
        var version = ReadVersion(configuration);

        // AddLmConfig registers MORE than pricing, and the extra registrations are the reason to read this
        // comment before adding a second caller. It also fills in IAgent/IStreamingAgent (a UnifiedAgent over
        // this catalog), IModelResolver, IProviderAgentFactory, OpenRouterModelService, and an
        // IHttpHandlerBuilder rewrite. Over an EMPTY or pricing-only catalog those resolve to an agent that
        // throws on first use rather than one that works — a method named for pricing quietly supplying a
        // broken default agent.
        // Inert as wired today: nothing in src/ or samples/ resolves IAgent, IStreamingAgent, IModelResolver,
        // IProviderAgentFactory or IHttpHandlerBuilder from this host's container — it builds its agents through
        // its own provider path. If that ever stops being true, the fix is to register the pricing pieces
        // directly rather than to widen this catalog into a real agent catalog by accident.
        _ = services.AddLmConfig(catalog);
        _ = services.AddSingleton<IPricingResolver>(_ =>
            PricingConfigResolver.FromAppConfig(catalog, Source, version));

        return services;
    }

    /// <summary>
    ///     Builds the <see cref="AppConfig" /> catalog from the <c>Pricing:Models</c> section. Each child key
    ///     is a model id; an entry missing either rate is skipped, because half a rate cannot produce a cost
    ///     and a zero substituted for the missing half would produce a wrong one. An absent section yields an
    ///     empty catalog.
    /// </summary>
    public static AppConfig BuildCatalog(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var models = new List<ModelConfig>();
        foreach (var entry in configuration.GetSection($"{SectionName}:Models").GetChildren())
        {
            var prompt = entry.GetValue<double?>("PromptPerMillion");
            var completion = entry.GetValue<double?>("CompletionPerMillion");
            if (prompt is null || completion is null)
            {
                continue;
            }

            var pricing = new PricingConfig
            {
                PromptPerMillion = prompt.Value,
                CompletionPerMillion = completion.Value,
            };

            // One provider entry per name the model answers to, all carrying the SAME rate. The resolver
            // indexes provider model_names alongside the catalog id, so this is how an alias becomes
            // resolvable; identical rates mean the aliases agree and none is dropped as ambiguous.
            var names = new List<string> { entry.Key };
            names.AddRange(
                entry.GetSection("Aliases").GetChildren()
                    .Select(a => a.Value)
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .Select(a => a!));

            models.Add(new ModelConfig
            {
                Id = entry.Key,
                Providers =
                [
                    .. names.Select(name => new ProviderConfig
                    {
                        Name = ConfiguredProviderName,
                        ModelName = name,
                        Pricing = pricing,
                    }),
                ],
            });
        }

        return new AppConfig { Models = models };
    }

    /// <summary>
    ///     Reads <c>Pricing:Version</c> — the effective date of the configured rates — or null when the host
    ///     declares none, in which case a resolved cost records its source but not its age.
    /// </summary>
    public static string? ReadVersion(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var version = configuration[$"{SectionName}:Version"];
        return string.IsNullOrWhiteSpace(version) ? null : version;
    }
}
