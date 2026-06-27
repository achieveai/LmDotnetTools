namespace LmStreaming.Sample.Models;

/// <summary>
/// Represents a chat mode that defines a persona, system prompt, and available tools.
/// </summary>
public record ChatMode
{
    /// <summary>
    /// Unique identifier for the mode.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Display name of the mode.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Optional description of what this mode does.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// The system prompt used when this mode is active.
    /// </summary>
    public required string SystemPrompt { get; init; }

    /// <summary>
    /// List of enabled (function/MCP) tool names. If null, all tools are enabled.
    /// </summary>
    public IReadOnlyList<string>? EnabledTools { get; init; }

    /// <summary>
    /// List of enabled server-side built-in tool names (e.g. <c>web_search</c>). Kept separate from
    /// <see cref="EnabledTools"/> so a mode can curate its function tools elsewhere (e.g. Workspace
    /// Agent sets <c>EnabledTools = []</c> and resolves function tools via the sandbox MCP gateway)
    /// while still declaring which server-side built-ins it wants. When null, built-in selection
    /// falls back to <see cref="EnabledTools"/> for backward compatibility.
    /// </summary>
    public IReadOnlyList<string>? EnabledBuiltInTools { get; init; }

    /// <summary>
    /// Whether this mode is system-defined (read-only) or user-created.
    /// </summary>
    public bool IsSystemDefined { get; init; }

    /// <summary>
    /// Unix timestamp (milliseconds) when the mode was created.
    /// </summary>
    public long CreatedAt { get; init; }

    /// <summary>
    /// Unix timestamp (milliseconds) when the mode was last updated.
    /// </summary>
    public long UpdatedAt { get; init; }
}

/// <summary>
/// DTO for creating or updating a chat mode.
/// </summary>
public record ChatModeCreateUpdate
{
    /// <summary>
    /// Display name of the mode.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Optional description of what this mode does.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// The system prompt used when this mode is active.
    /// </summary>
    public required string SystemPrompt { get; init; }

    /// <summary>
    /// List of enabled tool names. If null, all tools are enabled.
    /// </summary>
    public IReadOnlyList<string>? EnabledTools { get; init; }
}

/// <summary>
/// DTO for copying a mode with a new name.
/// </summary>
public record ChatModeCopy
{
    /// <summary>
    /// The new name for the copied mode.
    /// </summary>
    public required string NewName { get; init; }
}

/// <summary>
/// Represents a tool definition for the frontend.
/// </summary>
public record ToolDefinition
{
    /// <summary>
    /// The function name of the tool.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Description of what the tool does.
    /// </summary>
    public string? Description { get; init; }
}
