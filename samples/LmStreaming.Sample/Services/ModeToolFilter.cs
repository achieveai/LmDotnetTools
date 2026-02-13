using AchieveAi.LmDotnetTools.AnthropicProvider.Models;

namespace LmStreaming.Sample.Services;

/// <summary>
/// Utility for filtering provider built-in tools based on chat mode configuration.
/// </summary>
public static class ModeToolFilter
{
    /// <summary>
    /// Returns built-in tools allowed for the active mode.
    /// </summary>
    /// <param name="allBuiltInTools">All provider built-in tools configured for the current provider.</param>
    /// <param name="enabledTools">
    /// Mode-enabled tool names. Null means "all tools enabled".
    /// </param>
    /// <returns>Filtered built-in tools, or null when none should be sent.</returns>
    public static List<object>? FilterBuiltInTools(
        List<object>? allBuiltInTools,
        IReadOnlyList<string>? enabledTools)
    {
        if (allBuiltInTools == null)
        {
            return null;
        }

        // Null means "all tools enabled" for system/default modes.
        if (enabledTools == null)
        {
            return allBuiltInTools.ToList();
        }

        if (enabledTools.Count == 0)
        {
            return null;
        }

        var enabledToolSet = enabledTools.ToHashSet(StringComparer.Ordinal);
        var filtered = allBuiltInTools
            .OfType<AnthropicBuiltInTool>()
            .Where(t => enabledToolSet.Contains(t.Name))
            .Cast<object>()
            .ToList();

        return filtered.Count == 0 ? null : filtered;
    }
}
