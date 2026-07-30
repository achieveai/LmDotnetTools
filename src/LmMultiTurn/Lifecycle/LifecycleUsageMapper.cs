using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmLifecycle;
using AchieveAi.LmDotnetTools.LmLifecycle.Payloads;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;

/// <summary>
/// Converts the core <see cref="Usage"/> a provider reported into the lifecycle wire shape.
/// </summary>
/// <remarks>
/// The whole job here is to copy and not to compute. Providers disagree about what their fields
/// mean — some report total input tokens as the prompt count, others report only the uncached part —
/// so deriving one number from the others produces a figure that is wrong for at least one provider
/// and indistinguishable from a real measurement once it is on the wire.
/// </remarks>
internal static class LifecycleUsageMapper
{
    /// <summary>
    /// Maps reported usage, or returns null when there was none to report.
    /// </summary>
    /// <param name="usage">What the provider reported.</param>
    /// <param name="completeness">
    /// How complete the measurement is. See <see cref="LifecycleUsageCompleteness"/>.
    /// </param>
    /// <remarks>
    /// The detail counts are read through their nullable owners rather than through
    /// <see cref="Usage.TotalCachedTokens"/> and <see cref="Usage.TotalReasoningTokens"/>, which
    /// coerce a missing detail block to zero. On the wire those fields are nullable precisely so a
    /// consumer can tell "the provider does not report this" from "the provider reports none", and
    /// flattening both to 0 would destroy that distinction at the only point it can be observed.
    /// </remarks>
    public static LifecycleUsage? ToLifecycleUsage(
        Usage? usage,
        string completeness = LifecycleUsageCompleteness.Complete)
    {
        if (usage == null)
        {
            return null;
        }

        return new LifecycleUsage
        {
            PromptTokens = usage.PromptTokens,
            CompletionTokens = usage.CompletionTokens,
            TotalTokens = usage.TotalTokens,
            CachedPromptTokens = usage.InputTokenDetails?.CachedTokens,
            ReasoningTokens = usage.OutputTokenDetails?.ReasoningTokens,
            Completeness = completeness,
        };
    }
}
