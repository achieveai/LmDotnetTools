using System.Text.Json;
using LmStreaming.Sample.Models;

namespace LmStreaming.Sample.Persistence;

/// <summary>
/// Resolves one gateway/AppId-scoped workspace catalog and archives the ambiguous legacy catalog.
/// </summary>
public sealed class GatewayWorkspaceCatalogResolver
{
    private const string LegacyFileName = "workspaces.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly TimeProvider _timeProvider;

    public GatewayWorkspaceCatalogResolver(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<GatewayWorkspaceCatalogResolution> ResolveAsync(
        string rootDirectory,
        GatewayWorkspaceCatalogIdentity identity,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(identity);

        _ = Directory.CreateDirectory(rootDirectory);
        var legacyDirectory = Path.Combine(rootDirectory, "legacy");
        _ = Directory.CreateDirectory(legacyDirectory);

        await using var migrationLock = await AcquireLockAsync(
            Path.Combine(legacyDirectory, "migration.lock"),
            ct
        );

        var archivePath = await CompleteOrRunLegacyMigrationAsync(
            rootDirectory,
            legacyDirectory,
            identity,
            ct
        );

        var catalogDirectory = Path.Combine(rootDirectory, "gateways", identity.CatalogKey);
        _ = Directory.CreateDirectory(catalogDirectory);
        var manifestPath = Path.Combine(catalogDirectory, "gateway.json");
        var manifest = new GatewayWorkspaceCatalogManifest
        {
            SchemaVersion = identity.SchemaVersion,
            DerivationVersion = identity.DerivationVersion,
            CanonicalBaseUrl = identity.CanonicalBaseUrl,
            AppId = identity.AppId,
        };

        if (File.Exists(manifestPath))
        {
            var existing = await ReadJsonAsync<GatewayWorkspaceCatalogManifest>(manifestPath, ct);
            identity.ValidateManifest(existing);
        }
        else
        {
            await WriteJsonAtomicAsync(manifestPath, manifest, ct);
        }

        return new GatewayWorkspaceCatalogResolution(
            catalogDirectory,
            Path.Combine(catalogDirectory, LegacyFileName),
            manifestPath,
            archivePath
        );
    }

    private async Task<string?> CompleteOrRunLegacyMigrationAsync(
        string rootDirectory,
        string legacyDirectory,
        GatewayWorkspaceCatalogIdentity identity,
        CancellationToken ct
    )
    {
        var legacyPath = Path.Combine(rootDirectory, LegacyFileName);
        var pendingPath = Path.Combine(legacyDirectory, "migration.pending.json");
        var completedPath = Path.Combine(legacyDirectory, "migration.json");

        if (File.Exists(completedPath))
        {
            var completed = await ReadJsonAsync<GatewayWorkspaceMigrationMarker>(completedPath, ct);
            return Path.Combine(legacyDirectory, completed.ArchiveFileName);
        }

        GatewayWorkspaceMigrationMarker? marker = null;
        if (File.Exists(pendingPath))
        {
            marker = await ReadJsonAsync<GatewayWorkspaceMigrationMarker>(pendingPath, ct);
        }
        else if (File.Exists(legacyPath))
        {
            var stamp = _timeProvider.GetUtcNow().ToString("yyyyMMddTHHmmssfffffffZ");
            marker = new GatewayWorkspaceMigrationMarker
            {
                SchemaVersion = 1,
                CanonicalBaseUrl = identity.CanonicalBaseUrl,
                AppId = identity.AppId,
                ArchiveFileName = $"workspaces.{stamp}.json",
                StartedAt = _timeProvider.GetUtcNow(),
            };
            await WriteJsonAtomicAsync(pendingPath, marker, ct);
        }

        if (marker is null)
        {
            return null;
        }

        var archivePath = Path.Combine(legacyDirectory, marker.ArchiveFileName);
        if (File.Exists(legacyPath))
        {
            if (File.Exists(archivePath))
            {
                throw new InvalidOperationException(
                    $"Legacy workspace archive already exists at '{archivePath}'."
                );
            }

            File.Move(legacyPath, archivePath);
        }

        if (!File.Exists(archivePath))
        {
            throw new InvalidOperationException(
                $"Pending workspace migration archive '{archivePath}' is missing."
            );
        }

        _ = await ReadJsonAsync<List<Workspace>>(archivePath, ct);
        var completedMarker = marker with { CompletedAt = _timeProvider.GetUtcNow() };
        await WriteJsonAtomicAsync(completedPath, completedMarker, ct);
        File.Delete(pendingPath);
        return archivePath;
    }

    private static async Task<FileStream> AcquireLockAsync(string path, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous
                );
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(50, ct);
            }
        }
    }

    private static async Task<T> ReadJsonAsync<T>(string path, CancellationToken ct)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            return JsonSerializer.Deserialize<T>(json, JsonOptions)
                ?? throw new WorkspaceCatalogCorruptException(path, "JSON content was null.");
        }
        catch (JsonException ex)
        {
            throw new WorkspaceCatalogCorruptException(path, ex.Message, ex);
        }
    }

    private static async Task WriteJsonAtomicAsync<T>(string path, T value, CancellationToken ct)
    {
        var tempPath = path + ".tmp";
        var json = JsonSerializer.Serialize(value, JsonOptions);
        await File.WriteAllTextAsync(tempPath, json, ct);
        File.Move(tempPath, path, overwrite: true);
    }
}

public sealed record GatewayWorkspaceCatalogResolution(
    string CatalogDirectory,
    string WorkspacesFilePath,
    string ManifestPath,
    string? LegacyArchivePath
);

public sealed record GatewayWorkspaceMigrationMarker
{
    public int SchemaVersion { get; init; } = 1;
    public required string CanonicalBaseUrl { get; init; }
    public required string AppId { get; init; }
    public required string ArchiveFileName { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}

public sealed class WorkspaceCatalogCorruptException : InvalidOperationException
{
    public WorkspaceCatalogCorruptException(string path, string message, Exception? inner = null)
        : base($"Workspace catalog '{path}' is corrupt: {message}", inner)
    {
        Path = path;
    }

    public string Path { get; }
}
