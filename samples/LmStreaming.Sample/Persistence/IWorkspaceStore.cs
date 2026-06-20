using LmStreaming.Sample.Models;

namespace LmStreaming.Sample.Persistence;

/// <summary>
/// Interface for persisting user-selectable workspaces.
/// </summary>
public interface IWorkspaceStore
{
    /// <summary>
    /// Gets all workspaces (the seeded default first, then user workspaces by name).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<Workspace>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets a specific workspace by ID, or null if not found.
    /// </summary>
    /// <param name="id">The workspace ID.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Workspace?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Creates a new user-defined workspace.
    /// </summary>
    /// <param name="dto">The workspace creation data.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created workspace with generated ID and timestamps.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the name is empty, the sanitized directory is empty/escaping, or the directory
    /// collides (case-insensitive) with an existing workspace.
    /// </exception>
    Task<Workspace> CreateAsync(WorkspaceCreate dto, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing user-defined workspace's marketplaces.
    /// </summary>
    /// <param name="id">The workspace ID to update.</param>
    /// <param name="dto">The updated marketplaces.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated workspace.</returns>
    /// <exception cref="KeyNotFoundException">Thrown if the workspace is not found.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the workspace is system-defined.</exception>
    Task<Workspace> UpdateAsync(string id, WorkspaceUpdate dto, CancellationToken ct = default);
}
