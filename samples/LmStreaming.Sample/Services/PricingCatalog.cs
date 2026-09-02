using System.Globalization;
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
///       "Version": "2026-09-02",
///       "Models": {
///         "&lt;catalog id&gt;": {
///           "PromptPerMillion": 3.0,
///           "CompletionPerMillion": 15.0,
///           "CacheReadPerMillion": 0.30,
///           "CacheWrite5mPerMillion": 3.75,
///           "CacheWrite1hPerMillion": 6.0,
///           "ReasoningPerMillion": null,
///           "CacheAccounting": "Additive",
///           "EffectiveDate": "2026-09-02",
///           "_source": "https://vendor.example/pricing",
///           "Aliases": [ "&lt;another name the same model answers to&gt;" ]
///         }
///       }
///     }
///     </code>
///     <para>
///         The two base rates are required. The per-category rates (#682) are optional: a category left out
///         is not priced at the base rate and not priced at zero — a record with tokens in it gets a
///         <see cref="CostCompleteness.Partial" /> estimate naming the gap. <c>CacheAccounting</c> is
///         <c>SubsetOfInput</c> (OpenAI: cached tokens are inside the prompt count) or <c>Additive</c>
///         (Anthropic: <c>input_tokens</c> excludes them); it changes the arithmetic, so a misspelt value
///         rejects the entry rather than falling back. <c>EffectiveDate</c> (<c>yyyy-MM-dd</c>) and
///         <c>_source</c> (ignored by the binder) are how the next person re-verifies a rate.
///     </para>
///     <para>
///         <b>What is shipped in this repository's appsettings, and why only that.</b> Rates are an
///         operational fact with an expiry date, not a code constant, and a wrong one is worse than an absent
///         one because it is summed, reported and believed (#378). So the shipped section carries only public
///         list prices copied from a vendor's own pricing page, each dated and cited — a cited, dated rate is
///         not a guess — and only for the ids this sample's per-token API providers default to. Ids served
///         over a subscription transport (Copilot, the Claude CLI, Codex) are deliberately absent: there is no
///         per-token price to cite, so their cost resolves null, "unavailable", which is the honest state.
///         The citation list lives in <c>docs/features/public-pricing-catalog.md</c>. Operators override or
///         extend the section in <c>appsettings.&lt;Environment&gt;.json</c> or via environment variables.
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
        IConfiguration configuration
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var rejected = new List<string>();
        var catalog = BuildCatalog(configuration, rejected);
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
        _ = services.AddSingleton<IPricingResolver>(sp =>
        {
            // Reported here rather than at BuildCatalog time because this method runs while the container is
            // being described — there is no ILogger to resolve yet. The resolver's own factory is the first
            // moment one exists AND the first moment a dropped rate can actually be observed as a null cost,
            // so an operator reading the log sees the explanation next to the symptom.
            ReportRejectedRates(sp.GetService<ILoggerFactory>(), rejected);
            return PricingConfigResolver.FromAppConfig(catalog, Source, version);
        });

        return services;
    }

    /// <summary>
    ///     Builds the <see cref="AppConfig" /> catalog from the <c>Pricing:Models</c> section. Each child key
    ///     is a model id; an entry whose base rates are not both USABLE is skipped, because half a rate cannot
    ///     produce a cost and a zero substituted for the missing half would produce a wrong one. "Usable" is
    ///     present, finite and non-negative — see <see cref="IsUsableRate" /> for why zero is admitted and the
    ///     other three are not. A category rate (#682) may be ABSENT — that is what makes an estimate Partial
    ///     — but one that is present and unusable, an unknown <c>CacheAccounting</c>, or an unparseable
    ///     <c>EffectiveDate</c> rejects the whole entry: dropping only the bad key would leave a Partial
    ///     estimate that looks like a provider gap rather than an operator typo. An absent section yields an
    ///     empty catalog.
    /// </summary>
    /// <param name="configuration">Host configuration to read <c>Pricing:Models</c> from.</param>
    /// <param name="rejected">
    ///     Optional sink receiving one operator-readable sentence per skipped entry, so the drop can be
    ///     reported rather than left to be inferred from a null cost.
    /// </param>
    public static AppConfig BuildCatalog(IConfiguration configuration, ICollection<string>? rejected = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var models = new List<ModelConfig>();
        foreach (var entry in configuration.GetSection($"{SectionName}:Models").GetChildren())
        {
            var prompt = entry.GetValue<double?>("PromptPerMillion");
            var completion = entry.GetValue<double?>("CompletionPerMillion");
            var promptUsable = IsUsableRate(prompt, out var promptRate);
            var completionUsable = IsUsableRate(completion, out var completionRate);
            if (!promptUsable || !completionUsable)
            {
                rejected?.Add(
                    $"'{entry.Key}' has PromptPerMillion={Describe(prompt)} and "
                        + $"CompletionPerMillion={Describe(completion)}; a rate must be present, finite and "
                        + "not negative (zero is allowed, for a free model)"
                );
                continue;
            }

            if (
                !TryReadCategoryRate(entry, "CacheReadPerMillion", rejected, out var cacheRead)
                || !TryReadCategoryRate(entry, "CacheWrite5mPerMillion", rejected, out var cacheWrite5m)
                || !TryReadCategoryRate(entry, "CacheWrite1hPerMillion", rejected, out var cacheWrite1h)
                || !TryReadCategoryRate(entry, "ReasoningPerMillion", rejected, out var reasoning)
                || !TryReadCacheAccounting(entry, rejected, out var cacheAccounting)
                || !TryReadEffectiveDate(entry, rejected, out var effectiveDate)
            )
            {
                continue;
            }

            var pricing = new PricingConfig
            {
                PromptPerMillion = promptRate,
                CompletionPerMillion = completionRate,
                CacheReadPerMillion = cacheRead,
                CacheWrite5mPerMillion = cacheWrite5m,
                CacheWrite1hPerMillion = cacheWrite1h,
                ReasoningPerMillion = reasoning,
                CacheAccounting = cacheAccounting,
                EffectiveDate = effectiveDate,
            };

            // One provider entry per name the model answers to, all carrying the SAME rate. The resolver
            // indexes provider model_names alongside the catalog id, so this is how an alias becomes
            // resolvable; identical rates mean the aliases agree and none is dropped as ambiguous.
            var names = new List<string> { entry.Key };
            names.AddRange(
                entry
                    .GetSection("Aliases")
                    .GetChildren()
                    .Select(a => a.Value)
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .Select(a => a!)
            );

            models.Add(
                new ModelConfig
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
                }
            );
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

    /// <summary>
    ///     Warns once per unusable entry. A silent skip leaves the operator with a null cost that looks exactly
    ///     like a model they never configured — the state #378 was filed against — so the drop is announced.
    /// </summary>
    private static void ReportRejectedRates(ILoggerFactory? loggerFactory, IReadOnlyList<string> rejected)
    {
        if (rejected.Count == 0 || loggerFactory is null)
        {
            return;
        }

        var logger = loggerFactory.CreateLogger(typeof(PricingCatalog).FullName!);
        foreach (var reason in rejected)
        {
            logger.LogWarning(
                "Pricing entry dropped: {Reason}. Its models resolve to no cost at all rather than to a wrong one.",
                reason
            );
        }
    }

    /// <summary>
    ///     A rate this host is willing to sum: present, finite, and not negative. Zero is deliberately
    ///     ADMITTED — a free model has a knowable rate of zero, and "$0.00, priced" is a different fact from
    ///     "cost unavailable"; rejecting it would be the over-eager fix that breaks free models. Negative, NaN
    ///     and infinity are refused for the same reason half a rate is: a wrong number is worse than an absent
    ///     one because it is summed, reported and believed. NaN and infinity additionally have no decimal
    ///     representation, so <c>PricingConfigResolver</c>'s cast would throw and one mistyped entry would take
    ///     pricing down for every model the host runs, not just the mistyped one.
    /// </summary>
    private static bool IsUsableRate(double? rate, out double value)
    {
        value = rate.GetValueOrDefault();
        return rate.HasValue && double.IsFinite(value) && value >= 0;
    }

    /// <summary>
    ///     Reads an optional per-category rate (#682). Absent is fine and yields null — the category is then
    ///     reported as unpriced on any estimate that needs it. Present-but-unusable is the same operator error
    ///     as an unusable base rate and is refused the same way.
    /// </summary>
    private static bool TryReadCategoryRate(
        IConfigurationSection entry,
        string key,
        ICollection<string>? rejected,
        out double? rate
    )
    {
        rate = null;
        var raw = entry[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var parsed = entry.GetValue<double?>(key);
        if (!IsUsableRate(parsed, out var value))
        {
            rejected?.Add(
                $"'{entry.Key}' has {key}={Describe(parsed)}; a category rate may be omitted, but one that "
                    + "is present must be finite and not negative"
            );
            return false;
        }

        rate = value;
        return true;
    }

    /// <summary>
    ///     Reads <c>CacheAccounting</c>: absent defaults to <see cref="CacheAccounting.SubsetOfInput" />, any
    ///     other value must name a member exactly. The mode changes the arithmetic (Additive vs subset), so a
    ///     misspelt value silently taking the default would price every Anthropic cache read twice.
    /// </summary>
    private static bool TryReadCacheAccounting(
        IConfigurationSection entry,
        ICollection<string>? rejected,
        out CacheAccounting accounting
    )
    {
        accounting = CacheAccounting.SubsetOfInput;
        var raw = entry["CacheAccounting"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (Enum.TryParse(raw, ignoreCase: true, out accounting) && Enum.IsDefined(accounting))
        {
            return true;
        }

        rejected?.Add(
            $"'{entry.Key}' has CacheAccounting='{raw}'; expected one of "
                + $"{string.Join(", ", Enum.GetNames<CacheAccounting>())}"
        );
        return false;
    }

    /// <summary>Reads <c>EffectiveDate</c> as an ISO <c>yyyy-MM-dd</c> date; absent yields null.</summary>
    private static bool TryReadEffectiveDate(
        IConfigurationSection entry,
        ICollection<string>? rejected,
        out DateOnly? effectiveDate
    )
    {
        effectiveDate = null;
        var raw = entry["EffectiveDate"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (
            DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
        )
        {
            effectiveDate = parsed;
            return true;
        }

        rejected?.Add($"'{entry.Key}' has EffectiveDate='{raw}'; expected an ISO date (yyyy-MM-dd)");
        return false;
    }

    /// <summary>Renders a rate for the operator, naming an absent one rather than printing an empty string.</summary>
    private static string Describe(double? rate) =>
        rate is { } value ? value.ToString(CultureInfo.InvariantCulture) : "(absent)";
}
