namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>Where a skill's tier for an agent came from (gateway ADR 0041).</summary>
public enum SandboxSkillTierOrigin
{
    /// <summary>The agent's own override, set by <see cref="SandboxClient.UpdateSkillTiersAsync"/>.</summary>
    Override,

    /// <summary>The create request's <see cref="SandboxSkillsPolicy.Included"/> list.</summary>
    IncludedList,

    /// <summary>The sandbox default tier.</summary>
    Default,
}

/// <summary>
/// One agent's skill catalog (gateway ADR 0041, <c>GET/PATCH …/skills</c>): every skill the sandbox admits,
/// grouped marketplace → plugin → skill, plus the discovered workspace skills, each with the agent's tier and
/// load state. This is what a harness searches to decide which skills an agent should see.
/// </summary>
/// <param name="SessionId">The sandbox session.</param>
/// <param name="AgentId">The agent the catalog is for; empty for the default agent.</param>
/// <param name="DefaultTier">The sandbox default tier.</param>
/// <param name="Counts">Tier and load counts over the WHOLE catalog, before any filter.</param>
/// <param name="Marketplaces">Marketplace skills; a filter drops plugins and marketplaces it leaves empty.</param>
/// <param name="WorkspaceSkills">Discovered workspace skills.</param>
/// <param name="ToolsListChanged">
/// <c>true</c> on an update result: the agent's <c>Skill</c> description may have changed, and the gateway sends no
/// <c>list_changed</c> notification, so an MCP client must re-run <c>tools/list</c>. <c>false</c> on a read.
/// </param>
public sealed record SandboxSkillCatalog(
    string SessionId,
    string AgentId,
    SandboxSkillTier DefaultTier,
    SandboxSkillCounts Counts,
    IReadOnlyList<SandboxSkillMarketplace> Marketplaces,
    IReadOnlyList<SandboxSkill> WorkspaceSkills,
    bool ToolsListChanged
);

/// <summary>Counts over a whole skill catalog.</summary>
public sealed record SandboxSkillCounts(int Included, int Available, int Unavailable, int Loaded);

/// <summary>A marketplace of a skill catalog.</summary>
public sealed record SandboxSkillMarketplace(string Alias, IReadOnlyList<SandboxSkillPlugin> Plugins);

/// <summary>A plugin of a skill catalog; <paramref name="Version"/> and <paramref name="Description"/> come from its <c>plugin.json</c>.</summary>
public sealed record SandboxSkillPlugin(
    string Name,
    string? Version,
    string Description,
    IReadOnlyList<SandboxSkill> Skills
);

/// <summary>One skill of a catalog, as the agent sees it.</summary>
/// <param name="Name">What the <c>Skill</c> tool accepts. Marketplace skills are always plugin-qualified.</param>
/// <param name="Path">The sandbox-visible skill directory; always unique, so also a valid ref.</param>
/// <param name="Description">The skill's description.</param>
/// <param name="Tier">The agent's tier for it.</param>
/// <param name="TierOrigin">Where that tier came from.</param>
/// <param name="Loaded">Whether the agent has loaded it. Loads persist across gateway restarts.</param>
/// <param name="LoadedAt">When it was loaded, or <c>null</c>.</param>
/// <param name="Notified">
/// Workspace skills only: whether the agent has been told about it (ADR 0040). An included workspace skill is
/// listed only once notified; it loads by name regardless. <c>null</c> for marketplace skills.
/// </param>
/// <param name="Shadowed">
/// Marketplace skills only: a workspace skill takes its base name, so it is listed and loaded only by its
/// qualified name or path.
/// </param>
public sealed record SandboxSkill(
    string Name,
    string Path,
    string Description,
    SandboxSkillTier Tier,
    SandboxSkillTierOrigin TierOrigin,
    bool Loaded,
    DateTimeOffset? LoadedAt,
    bool? Notified,
    bool Shadowed
);
