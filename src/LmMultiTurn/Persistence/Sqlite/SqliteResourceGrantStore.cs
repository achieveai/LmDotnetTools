using System.Globalization;
using AchieveAi.LmDotnetTools.LmCore.Identity;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.Sqlite;

/// <summary>
/// SQLite-backed <see cref="IResourceGrantStore"/> over the <c>resource_grants</c> table of
/// migration step 4 (P1 spec 8.4).
/// </summary>
/// <remarks>
/// Does not own its <see cref="ISqliteConnectionFactory"/> and is deliberately not disposable, so a
/// host can register it as a singleton without making the synchronous
/// <c>ServiceProvider.Dispose()</c> throw on an <c>IAsyncDisposable</c>-only dependency - the same
/// constraint <see cref="SqliteTenantStore"/> works under.
/// </remarks>
public sealed class SqliteResourceGrantStore : IResourceGrantStore
{
    private const string RoleViewer = "viewer";
    private const string RoleEditor = "editor";

    private readonly ISqliteConnectionFactory _factory;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private bool _schemaInitialized;

    /// <summary>Creates a store over the given connection factory, which it does not own.</summary>
    /// <param name="factory">Connection factory for the database holding the grant table.</param>
    public SqliteResourceGrantStore(ISqliteConnectionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    private async Task EnsureSchemaAsync(CancellationToken ct)
    {
        if (_schemaInitialized)
        {
            return;
        }

        await _schemaLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_schemaInitialized)
            {
                return;
            }

            await SqliteSchemaInitializer.InitializeSchemaAsync(_factory, ct).ConfigureAwait(false);
            _schemaInitialized = true;
        }
        finally
        {
            _ = _schemaLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<GrantRole?> FindGrantAsync(
        string tenantId,
        ResourceRef resource,
        string subjectId,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _factory.GetConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT role FROM resource_grants
             WHERE tenant_id     = $tenantId
               AND resource_type = $resourceType
               AND resource_id   = $resourceId
               AND subject_id    = $subjectId
               AND (expires_at IS NULL OR expires_at > $now);
            """;
        _ = command.Parameters.AddWithValue("$tenantId", tenantId);
        _ = command.Parameters.AddWithValue("$resourceType", resource.Type);
        _ = command.Parameters.AddWithValue("$resourceId", resource.Id);
        _ = command.Parameters.AddWithValue("$subjectId", subjectId);
        _ = command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());

        var stored = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return stored is string role ? ParseRole(role) : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListGrantedResourceIdsAsync(
        string tenantId,
        string subjectId,
        string resourceType,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceType);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _factory.GetConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT resource_id FROM resource_grants
             WHERE tenant_id     = $tenantId
               AND subject_id    = $subjectId
               AND resource_type = $resourceType
               AND (expires_at IS NULL OR expires_at > $now);
            """;
        _ = command.Parameters.AddWithValue("$tenantId", tenantId);
        _ = command.Parameters.AddWithValue("$subjectId", subjectId);
        _ = command.Parameters.AddWithValue("$resourceType", resourceType);
        _ = command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());

        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ResourceGrant>> ListGrantsForResourceAsync(
        string tenantId,
        ResourceRef resource,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _factory.GetConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT subject_id, role, granted_by, granted_at, expires_at
              FROM resource_grants
             WHERE tenant_id     = $tenantId
               AND resource_type = $resourceType
               AND resource_id   = $resourceId
             ORDER BY subject_id;
            """;
        _ = command.Parameters.AddWithValue("$tenantId", tenantId);
        _ = command.Parameters.AddWithValue("$resourceType", resource.Type);
        _ = command.Parameters.AddWithValue("$resourceId", resource.Id);

        var grants = new List<ResourceGrant>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (ParseRole(reader.GetString(1)) is not { } role)
            {
                // Skipped, not defaulted to Viewer. Defaulting made this listing disagree with
                // FindGrantAsync about the same row: the point read denies it, so presenting it
                // here as a viewer grant tells the owner someone has access that nothing will
                // actually honour. `continue`, never `break` - one unreadable row must not hide
                // every valid grant ordered after it.
                continue;
            }

            grants.Add(new ResourceGrant
            {
                TenantId = tenantId,
                Resource = resource,
                SubjectId = reader.GetString(0),
                Role = role,
                GrantedBy = reader.GetString(2),
                GrantedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
                ExpiresAt = reader.IsDBNull(4)
                    ? null
                    : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
            });
        }

        return grants;
    }

    /// <inheritdoc />
    public async Task GrantAsync(ResourceGrant grant, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentException.ThrowIfNullOrWhiteSpace(grant.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(grant.SubjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(grant.GrantedBy);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _factory.GetConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO resource_grants
                   (tenant_id, resource_type, resource_id, subject_id, role, granted_by, granted_at, expires_at)
            VALUES ($tenantId, $resourceType, $resourceId, $subjectId, $role, $grantedBy, $grantedAt, $expiresAt)
            ON CONFLICT(tenant_id, resource_type, resource_id, subject_id) DO UPDATE SET
                role       = excluded.role,
                granted_by = excluded.granted_by,
                granted_at = excluded.granted_at,
                expires_at = excluded.expires_at;
            """;
        _ = command.Parameters.AddWithValue("$tenantId", grant.TenantId);
        _ = command.Parameters.AddWithValue("$resourceType", grant.Resource.Type);
        _ = command.Parameters.AddWithValue("$resourceId", grant.Resource.Id);
        _ = command.Parameters.AddWithValue("$subjectId", grant.SubjectId);
        _ = command.Parameters.AddWithValue("$role", FormatRole(grant.Role));
        _ = command.Parameters.AddWithValue("$grantedBy", grant.GrantedBy);
        _ = command.Parameters.AddWithValue("$grantedAt", grant.GrantedAt.ToUnixTimeMilliseconds());
        _ = command.Parameters.AddWithValue(
            "$expiresAt", (object?)grant.ExpiresAt?.ToUnixTimeMilliseconds() ?? DBNull.Value);

        _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> RevokeAsync(
        string tenantId,
        ResourceRef resource,
        string subjectId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _factory.GetConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM resource_grants
             WHERE tenant_id     = $tenantId
               AND resource_type = $resourceType
               AND resource_id   = $resourceId
               AND subject_id    = $subjectId;
            """;
        _ = command.Parameters.AddWithValue("$tenantId", tenantId);
        _ = command.Parameters.AddWithValue("$resourceType", resource.Type);
        _ = command.Parameters.AddWithValue("$resourceId", resource.Id);
        _ = command.Parameters.AddWithValue("$subjectId", subjectId);

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public async Task<bool> HasAnyGrantAsync(
        string tenantId,
        ResourceRef resource,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _factory.GetConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM resource_grants
             WHERE tenant_id     = $tenantId
               AND resource_type = $resourceType
               AND resource_id   = $resourceId
               AND (expires_at IS NULL OR expires_at > $now);
            """;
        _ = command.Parameters.AddWithValue("$tenantId", tenantId);
        _ = command.Parameters.AddWithValue("$resourceType", resource.Type);
        _ = command.Parameters.AddWithValue("$resourceId", resource.Id);
        _ = command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());

        var count = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(count, CultureInfo.InvariantCulture) > 0;
    }

    private static string FormatRole(GrantRole role) =>
        role == GrantRole.Editor ? RoleEditor : RoleViewer;

    /// <summary>
    /// Reads a stored role. An unrecognised value reads as null - no grant - rather than as
    /// <see cref="GrantRole.Viewer"/>: a value the CHECK constraint should have refused must fail
    /// closed, never confer anything.
    /// </summary>
    private static GrantRole? ParseRole(string role) => role switch
    {
        RoleEditor => GrantRole.Editor,
        RoleViewer => GrantRole.Viewer,
        _ => null,
    };
}
