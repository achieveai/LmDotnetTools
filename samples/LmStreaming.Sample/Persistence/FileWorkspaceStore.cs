using System.Text.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmCore.Utils;
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
        return [_defaultWorkspace, .. userWorkspaces.OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase)];
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
            throw new InvalidOperationException($"Could not derive a valid workspace directory from '{rawDir}'.");
        }

        await _lock.WaitAsync(ct);
        try
        {
            var userWorkspaces = await LoadUserWorkspacesAsync(ct);

            var collision =
                string.Equals(directory, _defaultWorkspace.DirectoryRelPath, StringComparison.OrdinalIgnoreCase)
                || userWorkspaces.Any(w =>
                    string.Equals(w.DirectoryRelPath, directory, StringComparison.OrdinalIgnoreCase)
                );
            if (collision)
            {
                throw new InvalidOperationException($"A workspace with directory '{directory}' already exists.");
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var workspace = new Workspace
            {
                Id = Guid.NewGuid().ToString(),
                Name = name,
                DirectoryRelPath = directory,
                Marketplaces = dto.Marketplaces ?? [],
                // Tri-state seeding: a null selection stays null ("no preference"), it is not
                // collapsed to [] the way Marketplaces is. Revision starts at the default 0.
                PluginSelection = dto.PluginSelection,
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

        SystemDefinedWorkspaceRule.ThrowIfSystemDefined(
            id,
            isSystemDefined: string.Equals(id, _defaultWorkspace.Id, StringComparison.Ordinal)
        );

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
            SystemDefinedWorkspaceRule.ThrowIfSystemDefined(id, existing.IsSystemDefined);

            if (dto.PluginSelection.IsSet)
            {
                WorkspaceRevisionConflictException.ThrowIfMismatch(id, dto.PluginsRevision, existing.PluginsRevision);
            }

            var updatedWorkspace = existing with
            {
                Marketplaces = dto.Marketplaces ?? [],
                PluginSelection = dto.PluginSelection.IsSet ? dto.PluginSelection.Value : existing.PluginSelection,
                PluginsRevision = dto.PluginSelection.IsSet ? existing.PluginsRevision + 1 : existing.PluginsRevision,
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

    private async Task<List<Workspace>> LoadUserWorkspacesAsync(CancellationToken ct)
    {
        if (!File.Exists(_workspacesFilePath))
        {
            return [];
        }

        try
        {
            var json = await File.ReadAllTextAsync(_workspacesFilePath, ct);
            var loaded = JsonSerializer.Deserialize<List<Workspace>>(json, JsonOptions) ?? [];

            // A null ENTRY is damage, not a value to repair: there is no workspace there to normalize.
            // Reported as a corrupt catalog (which every action already maps to 503) rather than
            // skipped, because silently dropping it would make a truncated or half-written file look
            // like a successful deletion — the one failure mode a workspace store must never fake.
            var nullEntry = loaded.FindIndex(static w => w is null);
            if (nullEntry >= 0)
            {
                throw new WorkspaceCatalogCorruptException(
                    _workspacesFilePath,
                    $"the entry at index {nullEntry} is null"
                );
            }

            // A null MARKETPLACES member, in contrast, has an unambiguous repair, and it needs one.
            // `Workspace.Marketplaces` is non-nullable and carries an `= []` initializer, so it reads
            // as though a null cannot survive deserialization. It can: nullable reference annotations
            // are a compile-time analysis and enforce nothing here, so an explicit `"marketplaces":
            // null` in the file is written straight into the member. Both WRITE paths above already
            // normalize with `?? []`; without the same normalization on the way back in, one such
            // entry made EVERY reader throw — including the catalog listing, so the UI could not load
            // and no API call could repair the file that broke it.
            return [.. loaded.Select(static w => w.Marketplaces is null ? w with { Marketplaces = [] } : w)];
        }
        catch (JsonException ex)
        {
            throw new WorkspaceCatalogCorruptException(_workspacesFilePath, ex.Message, ex);
        }
    }

    /// <summary>
    /// Atomic write through the shared <see cref="AtomicFile"/> helper: a unique staging file, then a
    /// rename over the target with a bounded retry, and cleanup of the staging file on any failure.
    /// <para>
    /// <see cref="_lock"/> covers none of that. It is a per-INSTANCE semaphore, so it serializes nothing
    /// between two stores over one catalog directory — which is what every request resolving a
    /// per-gateway catalog constructs — and nothing at all across processes. A plain concurrent read of
    /// the catalog (the workspace list is read on every page load) is by itself enough to make a Windows
    /// rename over the destination fail.
    /// </para>
    /// <para>
    /// Serializes to a string and writes it WITHOUT a byte-order mark, which is what this store has always
    /// written; <see cref="AtomicFile.WriteJsonAsync"/> emits one, so this path stages its own bytes rather
    /// than rewriting every existing catalog's encoding for no gain.
    /// </para>
    /// </summary>
    private Task SaveUserWorkspacesAsync(List<Workspace> workspaces, CancellationToken ct)
    {
        // Serialize before staging so a serialization failure never creates a temp file to clean up.
        var json = JsonSerializer.Serialize(workspaces, JsonOptions);

        return AtomicFile.WriteAsync(
            _workspacesFilePath,
            (tempFile, token) => File.WriteAllTextAsync(tempFile, json, token),
            ct
        );
    }
}

/// <summary>
/// Thrown when a workspace update with an explicit <c>PluginSelection</c> supplies a stale or
/// missing <c>pluginsRevision</c>. CAS is mandatory for any explicit plugin-selection change: a
/// missing revision is reported with <see cref="ExpectedRevision"/> equal to the sentinel <c>-1</c>
/// (no real revision can ever equal it) to distinguish "revision omitted entirely" from "revision
/// stale" (a real, mismatched, non-negative value). Only raised for updates that touch
/// <see cref="WorkspaceUpdate.PluginSelection"/>; marketplace-only updates never check the revision.
/// </summary>
public sealed class WorkspaceRevisionConflictException : Exception
{
    /// <summary>Creates a new <see cref="WorkspaceRevisionConflictException"/>.</summary>
    /// <param name="workspaceId">The workspace whose update was rejected.</param>
    /// <param name="expectedRevision">The revision the caller supplied, or <c>-1</c> when omitted.</param>
    /// <param name="actualRevision">The workspace's current revision.</param>
    public WorkspaceRevisionConflictException(string workspaceId, int expectedRevision, int actualRevision)
        : base(
            $"Workspace '{workspaceId}' plugins revision conflict: expected {expectedRevision}, actual {actualRevision}."
        )
    {
        WorkspaceId = workspaceId;
        ExpectedRevision = expectedRevision;
        ActualRevision = actualRevision;
    }

    /// <summary>The workspace whose update was rejected.</summary>
    public string WorkspaceId { get; }

    /// <summary>The revision the caller supplied, or the sentinel <c>-1</c> when it was omitted.</summary>
    public int ExpectedRevision { get; }

    /// <summary>The workspace's current revision at the time of the rejected update.</summary>
    public int ActualRevision { get; }

    /// <summary>
    /// The single owner of the compare-and-swap rule. Two callers need it — the store, which applies
    /// it atomically under its write lock, and the plugin-selection orchestrator, which must reject a
    /// stale request BEFORE it creates any candidate sandbox sessions (the store's own check runs at
    /// persist time, by which point candidates would already exist). Duplicating the rule is exactly
    /// how the marketplace-resolution bug arose: two copies of one rule drifted and disagreed on the
    /// same input. Both callers therefore route through here.
    /// </summary>
    /// <param name="workspaceId">The workspace being updated.</param>
    /// <param name="suppliedRevision">The revision the caller supplied; <c>null</c> when omitted.</param>
    /// <param name="actualRevision">The workspace's current stored revision.</param>
    /// <exception cref="WorkspaceRevisionConflictException">
    /// The revision was omitted (reported as <see cref="ExpectedRevision"/> <c>-1</c>) or did not
    /// match <paramref name="actualRevision"/>.
    /// </exception>
    public static void ThrowIfMismatch(string workspaceId, int? suppliedRevision, int actualRevision)
    {
        // An omitted revision is ambiguous ("caller didn't know it" vs "caller doesn't care") and must
        // never silently overwrite a concurrent change — reject it exactly like a stale revision, using
        // sentinel -1 (no real revision can equal it) so the payload still distinguishes "omitted" from
        // "stale".
        if (suppliedRevision is not int expected)
        {
            throw new WorkspaceRevisionConflictException(workspaceId, expectedRevision: -1, actualRevision);
        }

        if (expected != actualRevision)
        {
            throw new WorkspaceRevisionConflictException(workspaceId, expected, actualRevision);
        }
    }
}

/// <summary>
/// The single owner of the "system-defined workspaces are immutable" rule.
/// <para>
/// Three callers need it, for the same reason the revision CAS has two: the store applies it under
/// its write lock and is the authority, while the plugin-selection orchestrator must reject BEFORE it
/// snapshots partitions, waits for idle, and creates candidate sandbox sessions. Left to the store
/// alone the rejection arrives at persist time — after real gateway sessions were built and torn
/// down, and after a busy run has had the chance to turn a 400 into a 503 restart timeout or a stale
/// revision into a 409. Routing every caller through here is what keeps a doomed request free of side
/// effects, and keeps one rule from drifting into three.
/// </para>
/// <para>
/// The message is load-bearing: <c>WorkspacesController</c> maps a bare
/// <see cref="InvalidOperationException"/> to <c>400</c> in its trailing catch, so the exception type
/// and text are the wire contract and must not change.
/// </para>
/// </summary>
internal static class SystemDefinedWorkspaceRule
{
    /// <summary>Rejects a mutation targeting a system-defined workspace.</summary>
    /// <param name="workspaceId">The workspace being updated; interpolated into the message.</param>
    /// <param name="isSystemDefined">Whether that workspace is system-defined.</param>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="isSystemDefined"/> is <see langword="true"/>. Surfaces as <c>400</c>.
    /// </exception>
    public static void ThrowIfSystemDefined(string workspaceId, bool isSystemDefined)
    {
        if (isSystemDefined)
        {
            throw new InvalidOperationException($"Cannot update system-defined workspace '{workspaceId}'.");
        }
    }
}
