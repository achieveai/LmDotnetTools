namespace AchieveAi.LmDotnetTools.LmCore.AgentRuntime;

/// <summary>
///     Provider-neutral sub-agent descriptor supplied via an
///     <see cref="AgentRuntimeProfile"/>. Each consuming provider decides how (and
///     whether) to surface sub-agents to the spawned agent. Description, model, and
///     tool list typically live in the markdown frontmatter and are parsed by the
///     consuming provider.
/// </summary>
/// <remarks>
///     See the provider-specific materializer (e.g. <c>ProfileMaterializer</c> in
///     <c>ClaudeAgentSdkProvider</c>) for details on how a sub-agent's <see cref="Name"/>
///     and <see cref="Source"/> are mapped onto the underlying CLI's filesystem layout.
/// </remarks>
public sealed record SubAgentDefinition
{
    /// <summary>
    ///     Stable identifier for the sub-agent. Must be unique within the profile and
    ///     contain no path separators. Providers map this onto the per-provider
    ///     filesystem layout (for example, a markdown file stem).
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    ///     Where to read the sub-agent definition from. Path forms copy a single
    ///     markdown file (or the first markdown file in a directory). Inline forms
    ///     supply a literal markdown body. The provider materializer is responsible
    ///     for any I/O.
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
