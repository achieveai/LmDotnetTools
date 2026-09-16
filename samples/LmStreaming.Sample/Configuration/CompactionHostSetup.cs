using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;

namespace LmStreaming.Sample.Configuration;

/// <summary>
/// Host switch for just-in-time compaction (#721). The <c>Compaction</c> section binds straight onto the
/// library's <see cref="CompactionOptions"/> (Mode, ModeByRoute, ratios, reserve, tail, recall limits), so
/// every knob the policy has is settable from appsettings or <c>Compaction__*</c> environment variables.
/// </summary>
/// <remarks>
/// The library default is <see cref="CompactionMode.Off"/> and the shipped appsettings sets
/// <c>"Compaction": { "Mode": "Off" }</c>, so a host that does not turn a route on builds no setup: every loop
/// is constructed exactly as before, <c>manualCompaction</c> is not advertised and a manual request answers
/// 409 <c>compaction_off</c>.
/// </remarks>
public static class CompactionHostSetup
{
    public const string SectionName = "Compaction";

    /// <summary>Binds the section; an absent section yields the library defaults (Mode Off).</summary>
    public static CompactionOptions BindOptions(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.GetSection(SectionName).Get<CompactionOptions>() ?? new CompactionOptions();
    }

    /// <summary>
    ///     The setup handed to a root loop, or null when no route can reach a mode above Off — null is the
    ///     loop's "feature absent" contract (no recall tool, request built as before). Child loops inherit
    ///     the root's setup through <c>SubAgentOptions.Compaction</c>.
    /// </summary>
    /// <param name="options">Bound options, shared by every loop in the process.</param>
    /// <param name="capacityResolver">
    ///     The #681 resolver over <c>Pricing:Models</c>, so the policy and the context panel read one window.
    ///     Null leaves the window unknown and the policy answers <c>capacity_unknown</c>.
    /// </param>
    /// <param name="providerId">The conversation's provider, the first half of a ModeByRoute key.</param>
    public static CompactionSetup? Create(
        CompactionOptions options,
        IModelCapacityResolver? capacityResolver,
        string providerId
    )
    {
        ArgumentNullException.ThrowIfNull(options);

        var anyRouteOn =
            options.Mode > CompactionMode.Off
            || (options.ModeByRoute?.Values.Any(mode => mode > CompactionMode.Off) ?? false);
        if (!anyRouteOn)
        {
            return null;
        }

        string? summaryPrompt = null;
        if (!string.IsNullOrWhiteSpace(options.SummaryPromptPath))
        {
            // The eval's prompt files carry YAML front matter (id, parent, hypothesis); the model sees only the body.
            summaryPrompt = StripFrontMatter(File.ReadAllText(options.SummaryPromptPath));
        }

        return new CompactionSetup
        {
            Options = options,
            ProviderId = providerId,
            SummarySystemPrompt = summaryPrompt,
            ResolveWindowTokens = capacityResolver is null
                ? null
                : modelId => string.IsNullOrEmpty(modelId) ? null : capacityResolver.Resolve(modelId)?.WindowTokens,
        };
    }

    /// <summary>The text after a leading <c>---</c>…<c>---</c> block, trimmed; the whole text when there is none.</summary>
    internal static string StripFrontMatter(string text)
    {
        if (!text.StartsWith("---", StringComparison.Ordinal))
        {
            return text.Trim();
        }

        var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
        return end < 0 ? text.Trim() : text[(end + 4)..].Trim();
    }
}
