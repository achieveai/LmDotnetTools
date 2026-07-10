using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmSampleShared.Discovery;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// Builds sub-agent templates from the gateway's discovered items (design §4). Each item's full markdown
/// body is inline in <see cref="SandboxSessionRegistry.DiscoveredItem.Content"/> (the §3 fix), so no file
/// read is needed. A sub-agent is kept when its source marketplace — the alias parsed from
/// <see cref="SandboxSessionRegistry.DiscoveredItem.Path"/>, e.g. <c>/marketplaces/gb-plugins/…</c> — is in
/// the configured allow-list, so EVERY plugin in an allowed marketplace is exposed, not just one. An empty
/// allow-list keeps every discovered sub-agent. Templates are keyed by qualified name so two plugins'
/// same-named agents never collide.
/// </summary>
internal sealed class DiscoveredSubAgentTemplateBuilder(ILogger<DiscoveredSubAgentTemplateBuilder> logger)
{
    private const int MaxTurnsPerRun = 25;

    /// <summary>
    /// Maps the discovered <c>subagent</c> items to templates, keeping only those whose source marketplace is
    /// in <paramref name="marketplaceFilter"/> (empty ⇒ keep all, regardless of marketplace). Marketplace
    /// aliases are matched case-insensitively against the alias parsed from each item's <c>Path</c>.
    /// </summary>
    public IReadOnlyDictionary<string, SubAgentTemplate> Build(
        IReadOnlyList<SandboxSessionRegistry.DiscoveredItem> items,
        IReadOnlyList<string> marketplaceFilter,
        Func<IStreamingAgent> agentFactory)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(marketplaceFilter);
        ArgumentNullException.ThrowIfNull(agentFactory);

        var result = new Dictionary<string, SubAgentTemplate>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            if (!string.Equals(item.Kind, "subagent", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(item.QualifiedName)
                || string.IsNullOrWhiteSpace(item.Content)
                || !IsMarketplaceAllowed(item.Path, marketplaceFilter))
            {
                continue;
            }

            var parsed = SubAgentMarkdownParser.Parse(item.Content, item.QualifiedName);
            if (parsed is null)
            {
                logger.LogWarning("Skipping sub-agent {Name}: inline content had no valid frontmatter/body.", item.QualifiedName);
                continue;
            }

            if (!result.TryAdd(item.QualifiedName, SubAgentTemplateMapper.Map(parsed, agentFactory, MaxTurnsPerRun)))
            {
                logger.LogWarning("Duplicate sub-agent {Name}; keeping the first.", item.QualifiedName);
            }
        }

        return result;
    }

    /// <summary>
    /// True when <paramref name="path"/>'s source marketplace is in <paramref name="filter"/>, or the filter
    /// is empty (all marketplaces allowed). The alias is the segment right after <c>marketplaces</c> in a
    /// discovered path such as <c>/marketplaces/gb-plugins/code-reviewer/agents/architecture-review.md</c>.
    /// </summary>
    private static bool IsMarketplaceAllowed(string? path, IReadOnlyList<string> filter)
    {
        if (filter.Count == 0)
        {
            return true;
        }

        var marketplace = MarketplaceFromPath(path);
        if (marketplace is null)
        {
            return false;
        }

        foreach (var allowed in filter)
        {
            if (string.Equals(allowed, marketplace, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? MarketplaceFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (string.Equals(segments[i], "marketplaces", StringComparison.OrdinalIgnoreCase))
            {
                return segments[i + 1];
            }
        }

        return null;
    }
}
