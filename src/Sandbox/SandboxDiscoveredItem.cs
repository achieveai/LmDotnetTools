using System.Text.Json;

namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>
/// An item the gateway's background discovery sweep found for a session's workspace (e.g. a
/// sub-agent, skill, or context file), as returned by <c>SandboxClient.ListDiscoveredAsync</c>.
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

    /// <summary>
    /// A sub-agent's YAML frontmatter as JSON (gateway issue #124); <c>null</c> for other kinds.
    /// Agent-authored, untrusted content: never render it as HTML/markdown.
    /// </summary>
    public JsonElement? Frontmatter { get; init; }

    /// <summary>The owning plugin of a marketplace sub-agent; <c>null</c> otherwise.</summary>
    public string? Plugin { get; init; }

    /// <summary>When the gateway discovered the item; <c>null</c> only if the gateway omitted it.</summary>
    public DateTimeOffset? DiscoveredAt { get; init; }

    /// <summary>
    /// Whether the item's notification was delivered — to the default agent for an unfiltered or
    /// <c>agent_id=""</c> listing; <c>null</c> only if the gateway omitted it.
    /// </summary>
    public bool? Notified { get; init; }

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
