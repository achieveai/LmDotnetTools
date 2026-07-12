using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmSampleShared.Discovery;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// Builds sub-agent templates from the gateway's discovered items (design §4). Each item's full markdown
/// body is inline in <see cref="SandboxSessionRegistry.DiscoveredItem.Content"/> (the §3 fix), so no file
/// read is needed. Only <c>code-reviewer:*</c> sub-agents are kept; templates are keyed by qualified name
/// so two plugins' same-named agents never collide.
/// </summary>
internal sealed class DiscoveredSubAgentTemplateBuilder(ILogger<DiscoveredSubAgentTemplateBuilder> logger)
{
    private const int MaxTurnsPerRun = 25;

    public IReadOnlyDictionary<string, SubAgentTemplate> Build(
        IReadOnlyList<SandboxSessionRegistry.DiscoveredItem> items,
        string pluginFilter,
        Func<IStreamingAgent> agentFactory)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginFilter);
        ArgumentNullException.ThrowIfNull(agentFactory);

        var prefix = pluginFilter + ":";
        var result = new Dictionary<string, SubAgentTemplate>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            if (!string.Equals(item.Kind, "subagent", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(item.QualifiedName)
                || !item.QualifiedName.StartsWith(prefix, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(item.Content))
            {
                continue;
            }

            var parsed = SubAgentMarkdownParser.Parse(item.Content, item.QualifiedName);
            if (parsed is null)
            {
                logger.LogWarning("Skipping sub-agent {Name}: inline content had no valid frontmatter/body.", item.QualifiedName);
                continue;
            }

            foreach (var diagnostic in parsed.Diagnostics)
            {
                logger.LogWarning(
                    "Discovered sub-agent {Name} frontmatter diagnostic: {Diagnostic}",
                    parsed.Name,
                    diagnostic);
            }

            if (!result.TryAdd(item.QualifiedName, SubAgentTemplateMapper.Map(parsed, agentFactory, MaxTurnsPerRun)))
            {
                logger.LogWarning("Duplicate sub-agent {Name}; keeping the first.", item.QualifiedName);
            }
        }

        return result;
    }
}
