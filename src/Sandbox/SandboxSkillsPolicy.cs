namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>One agent's tier for one skill (gateway ADR 0041).</summary>
public enum SandboxSkillTier
{
    /// <summary>Listed in the agent's <c>Skill</c> tool description and loadable.</summary>
    Included,

    /// <summary>Loadable by exact name, but not listed.</summary>
    Available,

    /// <summary>Refused for this agent.</summary>
    Unavailable,
}

/// <summary>
/// A sandbox's skill-tier policy (gateway ADR 0041): sent as the create request's <c>skills</c> block, and
/// echoed resolved on the create response (<see cref="SandboxInfo.SkillsPolicy"/>). Per-agent changes after
/// create go through <see cref="SandboxClient.UpdateSkillTiersAsync"/>.
/// </summary>
public sealed class SandboxSkillsPolicy
{
    /// <summary>
    /// Tier of every admitted skill with no override and not in <see cref="Included"/>: only
    /// <see cref="SandboxSkillTier.Included"/> or <see cref="SandboxSkillTier.Available"/>. <c>null</c> on a
    /// request lets the gateway apply its <c>SKILL_DEFAULT_TIER</c> (else <c>included</c>).
    /// </summary>
    public SandboxSkillTier? DefaultTier { get; }

    /// <summary>
    /// Skills included for every agent unless that agent overrides them. On a request: a name the <c>Skill</c>
    /// tool accepts (validated against the marketplace skills, <c>400 skill_not_found</c>/<c>skill_ambiguous</c>)
    /// or a skill directory path (accepted as-is). On a response: the gateway's skill keys.
    /// </summary>
    public IReadOnlyList<string> Included { get; }

    /// <summary>
    /// Replaces the trailer appended to a <c>Skill</c> description that does not list every loadable skill;
    /// <c>""</c> removes it, <c>null</c> keeps the gateway's default trailer.
    /// </summary>
    public string? UnlistedHint { get; }

    /// <summary>
    /// Lists the gateway's <c>SearchSkills</c> tool for this sandbox (gateway 0.1.21, ADR 0047). Fixed at create.
    /// <c>null</c> on a request omits the field, which a gateway older than 0.1.21 requires: it rejects a <c>skills</c>
    /// block with fields it does not know (see <see cref="SandboxClient.SupportsSearchTools"/>). On a response,
    /// <c>false</c> after a create that sent either search flag, even though the gateway omits a false flag; <c>null</c>
    /// after any other create, where an absent flag cannot be told from an older gateway.
    /// </summary>
    public bool? SearchTool { get; }

    /// <summary>
    /// Lists the gateway's <c>SearchAgents</c> tool for this sandbox. Fixed at create; <c>null</c> and the response
    /// value as for <see cref="SearchTool"/>.
    /// </summary>
    public bool? AgentsSearchTool { get; }

    /// <summary>The constructor assemblies built before the search flags bind to; new code gets the one below.</summary>
    public SandboxSkillsPolicy(SandboxSkillTier? defaultTier, IReadOnlyList<string>? included, string? unlistedHint)
        : this(defaultTier, included, unlistedHint, searchTool: null, agentsSearchTool: null) { }

    public SandboxSkillsPolicy(
        SandboxSkillTier? defaultTier = null,
        IReadOnlyList<string>? included = null,
        string? unlistedHint = null,
        bool? searchTool = null,
        bool? agentsSearchTool = null
    )
    {
        if (defaultTier is SandboxSkillTier.Unavailable)
        {
            // The gateway answers 400 invalid_skills: a sandbox whose every skill is refused is a mistake.
            throw new ArgumentOutOfRangeException(
                nameof(defaultTier),
                "The default tier can only be Included or Available."
            );
        }

        if (included is not null)
        {
            foreach (var reference in included)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(reference, nameof(included));
            }
        }

        DefaultTier = defaultTier;
        Included = included is null ? [] : [.. included];
        UnlistedHint = unlistedHint;
        SearchTool = searchTool;
        AgentsSearchTool = agentsSearchTool;
    }
}
