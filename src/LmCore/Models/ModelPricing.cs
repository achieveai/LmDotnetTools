namespace AchieveAi.LmDotnetTools.LmCore.Models;

/// <summary>
///     Immutable per-model public pricing snapshot used to estimate cost from token counts. Captured at
///     execution time so a later catalog change never silently reprices a historical conversation (#196).
///     Rates are per one million tokens; money is computed in integer micro-units (1e-6 of
///     <see cref="Currency" />) for deterministic arithmetic.
/// </summary>
public sealed record ModelPricing
{
    /// <summary>The model these rates apply to.</summary>
    public required string ModelId { get; init; }

    /// <summary>Cost per one million input (prompt) tokens, in <see cref="Currency" /> units.</summary>
    public required decimal PromptPerMillion { get; init; }

    /// <summary>Cost per one million output (completion) tokens, in <see cref="Currency" /> units.</summary>
    public required decimal CompletionPerMillion { get; init; }

    /// <summary>ISO currency code for the rates.</summary>
    public string Currency { get; init; } = "USD";

    /// <summary>Pricing catalog source (e.g. provider name or "openrouter").</summary>
    public string? Source { get; init; }

    /// <summary>Catalog version or effective date, for provenance.</summary>
    public string? Version { get; init; }

    /// <summary>
    ///     Estimates the cost of the given token counts in micro-units. Because a per-million rate times a
    ///     token count already yields micro-units (tokens × rate ÷ 1e6 × 1e6), this is simply
    ///     <c>input × PromptPerMillion + output × CompletionPerMillion</c>, rounded half-to-even.
    /// </summary>
    public long EstimateMicros(long inputTokens, long outputTokens)
    {
        var micros = (inputTokens * PromptPerMillion) + (outputTokens * CompletionPerMillion);
        return (long)decimal.Round(micros, MidpointRounding.ToEven);
    }
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
