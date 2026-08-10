using System.Text.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using LmStreaming.Sample.Models;

namespace LmStreaming.Sample.Persistence;

/// <summary>
/// File-based implementation of <see cref="IWorkspaceStore"/>.
/// Stores user-defined workspaces in a JSON file. The default workspace is seeded in-memory and
/// maps to the configured sandbox workspace leaf.
/// </summary>
public sealed class FileWorkspaceStore : IWorkspaceStore
{
    private const string WorkspacesFileName = "workspaces.json";

    private readonly string _workspacesFilePath;
    private readonly Workspace _defaultWorkspace;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Creates a new <see cref="FileWorkspaceStore"/>.
    /// </summary>
    /// <param name="baseDirectory">Base directory for storing workspace data.</param>
    /// <param name="defaultDirectoryRelPath">
    /// The directory leaf the seeded default workspace maps to (today's configured sandbox leaf).
    /// </param>
    public FileWorkspaceStore(string baseDirectory, string? defaultDirectoryRelPath = null)
    {
        ArgumentNullException.ThrowIfNull(baseDirectory);
        _ = Directory.CreateDirectory(baseDirectory);
        _workspacesFilePath = Path.Combine(baseDirectory, WorkspacesFileName);

        _defaultWorkspace = new Workspace
        {
            Id = SandboxSessionRegistry.DefaultWorkspaceId,
            Name = "Default",
            DirectoryRelPath = defaultDirectoryRelPath ?? string.Empty,
            Marketplaces = [],
            IsSystemDefined = true,
            CreatedAt = 0,
            UpdatedAt = 0,
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Workspace>> GetAllAsync(CancellationToken ct = default)
    {
        var userWorkspaces = await LoadUserWorkspacesAsync(ct);
        return
        [
            _defaultWorkspace,
            .. userWorkspaces.OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <inheritdoc />
    public async Task<Workspace?> GetAsync(string id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(id);

        if (string.Equals(id, _defaultWorkspace.Id, StringComparison.Ordinal))
        {
            return _defaultWorkspace;
        }

        var userWorkspaces = await LoadUserWorkspacesAsync(ct);
        return userWorkspaces.FirstOrDefault(w => w.Id == id);
    }

    /// <inheritdoc />
    public async Task<Workspace> CreateAsync(WorkspaceCreate dto, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var name = dto.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(name))
        {
            throw new InvalidOperationException("Workspace name must not be empty.");
        }

        var rawDir = string.IsNullOrWhiteSpace(dto.DirectoryRelPath) ? name : dto.DirectoryRelPath;
        var directory = SanitizeDirectory(rawDir);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException(
                $"Could not derive a valid workspace directory from '{rawDir}'."
            );
        }

        // A home that was ASKED for but sanitizes away is rejected rather than silently downgraded to the
        // workspace root: the caller's whole reason for setting it is that the root is the wrong place to
        // start, and starting there anyway is the failure that looks like success.
        var home = SanitizeHomeRelPath(dto.HomeRelPath);
        if (!string.IsNullOrWhiteSpace(dto.HomeRelPath) && home is null)
        {
            throw new InvalidOperationException(
                $"Could not derive a valid workspace home from '{dto.HomeRelPath}'."
            );
        }

        await _lock.WaitAsync(ct);
        try
        {
            var userWorkspaces = await LoadUserWorkspacesAsync(ct);

            // Two workspaces collide when they resolve to the same PLACE, which since homes exist is the
            // (directory, home) pair rather than the directory alone. Several workspaces deliberately share
            // one mount when that mount holds several working copies — that is the point of a home — and
            // rejecting them on the directory alone would make only the first of them creatable.
            bool SamePlace(Workspace other) =>
                string.Equals(other.DirectoryRelPath, directory, StringComparison.OrdinalIgnoreCase)
                && string.Equals(other.HomeRelPath ?? string.Empty, home ?? string.Empty, StringComparison.OrdinalIgnoreCase);

            var collision = SamePlace(_defaultWorkspace) || userWorkspaces.Any(SamePlace);
            if (collision)
            {
                throw new InvalidOperationException(
                    $"A workspace with directory '{directory}'"
                        + (home is null ? string.Empty : $" and home '{home}'")
                        + " already exists."
                );
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var workspace = new Workspace
            {
                Id = Guid.NewGuid().ToString(),
                Name = name,
                DirectoryRelPath = directory,
                HomeRelPath = home,
                Marketplaces = dto.Marketplaces ?? [],
                IsSystemDefined = false,
                CreatedAt = now,
                UpdatedAt = now,
            };

            var updated = userWorkspaces.Append(workspace).ToList();
            await SaveUserWorkspacesAsync(updated, ct);

            return workspace;
        }
        finally
        {
            _ = _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<Workspace> UpdateAsync(string id, WorkspaceUpdate dto, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(dto);

        if (string.Equals(id, _defaultWorkspace.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Cannot update system-defined workspace '{id}'.");
        }

        await _lock.WaitAsync(ct);
        try
        {
            var userWorkspaces = await LoadUserWorkspacesAsync(ct);
            var index = userWorkspaces.FindIndex(w => w.Id == id);
            if (index < 0)
            {
                throw new KeyNotFoundException($"Workspace '{id}' not found.");
            }

            var existing = userWorkspaces[index];
            if (existing.IsSystemDefined)
            {
                throw new InvalidOperationException($"Cannot update system-defined workspace '{id}'.");
            }

            var updatedWorkspace = existing with
            {
                Marketplaces = dto.Marketplaces ?? [],
                UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };

            userWorkspaces[index] = updatedWorkspace;
            await SaveUserWorkspacesAsync(userWorkspaces, ct);

            return updatedWorkspace;
        }
        finally
        {
            _ = _lock.Release();
        }
    }

    /// <summary>
    /// Sanitizes a raw directory string into a safe single-segment leaf: lowercased, trimmed,
    /// whitespace runs collapsed to '-', and any path-invalid characters (plus '/', '\\', '..')
    /// stripped. Returns an empty string when nothing safe remains.
    /// </summary>
    internal static string SanitizeDirectory(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var lowered = raw.Trim().ToLowerInvariant();

        // Collapse whitespace runs into a single '-'.
        var collapsed = string.Join('-', lowered.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '/', '\\' };
        var sanitized = new string([.. collapsed.Where(c => !invalid.Contains(c))]);

        // Strip any '..' sequences that survived character filtering.
        sanitized = sanitized.Replace("..", string.Empty);

        return sanitized.Trim('-');
    }

    /// <summary>
    /// Sanitizes a workspace-relative home path. Unlike <see cref="SanitizeDirectory"/> this KEEPS the
    /// separators — a home exists to name a subdirectory of the mount, so collapsing it to one segment
    /// would point at a sibling of the intended directory rather than at it. Each segment is run through
    /// the same character filter, '.'/'..' segments are dropped/rejected, and the result is rejoined with
    /// '/'. Returns null when the input is blank or nothing safe survives.
    /// </summary>
    /// <remarks>
    /// '..' is REJECTED (null) rather than stripped. Stripping is what <see cref="SanitizeDirectory"/>
    /// does, and it is safe there only because the result is a single segment either way. Here, silently
    /// turning 'a/../../etc' into 'a/etc' would answer a traversal attempt with a plausible-looking path
    /// instead of a refusal, and the caller has no way to tell the two apart.
    /// </remarks>
    internal static string? SanitizeHomeRelPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '/', '\\' };
        var segments = new List<string>();
        foreach (
            var rawSegment in raw.Trim().Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
        )
        {
            var lowered = rawSegment.Trim().ToLowerInvariant();
            if (lowered == ".")
            {
                continue;
            }

            if (lowered == "..")
            {
                return null;
            }

            var collapsed = string.Join(
                '-',
                lowered.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            );
            var sanitized = new string([.. collapsed.Where(c => !invalid.Contains(c))]).Trim('-');
            if (sanitized.Length == 0 || sanitized.Contains("..", StringComparison.Ordinal))
            {
                return null;
            }

            segments.Add(sanitized);
        }

        return segments.Count == 0 ? null : string.Join('/', segments);
    }

    private async Task<List<Workspace>> LoadUserWorkspacesAsync(CancellationToken ct)
    {
        if (!File.Exists(_workspacesFilePath))
        {
            return [];
        }

        try
        {
            var json = await File.ReadAllTextAsync(_workspacesFilePath, ct);
            return JsonSerializer.Deserialize<List<Workspace>>(json, JsonOptions) ?? [];
        }
        catch (JsonException ex)
        {
            throw new WorkspaceCatalogCorruptException(_workspacesFilePath, ex.Message, ex);
        }
    }

    private async Task SaveUserWorkspacesAsync(List<Workspace> workspaces, CancellationToken ct)
    {
        // Write to a temp file first, then rename for an atomic operation.
        var tempFile = _workspacesFilePath + ".tmp";
        var json = JsonSerializer.Serialize(workspaces, JsonOptions);

        await File.WriteAllTextAsync(tempFile, json, ct);

        File.Move(tempFile, _workspacesFilePath, overwrite: true);
    }
}
