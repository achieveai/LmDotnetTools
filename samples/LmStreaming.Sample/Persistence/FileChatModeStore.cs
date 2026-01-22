using System.Text.Json;
using LmStreaming.Sample.Models;

namespace LmStreaming.Sample.Persistence;

/// <summary>
/// File-based implementation of IChatModeStore.
/// Stores user-defined modes in a JSON file. System modes are provided in-memory.
/// </summary>
public sealed class FileChatModeStore : IChatModeStore
{
    private const string ModesFileName = "chat-modes.json";

    private readonly string _modesFilePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Creates a new FileChatModeStore.
    /// </summary>
    /// <param name="baseDirectory">Base directory for storing mode data.</param>
    public FileChatModeStore(string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(baseDirectory);
        _ = Directory.CreateDirectory(baseDirectory);
        _modesFilePath = Path.Combine(baseDirectory, ModesFileName);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChatMode>> GetAllModesAsync(CancellationToken ct = default)
    {
        var userModes = await LoadUserModesAsync(ct);

        // Return system modes first, then user modes sorted by name
        return
        [
            .. SystemChatModes.All,
            .. userModes.OrderBy(m => m.Name)
        ];
    }

    /// <inheritdoc />
    public async Task<ChatMode?> GetModeAsync(string modeId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(modeId);

        // Check system modes first
        var systemMode = SystemChatModes.GetById(modeId);
        if (systemMode != null)
        {
            return systemMode;
        }

        // Check user modes
        var userModes = await LoadUserModesAsync(ct);
        return userModes.FirstOrDefault(m => m.Id == modeId);
    }

    /// <inheritdoc />
    public async Task<ChatMode> CreateModeAsync(ChatModeCreateUpdate mode, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mode);

        await _lock.WaitAsync(ct);
        try
        {
            var userModes = await LoadUserModesAsync(ct);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var newMode = new ChatMode
            {
                Id = Guid.NewGuid().ToString(),
                Name = mode.Name,
                Description = mode.Description,
                SystemPrompt = mode.SystemPrompt,
                EnabledTools = mode.EnabledTools,
                IsSystemDefined = false,
                CreatedAt = now,
                UpdatedAt = now,
            };

            var updatedModes = userModes.Append(newMode).ToList();
            await SaveUserModesAsync(updatedModes, ct);

            return newMode;
        }
        finally
        {
            _ = _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ChatMode> UpdateModeAsync(string modeId, ChatModeCreateUpdate mode, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(modeId);
        ArgumentNullException.ThrowIfNull(mode);

        if (SystemChatModes.IsSystemMode(modeId))
        {
            throw new InvalidOperationException($"Cannot update system-defined mode '{modeId}'.");
        }

        await _lock.WaitAsync(ct);
        try
        {
            var userModes = await LoadUserModesAsync(ct);
            var existingIndex = userModes.FindIndex(m => m.Id == modeId);

            if (existingIndex < 0)
            {
                throw new KeyNotFoundException($"Mode '{modeId}' not found.");
            }

            var existing = userModes[existingIndex];
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var updatedMode = existing with
            {
                Name = mode.Name,
                Description = mode.Description,
                SystemPrompt = mode.SystemPrompt,
                EnabledTools = mode.EnabledTools,
                UpdatedAt = now,
            };

            userModes[existingIndex] = updatedMode;
            await SaveUserModesAsync(userModes, ct);

            return updatedMode;
        }
        finally
        {
            _ = _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task DeleteModeAsync(string modeId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(modeId);

        if (SystemChatModes.IsSystemMode(modeId))
        {
            throw new InvalidOperationException($"Cannot delete system-defined mode '{modeId}'.");
        }

        await _lock.WaitAsync(ct);
        try
        {
            var userModes = await LoadUserModesAsync(ct);
            var updatedModes = userModes.Where(m => m.Id != modeId).ToList();
            await SaveUserModesAsync(updatedModes, ct);
        }
        finally
        {
            _ = _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ChatMode> CopyModeAsync(string modeId, string newName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(modeId);
        ArgumentNullException.ThrowIfNull(newName);

        var sourceMode = await GetModeAsync(modeId, ct)
            ?? throw new KeyNotFoundException($"Mode '{modeId}' not found.");

        await _lock.WaitAsync(ct);
        try
        {
            var userModes = await LoadUserModesAsync(ct);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var newMode = new ChatMode
            {
                Id = Guid.NewGuid().ToString(),
                Name = newName,
                Description = sourceMode.Description,
                SystemPrompt = sourceMode.SystemPrompt,
                EnabledTools = sourceMode.EnabledTools,
                IsSystemDefined = false,
                CreatedAt = now,
                UpdatedAt = now,
            };

            var updatedModes = userModes.Append(newMode).ToList();
            await SaveUserModesAsync(updatedModes, ct);

            return newMode;
        }
        finally
        {
            _ = _lock.Release();
        }
    }

    private async Task<List<ChatMode>> LoadUserModesAsync(CancellationToken ct)
    {
        if (!File.Exists(_modesFilePath))
        {
            return [];
        }

        try
        {
            var json = await File.ReadAllTextAsync(_modesFilePath, ct);
            return JsonSerializer.Deserialize<List<ChatMode>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            // If file is corrupted, start fresh
            return [];
        }
    }

    private async Task SaveUserModesAsync(List<ChatMode> modes, CancellationToken ct)
    {
        // Write to temp file first, then rename for atomic operation
        var tempFile = _modesFilePath + ".tmp";
        var json = JsonSerializer.Serialize(modes, JsonOptions);

        await File.WriteAllTextAsync(tempFile, json, ct);

        // Atomic rename
        File.Move(tempFile, _modesFilePath, overwrite: true);
    }
}
