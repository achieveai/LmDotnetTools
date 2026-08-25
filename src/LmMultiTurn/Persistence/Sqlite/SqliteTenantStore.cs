using System.Globalization;
using AchieveAi.LmDotnetTools.LmCore.Identity;
using Microsoft.Data.Sqlite;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.Sqlite;

/// <summary>
/// SQLite-backed <see cref="ITenantStore"/> over the <c>tenants</c> and <c>tenant_admins</c>
/// tables of migration step 2.
/// </summary>
/// <remarks>
/// Does not own its <see cref="ISqliteConnectionFactory"/> and is deliberately not disposable, so
/// a host can register it as a singleton without making <c>ServiceProvider.Dispose()</c> throw on
/// an <c>IAsyncDisposable</c>-only dependency - the same constraint <see cref="SqliteNotifyWaitStore"/>
/// works under. Schema initialization is lazy, on the same double-checked-lock pattern.
/// </remarks>
public sealed class SqliteTenantStore : ITenantStore
{
    private const string StatusActive = "active";
    private const string StatusSuspended = "suspended";
    private const string StatusQuarantined = "quarantined";

    private readonly ISqliteConnectionFactory _factory;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private bool _schemaInitialized;

    /// <summary>Creates a store over the given connection factory, which it does not own.</summary>
    /// <param name="factory">Connection factory for the database holding the tenant registry.</param>
    public SqliteTenantStore(ISqliteConnectionFactory factory)
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
    public async Task<TenantRecord?> FindByEntraTenantIdAsync(
        string entraTenantId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entraTenantId);

        // Normalized on BOTH sides (#347). SQLite compares TEXT under BINARY collation, so an
        // operator who provisioned an upper-cased GUID pasted out of a ticket would otherwise get
        // a 201 and then every user from that directory would get tenant_not_provisioned forever.
        return await FindAsync(
                "SELECT tenant_id, entra_tenant_id, display_name, status, created_at, created_by "
                    + "FROM tenants WHERE entra_tenant_id = $key;",
                NormalizeEntraTenantId(entraTenantId)!,
                ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TenantRecord?> FindByTenantIdAsync(
        string tenantId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        return await FindAsync(
                "SELECT tenant_id, entra_tenant_id, display_name, status, created_at, created_by "
                    + "FROM tenants WHERE tenant_id = $key;",
                tenantId,
                ct)
            .ConfigureAwait(false);
    }

    private async Task<TenantRecord?> FindAsync(string sql, string key, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _factory.GetConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = command.Parameters.AddWithValue("$key", key);

        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new TenantRecord
        {
            TenantId = reader.GetString(0),
            EntraTenantId = reader.IsDBNull(1) ? null : reader.GetString(1),
            DisplayName = reader.GetString(2),
            Status = ParseStatus(reader.GetString(3)),
            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
            CreatedBy = reader.GetString(5),
        };
    }

    /// <inheritdoc />
    public async Task<TenantProvisionOutcome> ProvisionAsync(
        TenantRecord tenant,
        string firstAdminUpn,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant.DisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(firstAdminUpn);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _factory.GetConnectionAsync(ct).ConfigureAwait(false);

        // BEGIN IMMEDIATE: the existence checks below and the inserts that depend on them must not
        // be separated by another writer, or two concurrent provisioning calls could both pass the
        // check. The tenant row and its first-admin row are also written together or not at all -
        // a tenant with no named admin has no one who could ever administer it.
        using var transaction = connection.BeginTransaction(deferred: false);

        if (await ExistsAsync(connection, transaction, "tenant_id", tenant.TenantId, ct).ConfigureAwait(false))
        {
            transaction.Rollback();
            return TenantProvisionOutcome.TenantIdExists;
        }

        var normalizedEntraTenantId = NormalizeEntraTenantId(tenant.EntraTenantId);

        if (normalizedEntraTenantId is { } entra
            && await ExistsAsync(connection, transaction, "entra_tenant_id", entra, ct).ConfigureAwait(false))
        {
            transaction.Rollback();
            return TenantProvisionOutcome.EntraTenantIdClaimed;
        }

        using (var insertTenant = connection.CreateCommand())
        {
            insertTenant.Transaction = transaction;
            insertTenant.CommandText = """
                INSERT INTO tenants (tenant_id, entra_tenant_id, display_name, status, created_at, created_by)
                VALUES ($tenantId, $entraTenantId, $displayName, $status, $createdAt, $createdBy);
                """;
            _ = insertTenant.Parameters.AddWithValue("$tenantId", tenant.TenantId);
            _ = insertTenant.Parameters.AddWithValue(
                "$entraTenantId", (object?)normalizedEntraTenantId ?? DBNull.Value);
            _ = insertTenant.Parameters.AddWithValue("$displayName", tenant.DisplayName);
            _ = insertTenant.Parameters.AddWithValue("$status", FormatStatus(tenant.Status));
            _ = insertTenant.Parameters.AddWithValue(
                "$createdAt", tenant.CreatedAt.ToUnixTimeMilliseconds());
            _ = insertTenant.Parameters.AddWithValue("$createdBy", tenant.CreatedBy);
            _ = await insertTenant.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        using (var insertAdmin = connection.CreateCommand())
        {
            insertAdmin.Transaction = transaction;
            insertAdmin.CommandText = """
                INSERT INTO tenant_admins (tenant_id, upn, user_id, granted_at, granted_by, bound_at)
                VALUES ($tenantId, $upn, NULL, $grantedAt, $grantedBy, NULL);
                """;
            _ = insertAdmin.Parameters.AddWithValue("$tenantId", tenant.TenantId);
            _ = insertAdmin.Parameters.AddWithValue("$upn", Normalize(firstAdminUpn));
            _ = insertAdmin.Parameters.AddWithValue(
                "$grantedAt", tenant.CreatedAt.ToUnixTimeMilliseconds());
            _ = insertAdmin.Parameters.AddWithValue("$grantedBy", tenant.CreatedBy);
            _ = await insertAdmin.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        transaction.Commit();
        return TenantProvisionOutcome.Created;
    }

    private static async Task<bool> ExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string column,
        string value,
        CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        // The column name comes from this file's own two call sites, never from input.
        command.CommandText =
            FormattableString.Invariant($"SELECT COUNT(*) FROM tenants WHERE {column} = $value;");
        _ = command.Parameters.AddWithValue("$value", value);

        var count = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(count, CultureInfo.InvariantCulture) > 0;
    }

    /// <inheritdoc />
    public async Task<bool> TryBindFirstAdminAsync(
        string tenantId,
        string upn,
        string userId,
        DateTimeOffset boundAt,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(upn);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _factory.GetConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();

        // The "user_id IS NULL" predicate is what makes this bind exactly once. Without it a later
        // sign-in - by anyone whose UPN was reassigned to this mailbox - would silently take over
        // the admin row, which is precisely why a mutable UPN is not the durable key.
        command.CommandText = """
            UPDATE tenant_admins
               SET user_id = $userId, bound_at = $boundAt
             WHERE tenant_id = $tenantId AND upn = $upn AND user_id IS NULL;
            """;
        _ = command.Parameters.AddWithValue("$userId", userId);
        _ = command.Parameters.AddWithValue("$boundAt", boundAt.ToUnixTimeMilliseconds());
        _ = command.Parameters.AddWithValue("$tenantId", tenantId);
        _ = command.Parameters.AddWithValue("$upn", Normalize(upn));

        var affected = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<int> NormalizeEntraTenantIdsAsync(CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _factory.GetConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();

        // The NOT EXISTS clause is what keeps this from wedging startup. Without it, two rows that
        // differ only in case would both fold onto one value, ux_tenants_entra would reject the
        // UPDATE, and the host would fail to start with no in-product way out.
        command.CommandText = """
            UPDATE tenants
               SET entra_tenant_id = lower(trim(entra_tenant_id))
             WHERE entra_tenant_id IS NOT NULL
               AND entra_tenant_id <> lower(trim(entra_tenant_id))
               AND NOT EXISTS (
                     SELECT 1 FROM tenants other
                      WHERE other.entra_tenant_id = lower(trim(tenants.entra_tenant_id)));
            """;

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> TryEnsureQuarantineTenantAsync(
        string tenantId,
        DateTimeOffset createdAt,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _factory.GetConnectionAsync(ct).ConfigureAwait(false);

        // BEGIN IMMEDIATE: the "is this id free, or already mine?" read and the insert that depends
        // on it must not be separated by another writer, or two hosts starting at once could both
        // pass the check. That is the concurrent-startup case this whole path exists under.
        using var transaction = connection.BeginTransaction(deferred: false);

        var existing = await ReadQuarantineStateAsync(connection, transaction, tenantId, ct)
            .ConfigureAwait(false);

        if (existing == QuarantineState.OtherTenant)
        {
            // Nothing written. The caller fails with legacy_tenant_id_collision; an operator picks
            // a different id, and no legacy row has been stamped with a real customer's tenant.
            transaction.Rollback();
            return false;
        }

        if (existing == QuarantineState.Absent)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO tenants (tenant_id, entra_tenant_id, display_name, status, created_at, created_by)
                VALUES ($tenantId, NULL, $displayName, $status, $createdAt, 'migration');
                """;
            _ = insert.Parameters.AddWithValue("$tenantId", tenantId);
            _ = insert.Parameters.AddWithValue("$displayName", "Unadopted (pre-identity) data");
            _ = insert.Parameters.AddWithValue("$status", StatusQuarantined);
            _ = insert.Parameters.AddWithValue("$createdAt", createdAt.ToUnixTimeMilliseconds());
            _ = await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        transaction.Commit();
        return true;
    }

    /// <summary>What the configured legacy tenant id currently names.</summary>
    private enum QuarantineState
    {
        /// <summary>No row holds that id, so it is free to create.</summary>
        Absent = 0,

        /// <summary>The row is the quarantine tenant: no Entra directory, status quarantined.</summary>
        Quarantine = 1,

        /// <summary>Some other tenant holds that id. Writing anything here would be the leak.</summary>
        OtherTenant = 2,
    }

    private static async Task<QuarantineState> ReadQuarantineStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT entra_tenant_id, status FROM tenants WHERE tenant_id = $tenantId;";
        _ = command.Parameters.AddWithValue("$tenantId", tenantId);

        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return QuarantineState.Absent;
        }

        var isQuarantine = reader.IsDBNull(0)
            && string.Equals(reader.GetString(1), StatusQuarantined, StringComparison.Ordinal);

        return isQuarantine ? QuarantineState.Quarantine : QuarantineState.OtherTenant;
    }

    /// <inheritdoc />
    public async Task<bool> IsTenantAdminAsync(
        string tenantId,
        string userId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _factory.GetConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM tenant_admins WHERE tenant_id = $tenantId AND user_id = $userId;";
        _ = command.Parameters.AddWithValue("$tenantId", tenantId);
        _ = command.Parameters.AddWithValue("$userId", userId);

        var count = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(count, CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>
    /// Lower-cases a UPN for storage and matching. Invariant rather than current culture: the
    /// stored key must not depend on the server's locale, or a Turkish-locale host would fail to
    /// match a row a neutral-locale host wrote.
    /// </summary>
    private static string Normalize(string upn) => upn.Trim().ToLowerInvariant();

    /// <summary>
    /// Lower-cases and trims an Entra directory id for storage and matching (#347). The value is a
    /// GUID, so an invariant lower-case is lossless; what it buys is that an operator who pastes an
    /// upper-cased id out of a ticket provisions the same row a token carrying the lower-cased one
    /// resolves to, instead of a row nobody can ever sign in against.
    /// </summary>
    /// <param name="entraTenantId">The raw value, from configuration or from a token claim.</param>
    internal static string? NormalizeEntraTenantId(string? entraTenantId) =>
        string.IsNullOrWhiteSpace(entraTenantId) ? null : entraTenantId.Trim().ToLowerInvariant();

    private static string FormatStatus(TenantStatus status) => status switch
    {
        TenantStatus.Suspended => StatusSuspended,
        TenantStatus.Quarantined => StatusQuarantined,
        _ => StatusActive,
    };

    /// <summary>
    /// Reads a status column. Anything that is not exactly <c>active</c> reads as suspended: an
    /// unrecognised status must fail closed, never grant sign-in.
    /// </summary>
    private static TenantStatus ParseStatus(string status) => status switch
    {
        StatusActive => TenantStatus.Active,
        StatusQuarantined => TenantStatus.Quarantined,
        _ => TenantStatus.Suspended,
    };
}
