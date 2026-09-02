namespace AchieveAi.LmDotnetTools.LmCore.Models;

/// <summary>
///     What a model can hold: its context window and, when known, its output ceiling (#681; spec 679 §4.2).
/// </summary>
/// <param name="WindowTokens">The model's context window in tokens.</param>
/// <param name="MaxOutputTokens">The model's output ceiling, when the catalog states one.</param>
public sealed record ModelCapacity(long WindowTokens, long? MaxOutputTokens);

/// <summary>
///     Resolves a model's capacity, or null when the catalog does not know the model — in which case an
///     observation carries no window, its utilization is null, and the UI shows <i>unknown</i> rather than
///     a number (spec 679 §7.1). Sibling of <see cref="IPricingResolver" />.
/// </summary>
public interface IModelCapacityResolver
{
    /// <summary>The capacity of <paramref name="modelId" />, or null when unknown.</summary>
    ModelCapacity? Resolve(string modelId);
}
