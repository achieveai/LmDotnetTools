using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmCore.Models;

/// <summary>Applies a different rate to every token of a request above the input threshold.</summary>
/// <param name="InputTokensGreaterThan">Exclusive threshold on total billed input, including cached input.</param>
/// <param name="InputMultiplier">Multiplier for uncached input, reads, and writes.</param>
/// <param name="OutputMultiplier">Multiplier for output, including reasoning.</param>
public sealed record LongContextPricing(
    [property: JsonPropertyName("input_tokens_greater_than")] int InputTokensGreaterThan,
    [property: JsonPropertyName("input_multiplier")] decimal InputMultiplier,
    [property: JsonPropertyName("output_multiplier")] decimal OutputMultiplier
);
