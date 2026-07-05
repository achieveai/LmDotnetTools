using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

namespace AchieveAi.LmDotnetTools.LmSampleShared.Discovery;

/// <summary>
/// Maps a parsed sub-agent markdown document into a <see cref="SubAgentTemplate"/>. Shared by the
/// LmStreaming workspace loader and the Code-Review Daemon's discovered-template builder so the
/// mapping table (description → description + when-to-use, model → default options, tools → allow-list)
/// stays identical across both samples.
/// </summary>
public static class SubAgentTemplateMapper
{
    /// <summary>
    /// Mapping rules:
    /// <list type="bullet">
    ///   <item><c>description</c> → both <see cref="SubAgentTemplate.Description"/> AND
    ///     <see cref="SubAgentTemplate.WhenToUse"/>, so the Agent-tool catalog isn't blank
    ///     for discovered templates (which don't carry a separate <c>when_to_use</c> field).</item>
    ///   <item><c>model</c> → <see cref="SubAgentTemplate.DefaultOptions"/> with only
    ///     <see cref="GenerateReplyOptions.ModelId"/> set; absent leaves
    ///     <see cref="SubAgentTemplate.DefaultOptions"/> null so the sub-agent inherits the
    ///     parent's runtime defaults (matching the built-in templates' shape).</item>
    ///   <item><c>tools</c> → <see cref="SubAgentTemplate.EnabledTools"/>. Absent (null) means
    ///     inherit every parent tool; an empty list means deny all tools (distinct case).</item>
    ///   <item><paramref name="maxTurnsPerRun"/> to match the caller's production templates.</item>
    /// </list>
    /// </summary>
    public static SubAgentTemplate Map(ParsedSubAgent parsed, Func<IStreamingAgent> agentFactory, int maxTurnsPerRun)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(agentFactory);

        // "inherit" is the Claude-Code convention for "use the parent agent's model" — it is NOT a real
        // model id. Passing it through as an explicit ModelId makes the provider reject the sub-agent turn
        // (the GitHub Copilot backend returns model_not_supported for "inherit" and other non-native
        // aliases). Treat "inherit" (and a blank model) as "no explicit model" so the sub-agent inherits
        // the parent loop's concrete model instead.
        var hasExplicitModel = !string.IsNullOrWhiteSpace(parsed.Model)
            && !string.Equals(parsed.Model.Trim(), "inherit", StringComparison.OrdinalIgnoreCase);
        var defaults = hasExplicitModel
            ? new GenerateReplyOptions { ModelId = parsed.Model!.Trim() }
            : null;

        return new SubAgentTemplate
        {
            Name = parsed.Name,
            Description = parsed.Description,
            WhenToUse = parsed.Description,
            SystemPrompt = parsed.SystemPrompt,
            AgentFactory = agentFactory,
            DefaultOptions = defaults,
            EnabledTools = parsed.Tools,
            MaxTurnsPerRun = maxTurnsPerRun,
        };
    }
}
