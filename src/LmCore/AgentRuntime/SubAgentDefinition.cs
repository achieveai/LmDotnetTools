namespace AchieveAi.LmDotnetTools.LmCore.AgentRuntime;

/// <summary>
///     Profile-supplied sub-agent that materializes into <c>.claude/agents/&lt;Name&gt;.md</c>
///     for the Claude Agent SDK. Description, model, and tool list live in the
///     <c>.md</c> frontmatter and are parsed by the consuming CLI.
/// </summary>
public sealed record SubAgentDefinition
{
    /// <summary>
    ///     File stem (without <c>.md</c>) under <c>.claude/agents/</c>. Must be unique
    ///     within the profile.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    ///     Where to read the sub-agent definition from. Path forms copy a single
    ///     <c>.md</c> file (or the first <c>.md</c> in a directory). Inline forms
    ///     write a literal markdown body.
    /// </summary>
    public required ContentSource Source { get; init; }

    /// <summary>
    ///     Sugar for inline-markdown sub-agents.
    /// </summary>
    public static SubAgentDefinition Inline(string name, string markdown)
        => new() { Name = name, Source = new ContentSource.FromInline(markdown) };

    /// <summary>
    ///     Sugar for path-sourced sub-agents.
    /// </summary>
    public static SubAgentDefinition FromPath(string name, string path)
        => new() { Name = name, Source = new ContentSource.FromPath(path) };
}
