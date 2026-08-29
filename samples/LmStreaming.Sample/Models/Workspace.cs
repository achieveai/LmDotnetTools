using System.Text.Json.Serialization;
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
    /// Explicit per-plugin selection for this workspace. Tri-state: <see langword="null"/> means the
    /// workspace expressed no preference (legacy "all plugins from the enabled marketplaces"), an
    /// empty list means explicitly no plugins, and a non-empty list is an explicit subset.
    /// <see langword="null"/> is never collapsed to an empty list.
    /// </summary>
    public IReadOnlyList<PluginRef>? PluginSelection { get; init; }

    /// <summary>
    /// Monotonic revision of <see cref="PluginSelection"/>, incremented on every explicit selection
    /// change. Callers echo it back as the compare-and-swap token on updates.
    /// </summary>
    public int PluginsRevision { get; init; }

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

    /// <summary>
    /// Optional explicit plugin selection to seed the workspace with. Tri-state, exactly as on
    /// <see cref="Workspace.PluginSelection"/>: <see langword="null"/> means "no preference"
    /// (legacy all-plugins) and is never collapsed to an empty list.
    /// </summary>
    public IReadOnlyList<PluginRef>? PluginSelection { get; init; }
}

/// <summary>
/// DTO for editing an existing workspace. Marketplaces and the explicit plugin selection can be
/// changed; the plugin selection uses <see cref="Optional{T}"/> so an omitted property ("leave
/// unchanged") stays distinguishable from an explicit <c>null</c> ("clear to legacy all-plugins").
/// </summary>
public record WorkspaceUpdate
{
    /// <summary>
    /// Replacement set of plugin marketplaces for the workspace.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT nullable, unlike <see cref="WorkspaceCreate.Marketplaces"/>. The annotation is
    /// load-bearing rather than decorative: MVC's implicit-required convention for non-nullable
    /// reference-type members is what turns an explicit <c>"marketplaces": null</c> into a 400 before
    /// the action runs. Widening it to <c>IReadOnlyList&lt;string&gt;?</c> "for consistency with
    /// create" would silently make that null bindable, and this is a REPLACEMENT set — a client that
    /// emitted a stray null would then wipe a live workspace's marketplaces and be told nothing.
    /// Create can afford the leniency because a workspace being created has nothing to wipe.
    /// </remarks>
    public IReadOnlyList<string> Marketplaces { get; init; } = [];

    /// <summary>
    /// Replacement plugin selection. Four-state: omitted (leave unchanged), explicit <c>null</c>
    /// (clear to legacy all-plugins), empty list (explicitly no plugins), or a non-empty subset.
    /// </summary>
    [JsonConverter(typeof(OptionalJsonConverterFactory))]
    public Optional<IReadOnlyList<PluginRef>?> PluginSelection { get; init; } =
        Optional<IReadOnlyList<PluginRef>?>.Unset;

    /// <summary>
    /// Compare-and-swap token echoed from <see cref="Workspace.PluginsRevision"/>. Mandatory
    /// whenever <see cref="PluginSelection"/> is set; ignored otherwise.
    /// </summary>
    public int? PluginsRevision { get; init; }
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
    IReadOnlyList<string> UnsupportedMarketplaces,
    IReadOnlyList<PluginRef>? PluginSelection,
    int PluginsRevision
);

public sealed record WorkspaceGatewayView(string CanonicalBaseUrl, string AppId, bool Available, string? Error);

public sealed record WorkspaceListResponse(WorkspaceGatewayView Gateway, IReadOnlyList<WorkspaceView> Workspaces);

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
            compatibility.UnsupportedMarketplaces,
            workspace.PluginSelection,
            workspace.PluginsRevision
        );
    }
}
