using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Models;

/// <summary>
/// Represents a user-selectable workspace that mounts its own sandbox directory and
/// optionally enables a set of plugin marketplaces.
/// </summary>
public record Workspace
{
    /// <summary>
    /// Unique identifier for the workspace.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Display name of the workspace.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Workspace directory leaf, relative to the sandbox workspace base. Sanitized on creation.
    /// </summary>
    public required string DirectoryRelPath { get; init; }

    /// <summary>
    /// Plugin marketplaces enabled for this workspace.
    /// </summary>
    public IReadOnlyList<string> Marketplaces { get; init; } = [];

    /// <summary>
    /// Whether this workspace is system-defined (read-only directory/name) or user-created.
    /// </summary>
    public bool IsSystemDefined { get; init; }

    /// <summary>
    /// Unix timestamp (milliseconds) when the workspace was created.
    /// </summary>
    public long CreatedAt { get; init; }

    /// <summary>
    /// Unix timestamp (milliseconds) when the workspace was last updated.
    /// </summary>
    public long UpdatedAt { get; init; }
}

/// <summary>
/// DTO for creating a new workspace.
/// </summary>
public record WorkspaceCreate
{
    /// <summary>
    /// Display name of the workspace.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Optional directory leaf. When null/blank, the sanitized name is used.
    /// </summary>
    public string? DirectoryRelPath { get; init; }

    /// <summary>
    /// Optional plugin marketplaces to enable. Null is treated as an empty list.
    /// </summary>
    public IReadOnlyList<string>? Marketplaces { get; init; }
}

/// <summary>
/// DTO for editing an existing workspace. Only the marketplaces can be changed.
/// </summary>
public record WorkspaceUpdate
{
    /// <summary>
    /// Replacement set of plugin marketplaces for the workspace.
    /// </summary>
    public IReadOnlyList<string> Marketplaces { get; init; } = [];
}

public sealed record WorkspaceView(
    string Id,
    string Name,
    string DirectoryRelPath,
    IReadOnlyList<string> Marketplaces,
    bool IsSystemDefined,
    long CreatedAt,
    long UpdatedAt,
    string Compatibility,
    IReadOnlyList<string> UnsupportedMarketplaces
);

public sealed record WorkspaceGatewayView(
    string CanonicalBaseUrl,
    string AppId,
    bool Available,
    string? Error
);

public sealed record WorkspaceListResponse(
    WorkspaceGatewayView Gateway,
    IReadOnlyList<WorkspaceView> Workspaces
);

public static class WorkspaceViewMapping
{
    public static WorkspaceView ToView(this Workspace workspace, WorkspaceCompatibilityResult compatibility)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(compatibility);
        return new(
            workspace.Id,
            workspace.Name,
            workspace.DirectoryRelPath,
            workspace.Marketplaces,
            workspace.IsSystemDefined,
            workspace.CreatedAt,
            workspace.UpdatedAt,
            compatibility.Compatibility.ToString().ToLowerInvariant(),
            compatibility.UnsupportedMarketplaces
        );
    }
}
