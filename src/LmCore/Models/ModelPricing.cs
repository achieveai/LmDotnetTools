namespace AchieveAi.LmDotnetTools.LmCore.Models;

/// <summary>
///     How a provider reports cache tokens relative to <see cref="UsageRecord.InputTokens" />. The two
///     conventions differ in what the base input count already contains, so the same record priced under
///     the wrong mode is either double-counted or under-counted (#682).
/// </summary>
public enum CacheAccounting
{
    /// <summary>
    ///     OpenAI convention: <c>cached_tokens</c> is a subset of <c>prompt_tokens</c>. Uncached input is
    ///     <c>InputTokens − CacheReadTokens</c>. This is the default, matching the normative semantics
    ///     documented on <see cref="UsageRecord" />.
    /// </summary>
    SubsetOfInput,

    /// <summary>
    ///     Anthropic convention: <c>input_tokens</c> EXCLUDES <c>cache_read_input_tokens</c> and
    ///     <c>cache_creation_input_tokens</c>. Every category is billed in addition to the base count.
    /// </summary>
    Additive,
}

/// <summary>
///     Whether a public-price estimate covers every billed category of the record it was computed for.
/// </summary>
/// <remarks>
///     The zero value is deliberately <see cref="Unavailable" />, not <see cref="Complete" />: usage rows are
///     persisted as reflection-serialized JSON, and a row written before this field existed carries a
///     two-category estimate (input + output only). Deserializing it must not label that old estimate
///     complete; the seed path re-derives <see cref="Partial" /> from the populated figure instead.
/// </remarks>
public enum CostCompleteness
{
    /// <summary>No estimate could be produced (unknown model, or no category could be priced).</summary>
    Unavailable = 0,

    /// <summary>
    ///     Some category with tokens had no rate, or a rate dimension is ambiguous (cache-write TTL). The
    ///     figure, when present, is a lower bound over the priced categories — never an exact total.
    /// </summary>
    Partial,

    /// <summary>Every category with tokens was priced at a known rate.</summary>
    Complete,
}

/// <summary>
///     Result of <see cref="ModelPricing.Estimate" />: the micro-unit figure (null when nothing could be
///     priced), how complete it is, and which categories it could not price.
/// </summary>
/// <param name="Micros">
///     Estimated cost in micro-units of the pricing's currency, or null when no category with tokens could be
///     priced. For a <see cref="CostCompleteness.Partial" /> estimate this is the sum of the priced categories
///     only — a lower bound, never a total.
/// </param>
/// <param name="Completeness">Whether every category with tokens was priced.</param>
/// <param name="MissingCategories">
///     Stable identifiers of the unpriced or ambiguous dimensions, each listed once:
///     <c>cache_read</c>, <c>cache_write</c>, <c>cache_write_ttl_unknown</c>, <c>cache_accounting_mismatch</c>.
///     Empty when <see cref="Completeness" /> is <see cref="CostCompleteness.Complete" />.
/// </param>
public sealed record CostEstimate(long? Micros, CostCompleteness Completeness, IReadOnlyList<string> MissingCategories)
{
    /// <summary>The estimate for a record whose model has no public pricing at all.</summary>
    public static CostEstimate Unavailable { get; } = new(null, CostCompleteness.Unavailable, []);

    /// <summary>Cache-read tokens present, no cache-read rate.</summary>
    public const string MissingCacheRead = "cache_read";

    /// <summary>Cache-write tokens present, no rate for (some of) them.</summary>
    public const string MissingCacheWrite = "cache_write";

    /// <summary>
    ///     Cache-write tokens present but the provider did not report the 5m/1h split, so they were priced at
    ///     the 5m rate as a lower bound.
    /// </summary>
    public const string CacheWriteTtlUnknown = "cache_write_ttl_unknown";

    /// <summary>
    ///     The record contradicts the pricing's <see cref="CacheAccounting" /> (more cache reads than input
    ///     under <see cref="CacheAccounting.SubsetOfInput" />); uncached input was clamped to zero.
    /// </summary>
    public const string CacheAccountingMismatch = "cache_accounting_mismatch";
}

/// <summary>
///     Immutable per-model public pricing snapshot used to estimate cost from token counts. Captured at
///     execution time so a later catalog change never silently reprices a historical conversation (#196).
///     Rates are per one million tokens; money is computed in integer micro-units (1e-6 of
///     <see cref="Currency" />) for deterministic arithmetic.
/// </summary>
/// <remarks>
///     The base rates (<see cref="PromptPerMillion" />, <see cref="CompletionPerMillion" />) are required. The
///     category rates added by #682 are optional: a null rate for a category that has tokens makes
///     <see cref="Estimate" /> <see cref="CostCompleteness.Partial" /> rather than pricing that category at
///     the base rate or at zero, so a two-rate entry from before #682 keeps loading and is honest about what
///     it cannot price.
/// </remarks>
public sealed record ModelPricing
{
    /// <summary>The model these rates apply to.</summary>
    public required string ModelId { get; init; }

    /// <summary>Cost per one million uncached input (prompt) tokens, in <see cref="Currency" /> units.</summary>
    public required decimal PromptPerMillion { get; init; }

    /// <summary>Cost per one million output (completion) tokens, in <see cref="Currency" /> units.</summary>
    public required decimal CompletionPerMillion { get; init; }

    /// <summary>Cost per one million cache-read (cache hit) tokens, or null when the catalog carries none.</summary>
    public decimal? CacheReadPerMillion { get; init; }

    /// <summary>Cost per one million cache-write tokens with a 5-minute TTL, or null when unknown.</summary>
    public decimal? CacheWrite5mPerMillion { get; init; }

    /// <summary>Cost per one million cache-write tokens with a 1-hour TTL, or null when unknown.</summary>
    public decimal? CacheWrite1hPerMillion { get; init; }

    /// <summary>
    ///     Cost per one million reasoning tokens, or null when reasoning is billed as ordinary completion
    ///     output (every current provider).
    /// </summary>
    public decimal? ReasoningPerMillion { get; init; }

    /// <summary>
    ///     How the provider that produced the usage reports cache tokens relative to the base input count.
    ///     Defaults to <see cref="CacheAccounting.SubsetOfInput" />, the semantics <see cref="UsageRecord" />
    ///     documents; Anthropic entries must declare <see cref="CacheAccounting.Additive" />.
    /// </summary>
    public CacheAccounting CacheAccounting { get; init; } = CacheAccounting.SubsetOfInput;

    /// <summary>Date the rates were taken from the vendor's published list, for re-verification.</summary>
    public DateOnly? EffectiveDate { get; init; }

    /// <summary>ISO currency code for the rates.</summary>
    public string Currency { get; init; } = "USD";

    /// <summary>Pricing catalog source (e.g. provider name or "openrouter").</summary>
    public string? Source { get; init; }

    /// <summary>Catalog version or effective date, for provenance.</summary>
    public string? Version { get; init; }

    /// <summary>
    ///     Estimates the two-category cost of the given token counts in micro-units. Because a per-million
    ///     rate times a token count already yields micro-units (tokens × rate ÷ 1e6 × 1e6), this is simply
    ///     <c>input × PromptPerMillion + output × CompletionPerMillion</c>, rounded half-to-even.
    /// </summary>
    /// <remarks>
    ///     Prices every input token at the base rate and knows nothing about cache or reasoning categories.
    ///     The accounting layer uses <see cref="Estimate" />; this remains for callers that have only two
    ///     counts.
    /// </remarks>
    public long EstimateMicros(long inputTokens, long outputTokens)
    {
        var micros = (inputTokens * PromptPerMillion) + (outputTokens * CompletionPerMillion);
        return RoundMicros(micros);
    }

    /// <summary>
    ///     Prices every billed category of <paramref name="record" /> — uncached input, cache reads, cache
    ///     writes per TTL, plain output and reasoning — under this pricing's <see cref="CacheAccounting" />,
    ///     without double counting, and reports which categories it could not price (#682).
    /// </summary>
    /// <remarks>
    ///     <list type="bullet">
    ///         <item>
    ///             A category with tokens and no rate is never priced at the base rate and never at zero: it
    ///             is left out of the figure and named in <see cref="CostEstimate.MissingCategories" />, making
    ///             the estimate <see cref="CostCompleteness.Partial" />.
    ///         </item>
    ///         <item>
    ///             Cache writes whose TTL split is unknown (<see cref="UsageRecord.CacheWrite1hTokens" /> null)
    ///             are priced at the 5m rate — the lower bound — and flagged
    ///             <see cref="CostEstimate.CacheWriteTtlUnknown" />.
    ///         </item>
    ///         <item>Reasoning tokens fall back to the completion rate when <see cref="ReasoningPerMillion" /> is null.</item>
    ///         <item>
    ///             The total is rounded once, half-to-even, so the same record always yields the same integer
    ///             regardless of how many categories contributed.
    ///         </item>
    ///         <item>
    ///             When nothing with tokens could be priced the figure is null rather than zero; zero is
    ///             reserved for a record that genuinely has no tokens.
    ///         </item>
    ///     </list>
    /// </remarks>
    public CostEstimate Estimate(UsageRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var missing = new List<string>();
        var micros = 0m;
        var pricedAny = false;

        void Price(long tokens, decimal? rate, string missingCategory)
        {
            if (tokens <= 0)
            {
                return;
            }

            if (rate is null)
            {
                if (!missing.Contains(missingCategory, StringComparer.Ordinal))
                {
                    missing.Add(missingCategory);
                }

                return;
            }

            micros += tokens * rate.Value;
            pricedAny = true;
        }

        // Uncached input: what the base rate applies to depends on the provider's accounting convention.
        var uncachedInput = record.InputTokens;
        if (CacheAccounting == CacheAccounting.SubsetOfInput)
        {
            uncachedInput -= record.CacheReadTokens;
            if (uncachedInput < 0)
            {
                // The record has more cache reads than input, which a subset cannot. The entry's accounting
                // mode does not match the provider that produced the record; clamp and say so rather than
                // subtracting into a negative cost.
                uncachedInput = 0;
                missing.Add(CostEstimate.CacheAccountingMismatch);
            }
        }

        Price(uncachedInput, PromptPerMillion, "input");
        Price(record.CacheReadTokens, CacheReadPerMillion, CostEstimate.MissingCacheRead);

        if (record.CacheWriteTokens > 0)
        {
            if (record.CacheWrite1hTokens is null)
            {
                // The provider did not report the TTL split. The 5m rate is the cheaper of the two, so
                // pricing everything at it is a lower bound — and the estimate is labelled Partial for it.
                Price(record.CacheWriteTokens, CacheWrite5mPerMillion, CostEstimate.MissingCacheWrite);
                missing.Add(CostEstimate.CacheWriteTtlUnknown);
            }
            else
            {
                var oneHour = Math.Min(Math.Max(record.CacheWrite1hTokens.Value, 0), record.CacheWriteTokens);
                Price(record.CacheWriteTokens - oneHour, CacheWrite5mPerMillion, CostEstimate.MissingCacheWrite);
                Price(oneHour, CacheWrite1hPerMillion, CostEstimate.MissingCacheWrite);
            }
        }

        // Reasoning is a subset of output; the split only matters when it has its own rate.
        var reasoning = Math.Min(Math.Max(record.ReasoningTokens, 0), record.OutputTokens);
        Price(record.OutputTokens - reasoning, CompletionPerMillion, "output");
        Price(reasoning, ReasoningPerMillion ?? CompletionPerMillion, "reasoning");

        if (missing.Count == 0)
        {
            return new CostEstimate(RoundMicros(micros), CostCompleteness.Complete, []);
        }

        return new CostEstimate(pricedAny ? RoundMicros(micros) : null, CostCompleteness.Partial, missing);
    }

    /// <summary>The single rounding rule for money: one half-to-even rounding of the total, to integer micros.</summary>
    private static long RoundMicros(decimal micros) => (long)decimal.Round(micros, MidpointRounding.ToEven);
}

/// <summary>
///     Narrow abstraction that resolves a model id to its <see cref="ModelPricing" />. Lets the usage
///     accounting layer estimate public cost without taking a direct dependency on the configuration
///     library (#196).
/// </summary>
public interface IPricingResolver
{
    /// <summary>Resolves pricing for a model, or null when no public pricing is available for it.</summary>
    ModelPricing? Resolve(string modelId);
}
