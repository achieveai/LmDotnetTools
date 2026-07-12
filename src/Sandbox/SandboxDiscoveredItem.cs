namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>
/// An item the gateway's background discovery sweep found for a session's workspace (e.g. a
/// sub-agent, skill, or context file), as returned by <see cref="SandboxClient.ListDiscoveredAsync"/>.
/// </summary>
/// <remarks>
/// Only <see cref="Kind"/> and <see cref="Path"/> are required by the gateway's
/// <c>DiscoveredFile</c> wire contract (<c>crates/mcp-gateway/src/api/sandboxes.rs</c>) — every
/// other field, including <see cref="Name"/>, is <c>Option&lt;String&gt;</c> and is omitted
/// whenever the gateway has nothing to report for it. In particular a <c>"context_file"</c> item
/// (e.g. a discovered <c>CLAUDE.md</c>/<c>AGENTS.md</c>) never carries a name, and any other
/// present or future <c>Kind</c> discriminator the gateway emits may omit it too — this type does
/// not hard-code per-kind requirements, so an unrecognized <c>Kind</c> value is tolerated rather
/// than rejected.
/// </remarks>
public sealed class SandboxDiscoveredItem
{
    /// <summary>Discovery kind (e.g. <c>"subagent"</c>, <c>"skill"</c>, <c>"context_file"</c>). Callers filter on this.</summary>
    public string Kind { get; }

    /// <summary>
    /// Item name (e.g. a skill/subagent name). <c>null</c> for kinds the gateway never names —
    /// <c>"context_file"</c> always omits it — and for any other kind the gateway omits it for.
    /// </summary>
    public string? Name { get; }
    public string? Description { get; }

    /// <summary>Path INSIDE the sandbox the item was discovered at — never a local host path.</summary>
    public string Path { get; }

    public string? Content { get; }
    public string? QualifiedName { get; }

    public SandboxDiscoveredItem(
        string kind,
        string? name,
        string? description,
        string path,
        string? content = null,
        string? qualifiedName = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Kind = kind;
        Name = name;
        Description = description;
        Path = path;
        Content = content;
        QualifiedName = qualifiedName;
    }
}
