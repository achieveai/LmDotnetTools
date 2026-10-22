namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>
/// One agent's tier for one skill (gateway ADR 0041). Whether the agent has LOADED a skill is a separate
/// flag (<see cref="SandboxSkill.Loaded"/>), never a tier: loading an available skill leaves it available.
/// </summary>
public enum SandboxSkillTier
{
    /// <summary>Listed in the agent's <c>Skill</c> tool description (wire <c>included</c>).</summary>
    Included,

    /// <summary>Not listed, but loads by exact name and appears in the catalog (wire <c>available</c>).</summary>
    Available,

    /// <summary>Refused for this agent (wire <c>unavailable</c>).</summary>
    Unavailable,
}

/// <summary>
/// A sandbox's skill-tier policy (gateway ADR 0041): sent as the create request's <c>skills</c> block via
/// <see cref="SandboxCreateRequest.Skills"/>, and echoed back, resolved, on <see cref="SandboxInfo.Skills"/>.
/// It sets the starting tier of every skill for every agent; per-agent changes after create go through
/// <see cref="SandboxClient.PatchSkillTiersAsync"/>.
/// </summary>
public sealed class SandboxSkillsPolicy
{
    /// <summary>
    /// Tier of every admitted skill that is not in <see cref="Included"/> and has no agent override.
    /// <c>null</c> omits the field so the gateway applies its <c>SKILL_DEFAULT_TIER</c> (else
    /// <see cref="SandboxSkillTier.Included"/>). Never <see cref="SandboxSkillTier.Unavailable"/>: a sandbox
    /// that hides every skill is rejected by the gateway, so it is rejected here first. Always set on an
    /// echoed policy.
    /// </summary>
    /// <remarks>
    /// <see cref="SandboxSkillTier.Available"/> is the setting that keeps a discovered skill from changing an
    /// agent's <c>Skill</c> description: it loads by name immediately but is not listed until a
    /// <c>PATCH</c> includes it.
    /// </remarks>
    public SandboxSkillTier? DefaultTier { get; }

    /// <summary>
    /// Skills included for every agent unless that agent overrides them, defensively copied. On a request,
    /// each entry is a name the <c>Skill</c> tool accepts (marketplace skills are plugin-qualified, e.g.
    /// <c>document-skills:pdf</c>; it must resolve among the sandbox's admitted marketplace skills) or a skill
    /// directory path (accepted as-is, so a workspace skill can be included before it is discovered). On an
    /// echoed policy the gateway reports its resolved skill keys (the skill directory paths) instead.
    /// Empty omits the field.
    /// </summary>
    public IReadOnlyList<string> Included { get; }

    /// <summary>
    /// Replacement for the fixed trailer the gateway appends to a <c>Skill</c> description that does not
    /// list every loadable skill. <c>null</c> keeps the gateway's default trailer; <c>""</c> suppresses the
    /// trailer entirely.
    /// </summary>
    public string? UnlistedHint { get; }

    /// <exception cref="ArgumentException">
    /// <paramref name="defaultTier"/> is <see cref="SandboxSkillTier.Unavailable"/>, or an
    /// <paramref name="included"/> entry is null or blank.
    /// </exception>
    public SandboxSkillsPolicy(
        SandboxSkillTier? defaultTier = null,
        IReadOnlyList<string>? included = null,
        string? unlistedHint = null
    )
    {
        if (defaultTier == SandboxSkillTier.Unavailable)
        {
            throw new ArgumentException(
                "A sandbox default skill tier must be Included or Available; the gateway rejects Unavailable.",
                nameof(defaultTier)
            );
        }

        if (included is not null && included.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Included skill refs must be non-blank names or paths.", nameof(included));
        }

        DefaultTier = defaultTier;
        Included = included is null ? [] : [.. included];
        UnlistedHint = unlistedHint;
    }
}
