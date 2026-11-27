using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmCore.Models;

/// <summary>A scheduled change to the configured rates.</summary>
/// <param name="EffectiveDate">First UTC date when the new rates apply.</param>
/// <param name="Multiplier">Multiplier for every token rate.</param>
public sealed record PricingRateChange(
    [property: JsonPropertyName("effective_date")] DateOnly EffectiveDate,
    [property: JsonPropertyName("multiplier")] decimal Multiplier
);
