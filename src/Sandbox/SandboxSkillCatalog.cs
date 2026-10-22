namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>Why a skill has the tier it has for an agent (gateway ADR 0041 <c>tier_origin</c>).</summary>
public enum SandboxSkillTierOrigin
{
    /// <summary>The agent's own override, set by <see cref="SandboxClient.PatchSkillTiersAsync"/> (wire <c>override</c>).</summary>
    Override,

    /// <summary>The sandbox's create-time <see cref="SandboxSkillsPolicy.Included"/> list (wire <c>included_list</c>).</summary>
    IncludedList,

    /// <summary>The sandbox's <see cref="SandboxSkillsPolicy.DefaultTier"/> (wire <c>default</c>).</summary>
    Default,
}

/// <summary>
/// One agent's skill catalog (gateway ADR 0041), as returned by <see cref="SandboxClient.GetSkillCatalogAsync"/>
/// and <see cref="SandboxClient.PatchSkillTiersAsync"/>: every skill the sandbox admits, grouped marketplace →
/// plugin → skill, plus the discovered workspace skills, each with this agent's tier and load state. This is
/// what a harness searches to decide which skills an agent should see; the gateway has no search of its own.
/// </summary>
/// <param name="SessionId">The sandbox session.</param>
/// <param name="AgentId">The agent the catalog is for; <c>""</c> for the default agent.</param>
/// <param name="DefaultTier">The sandbox's resolved default tier.</param>
/// <param name="Counts">Tier and load counts over the WHOLE catalog, before any tier/loaded filter.</param>
/// <param name="Marketplaces">Marketplaces in the sandbox's order; a filter drops ones it leaves empty.</param>
/// <param name="WorkspaceSkills">Skills discovered in the workspace.</param>
/// <param name="ToolsListChanged">
/// <c>true</c> on a <see cref="SandboxClient.PatchSkillTiersAsync"/> result: the agent's <c>Skill</c>
/// description may have changed, and the gateway sends no MCP <c>list_changed</c> notification, so an MCP client
/// must re-run <c>tools/list</c>. Always <c>false</c> on a <see cref="SandboxClient.GetSkillCatalogAsync"/> result.
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

/// <summary>Tier and load counts over an agent's whole skill catalog.</summary>
public sealed record SandboxSkillCounts(int Included, int Available, int Unavailable, int Loaded);

/// <summary>One marketplace of a <see cref="SandboxSkillCatalog"/>.</summary>
public sealed record SandboxSkillMarketplace(string Alias, IReadOnlyList<SandboxSkillPlugin> Plugins);

/// <summary>
/// One plugin of a <see cref="SandboxSkillMarketplace"/>. <see cref="Version"/> and <see cref="Description"/>
/// come from the plugin's <c>plugin.json</c>; <see cref="Version"/> is <c>null</c> when it declares none.
/// </summary>
public sealed record SandboxSkillPlugin(
    string Name,
    string? Version,
    string Description,
    IReadOnlyList<SandboxSkill> Skills
);

/// <summary>One skill row of a <see cref="SandboxSkillCatalog"/>.</summary>
/// <param name="Name">
/// What the <c>Skill</c> tool accepts. Marketplace skills are always plugin-qualified (<c>plugin:skill</c>).
/// </param>
/// <param name="Path">The sandbox-visible skill directory; always unique, so always a safe PATCH ref.</param>
/// <param name="Description">The skill's description.</param>
/// <param name="Tier">This agent's tier for the skill.</param>
/// <param name="TierOrigin">Where that tier came from.</param>
/// <param name="Loaded">Whether this agent has loaded the skill through the <c>Skill</c> tool.</param>
/// <param name="LoadedAt">When it was loaded; <c>null</c> when it is not loaded.</param>
/// <param name="Notified">
/// Workspace skills only: whether this agent has been told about the skill (ADR 0040). An included workspace
/// skill is listed only once notified. <c>null</c> for marketplace skills.
/// </param>
/// <param name="Shadowed">
/// Marketplace skills only: a workspace skill takes this skill's base name, so it is listed and loaded only by
/// its qualified name or path.
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
