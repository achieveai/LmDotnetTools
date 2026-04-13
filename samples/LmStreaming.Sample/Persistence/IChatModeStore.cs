using LmStreaming.Sample.Models;

namespace LmStreaming.Sample.Persistence;

/// <summary>
/// Interface for persisting chat modes.
/// </summary>
public interface IChatModeStore
{
    /// <summary>
    /// Gets all chat modes (system-defined and user-created).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>All chat modes, ordered by system modes first, then user modes by name.</returns>
    Task<IReadOnlyList<ChatMode>> GetAllModesAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets a specific chat mode by ID.
    /// </summary>
    /// <param name="modeId">The mode ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The chat mode, or null if not found.</returns>
    Task<ChatMode?> GetModeAsync(string modeId, CancellationToken ct = default);

    /// <summary>
    /// Creates a new user-defined chat mode.
    /// </summary>
    /// <param name="mode">The mode creation data.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created chat mode with generated ID and timestamps.</returns>
    Task<ChatMode> CreateModeAsync(ChatModeCreateUpdate mode, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing user-defined chat mode.
    /// Throws InvalidOperationException if the mode is system-defined.
    /// </summary>
    /// <param name="modeId">The mode ID to update.</param>
    /// <param name="mode">The updated mode data.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated chat mode.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the mode is system-defined.</exception>
    /// <exception cref="KeyNotFoundException">Thrown if the mode is not found.</exception>
    Task<ChatMode> UpdateModeAsync(string modeId, ChatModeCreateUpdate mode, CancellationToken ct = default);

    /// <summary>
    /// Deletes a user-defined chat mode.
    /// Throws InvalidOperationException if the mode is system-defined.
    /// </summary>
    /// <param name="modeId">The mode ID to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown if the mode is system-defined.</exception>
    Task DeleteModeAsync(string modeId, CancellationToken ct = default);

    /// <summary>
    /// Creates a copy of an existing mode as a new user-defined mode.
    /// Can copy both system-defined and user-defined modes.
    /// </summary>
    /// <param name="modeId">The mode ID to copy.</param>
    /// <param name="newName">The name for the copied mode.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The new chat mode copy.</returns>
    /// <exception cref="KeyNotFoundException">Thrown if the source mode is not found.</exception>
    Task<ChatMode> CopyModeAsync(string modeId, string newName, CancellationToken ct = default);
}
