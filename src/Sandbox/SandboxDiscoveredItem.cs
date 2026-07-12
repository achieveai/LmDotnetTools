namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>
/// An item the gateway's background discovery sweep found for a session's workspace (e.g. a
/// sub-agent or skill context file), as returned by <see cref="SandboxClient.ListDiscoveredAsync"/>.
/// </summary>
public sealed class SandboxDiscoveredItem
{
    /// <summary>Discovery kind (e.g. <c>"subagent"</c>, <c>"skill"</c>). Callers filter on this.</summary>
    public string Kind { get; }

    public string Name { get; }
    public string? Description { get; }

    /// <summary>Path INSIDE the sandbox the item was discovered at — never a local host path.</summary>
    public string Path { get; }

    public string? Content { get; }
    public string? QualifiedName { get; }

    public SandboxDiscoveredItem(
        string kind,
        string name,
        string? description,
        string path,
        string? content = null,
        string? qualifiedName = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Kind = kind;
        Name = name;
        Description = description;
        Path = path;
        Content = content;
        QualifiedName = qualifiedName;
    }
}
