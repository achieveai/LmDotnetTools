namespace AchieveAi.LmDotnetTools.LmCore.AgentRuntime;

/// <summary>
///     Profile-supplied skill that materializes into <c>.claude/skills/&lt;Name&gt;/SKILL.md</c>
///     for the Claude Agent SDK. Description and other metadata live in the
///     SKILL.md frontmatter and are parsed by the consuming CLI, not by this SDK.
/// </summary>
public sealed record AgentSkill
{
    /// <summary>
    ///     Directory name under <c>.claude/skills/</c>. Must be unique within the profile.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    ///     Where to read the skill content from. Path forms copy a directory tree or
    ///     a single <c>SKILL.md</c> file; inline forms write a literal markdown body.
    /// </summary>
    public required ContentSource Source { get; init; }

    /// <summary>
    ///     Sugar for inline-markdown skills.
    /// </summary>
    public static AgentSkill Inline(string name, string markdown)
        => new() { Name = name, Source = new ContentSource.FromInline(markdown) };

    /// <summary>
    ///     Sugar for path-sourced skills.
    /// </summary>
    public static AgentSkill FromPath(string name, string path)
        => new() { Name = name, Source = new ContentSource.FromPath(path) };
}
