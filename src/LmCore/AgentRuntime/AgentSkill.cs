namespace AchieveAi.LmDotnetTools.LmCore.AgentRuntime;

/// <summary>
///     Provider-neutral skill descriptor supplied via an <see cref="AgentRuntimeProfile"/>.
///     Each consuming provider decides how (and whether) to surface skills to the
///     spawned agent. Description and other metadata typically live in the markdown
///     frontmatter and are parsed by the consuming provider, not by this SDK.
/// </summary>
/// <remarks>
///     See the provider-specific materializer (e.g. <c>ProfileMaterializer</c> in
///     <c>ClaudeAgentSdkProvider</c>) for details on how a skill's <see cref="Name"/>
///     and <see cref="Source"/> are mapped onto the underlying CLI's filesystem layout.
/// </remarks>
public sealed record AgentSkill
{
    /// <summary>
    ///     Stable identifier for the skill. Must be unique within the profile and
    ///     contain no path separators. Providers map this onto the per-provider
    ///     filesystem layout (for example, a directory name).
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    ///     Where to read the skill content from. Path forms copy a directory tree or
    ///     a single markdown file; inline forms supply a literal markdown body. The
    ///     provider materializer is responsible for any I/O.
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
