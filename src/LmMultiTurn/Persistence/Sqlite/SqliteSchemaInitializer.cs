using System.Globalization;
using Microsoft.Data.Sqlite;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.Sqlite;

/// <summary>
/// Handles SQLite schema initialization for conversation persistence.
/// </summary>
public static class SqliteSchemaInitializer
{
    private const string CreateMessagesTableSql = """
        CREATE TABLE IF NOT EXISTS messages (
            id TEXT PRIMARY KEY,
            thread_id TEXT NOT NULL,
            run_id TEXT NOT NULL,
            parent_run_id TEXT,
            generation_id TEXT,
            message_order_idx INTEGER,
            timestamp INTEGER NOT NULL,
            message_type TEXT NOT NULL,
            role TEXT NOT NULL,
            from_agent TEXT,
            message_json TEXT NOT NULL
        );
        """;

    private const string CreateMessagesIndexSql = """
        CREATE INDEX IF NOT EXISTS idx_messages_thread_id
        ON messages (thread_id, timestamp, message_order_idx);
        """;

    private const string CreateMetadataTableSql = """
        CREATE TABLE IF NOT EXISTS thread_metadata (
            thread_id TEXT PRIMARY KEY,
            current_run_id TEXT,
            last_updated INTEGER NOT NULL,
            metadata_json TEXT
        );
        """;

    private const string CreateRunLedgerTableSql = """
        CREATE TABLE IF NOT EXISTS run_ledger (
            run_id TEXT PRIMARY KEY,
            thread_id TEXT NOT NULL,
            status TEXT NOT NULL,
            input_ids TEXT NOT NULL,
            created_at INTEGER NOT NULL,
            updated_at INTEGER NOT NULL
        );
        """;

    private const string CreateRunLedgerIndexSql = """
        CREATE INDEX IF NOT EXISTS idx_run_ledger_thread_id
        ON run_ledger (thread_id, created_at);
        """;

    private const string CreateAcceptedInputsTableSql = """
        CREATE TABLE IF NOT EXISTS accepted_inputs (
            thread_id TEXT NOT NULL,
            input_id TEXT NOT NULL,
            accepted_at INTEGER NOT NULL,
            PRIMARY KEY (thread_id, input_id)
        );
        """;

    // Kept beside run_ledger rather than added to it: the ledger's columns back the status API,
    // and lifecycle observation must be addable without migrating that table.
    private const string CreateRunLifecycleTableSql = """
        CREATE TABLE IF NOT EXISTS run_lifecycle (
            run_id TEXT PRIMARY KEY,
            thread_id TEXT NOT NULL,
            generation_id TEXT NOT NULL,
            parent_run_id TEXT,
            parent_thread_id TEXT,
            spawning_tool_call_id TEXT,
            sub_agent_id TEXT,
            cause_kind TEXT NOT NULL,
            cause_tool_call_id TEXT,
            phase TEXT NOT NULL,
            outcome TEXT,
            turn_count INTEGER NOT NULL,
            started_at INTEGER NOT NULL,
            updated_at INTEGER NOT NULL,
            terminal_at INTEGER
        );
        """;

    private const string CreateRunLifecycleIndexSql = """
        CREATE INDEX IF NOT EXISTS idx_run_lifecycle_thread
        ON run_lifecycle (thread_id, started_at);
        """;

    // A table rather than a JSON column on run_lifecycle, because resolving a deferred call is a
    // conditional single-row update keyed on (thread_id, tool_call_id) — the primary key below —
    // and that is what makes concurrent resolutions of the same call resolve exactly once.
    private const string CreateRunDeferredCallsTableSql = """
        CREATE TABLE IF NOT EXISTS run_deferred_calls (
            thread_id TEXT NOT NULL,
            tool_call_id TEXT NOT NULL,
            run_id TEXT NOT NULL,
            tool_name TEXT NOT NULL,
            generation_id TEXT,
            ordinal INTEGER NOT NULL,
            deferred_at INTEGER NOT NULL,
            resolved_at INTEGER,
            resolution_fingerprint TEXT,
            child_run_id TEXT,
            PRIMARY KEY (thread_id, tool_call_id)
        );
        """;

    private const string CreateRunDeferredCallsIndexSql = """
        CREATE INDEX IF NOT EXISTS idx_run_deferred_calls_run
        ON run_deferred_calls (run_id, ordinal);
        """;

    private const string CreateInputAcceptancesTableSql = """
        CREATE TABLE IF NOT EXISTS input_acceptances (
            thread_id TEXT NOT NULL,
            input_id TEXT NOT NULL,
            accepted_at INTEGER NOT NULL,
            state TEXT NOT NULL,
            spawning_suppressed INTEGER NOT NULL,
            idempotency_honored INTEGER NOT NULL,
            reservation_id TEXT NOT NULL,
            PRIMARY KEY (thread_id, input_id)
        );
        """;

    private const string CreateNotifyWaitsTableSql = """
        CREATE TABLE IF NOT EXISTS notify_waits (
            wait_id TEXT NOT NULL,
            thread_id TEXT NOT NULL,
            kind TEXT NOT NULL,
            args TEXT NOT NULL,
            label TEXT NULL,
            max_fires INTEGER NULL,
            fires_so_far INTEGER NOT NULL DEFAULT 0,
            timeout_at INTEGER NOT NULL,
            armed_at INTEGER NOT NULL,
            status TEXT NOT NULL,
            PRIMARY KEY (thread_id, wait_id)
        );
        """;

    private const string CreateNotifyWaitsIndexSql =
        "CREATE INDEX IF NOT EXISTS ix_notify_waits_thread ON notify_waits (thread_id);";

    // migration step 2 - the tenant registry (P1 spec 8.2). entra_tenant_id is nullable for
    // exactly one reason: the legacy tenant of slice 2 predates Entra and has no directory behind
    // it. Every PROVISIONED tenant has one, and the partial unique index below is what makes it
    // structurally impossible for two tenants to claim the same Entra directory - a cross-tenant
    // identity collision is refused by the schema, not by the store that writes it.
    private const string CreateTenantsTableSql = """
        CREATE TABLE IF NOT EXISTS tenants (
            tenant_id       TEXT PRIMARY KEY,
            entra_tenant_id TEXT,
            display_name    TEXT NOT NULL,
            status          TEXT NOT NULL,
            created_at      INTEGER NOT NULL,
            created_by      TEXT NOT NULL
        );
        """;

    private const string CreateTenantsEntraIndexSql = """
        CREATE UNIQUE INDEX IF NOT EXISTS ux_tenants_entra
        ON tenants (entra_tenant_id) WHERE entra_tenant_id IS NOT NULL;
        """;

    // Keyed by UPN rather than by user id because the first admin must be NAMED before they have
    // ever signed in, so their oid is not yet knowable. user_id is bound from '{tid}:{oid}' on
    // that user's first successful sign-in; after that the UPN is never consulted again, because
    // a UPN is mutable and cannot be a durable key.
    private const string CreateTenantAdminsTableSql = """
        CREATE TABLE IF NOT EXISTS tenant_admins (
            tenant_id  TEXT NOT NULL,
            upn        TEXT NOT NULL,
            user_id    TEXT,
            granted_at INTEGER NOT NULL,
            granted_by TEXT NOT NULL,
            bound_at   INTEGER,
            PRIMARY KEY (tenant_id, upn)
        );
        """;

    private const string CreateTenantAdminsUserIndexSql = """
        CREATE INDEX IF NOT EXISTS ix_tenant_admins_user ON tenant_admins (user_id);
        """;

    // migration step 3 - ownership on thread_metadata (P1 spec 8.3). Nullable, because SQLite
    // cannot add a NOT NULL column without a default AND because null is the signal for "legacy,
    // unclaimed" (8.5). visibility is STORED rather than derived from the presence of a grant row:
    // deriving it would make the policy issue a second query before it could even build a
    // ResourceDescriptor, and would make Private and Shared indistinguishable the moment a grant
    // expired rather than being revoked. A null reads as Private.
    //
    // DEVIATION from spec 8.5.1, deliberate and explained in the PR body: this step is DDL only.
    // The quarantine tenant row and the null-tenant backfill are NOT here. 8.5.4 already requires
    // both to run on every startup rather than once at version 3, and the tenant registry is not
    // guaranteed to live in this database file - the sample's conversations are a FILE store, so
    // there is no file in which a `tenants` row and a `thread_metadata` row are ever siblings.
    // A copy of the guard here would read whichever (usually empty) `tenants` table happens to sit
    // in this file, which is exactly the check that must not be vacuous.
    private static readonly string[] AddThreadMetadataOwnerColumnsSql =
    [
        "ALTER TABLE thread_metadata ADD COLUMN tenant_id     TEXT;",
        "ALTER TABLE thread_metadata ADD COLUMN owner_user_id TEXT;",
        "ALTER TABLE thread_metadata ADD COLUMN owner_app_id  TEXT;",
        "ALTER TABLE thread_metadata ADD COLUMN visibility    TEXT;",
    ];

    // Exactly matches the WHERE and ORDER BY of the listing filter in spec 7.5.
    private const string CreateThreadMetadataOwnerIndexSql = """
        CREATE INDEX IF NOT EXISTS idx_thread_metadata_owner
        ON thread_metadata (tenant_id, owner_user_id, last_updated DESC);
        """;

    // migration step 4 - named sharing (P1 spec 8.4). One table serves conversations, workspaces
    // and modes so the three resource slices share the sharing surface, the policy code and the
    // audit shape. The role vocabulary is closed by a CHECK rather than only by the policy: grants
    // never confer delete, share or publish, and there is no role value that maps to those three,
    // by construction - so a bad grant cannot sit in the table waiting for a policy bug to honour
    // it.
    private const string CreateResourceGrantsTableSql = """
        CREATE TABLE IF NOT EXISTS resource_grants (
            tenant_id     TEXT NOT NULL,
            resource_type TEXT NOT NULL,
            resource_id   TEXT NOT NULL,
            subject_id    TEXT NOT NULL,
            role          TEXT NOT NULL CHECK (role IN ('viewer','editor')),
            granted_by    TEXT NOT NULL,
            granted_at    INTEGER NOT NULL,
            expires_at    INTEGER,
            PRIMARY KEY (tenant_id, resource_type, resource_id, subject_id)
        );
        """;

    private const string CreateResourceGrantsSubjectIndexSql = """
        CREATE INDEX IF NOT EXISTS idx_resource_grants_subject
        ON resource_grants (tenant_id, subject_id, resource_type);
        """;

    /// <summary>One ordered migration step, applied atomically with its version bump.</summary>
    /// <param name="Version">
    /// The <c>PRAGMA user_version</c> the database holds after this step commits. This array is
    /// the single source of truth for those numbers.
    /// </param>
    /// <param name="Statements">The step's statements, in dependency order.</param>
    private sealed record MigrationStep(int Version, string[] Statements);

    /// <summary>
    /// The ordered migration steps. Step 1 is the original <c>CREATE TABLE IF NOT EXISTS</c> block,
    /// so a database created by an earlier build - which never wrote <c>user_version</c> and so
    /// reads 0 - is brought to 1 by re-running statements that are all no-ops for it, then carries
    /// on into the later steps. The runner never branches on "new versus existing".
    /// </summary>
    private static readonly MigrationStep[] Migrations =
    [
        new(
            1,
            [
                CreateMessagesTableSql,
                CreateMessagesIndexSql,
                CreateMetadataTableSql,
                CreateRunLedgerTableSql,
                CreateRunLedgerIndexSql,
                CreateAcceptedInputsTableSql,
                CreateInputAcceptancesTableSql,
                CreateRunLifecycleTableSql,
                CreateRunLifecycleIndexSql,
                CreateRunDeferredCallsTableSql,
                CreateRunDeferredCallsIndexSql,
                CreateNotifyWaitsTableSql,
                CreateNotifyWaitsIndexSql,
            ]),
        new(
            2,
            [
                CreateTenantsTableSql,
                CreateTenantsEntraIndexSql,
                CreateTenantAdminsTableSql,
                CreateTenantAdminsUserIndexSql,
            ]),
        new(
            3,
            [
                .. AddThreadMetadataOwnerColumnsSql,
                CreateThreadMetadataOwnerIndexSql,
            ]),
        new(
            4,
            [
                CreateResourceGrantsTableSql,
                CreateResourceGrantsSubjectIndexSql,
            ]),
    ];

    /// <summary>
    /// The <c>PRAGMA user_version</c> a fully migrated database holds. Exposed so callers - and
    /// tests - can assert the version without duplicating the step table.
    /// </summary>
    public static int LatestSchemaVersion => Migrations[^1].Version;

    /// <summary>
    /// Brings the database up to <see cref="LatestSchemaVersion"/>, applying only the steps it has
    /// not already taken.
    /// </summary>
    /// <remarks>
    /// ONE transaction spans the whole ladder, not one per step: it is opened before the loop and
    /// committed after the last step, so every outstanding step and every <c>user_version</c> bump
    /// commits together or none of them does. A run that fails partway therefore leaves the file at
    /// the version it started at, and is retried from there in full - there is no intermediate
    /// version a reader can observe. The version is re-read INSIDE that transaction, which is taken
    /// with <c>BEGIN IMMEDIATE</c>: two processes opening the same file concurrently would otherwise
    /// both read the old version and both apply the step, which is harmless for a
    /// <c>CREATE ... IF NOT EXISTS</c> and is not harmless for the <c>ALTER TABLE</c> and
    /// data-backfill steps that follow in later slices.
    /// </remarks>
    /// <param name="connection">An open SQLite connection.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task InitializeSchemaAsync(
        SqliteConnection connection,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Fast path: an already-migrated database is the common case (every store instance calls
        // this on first use), and it should not take the write lock. The read is advisory only -
        // the authoritative one happens under the lock below.
        var advisory = await ReadUserVersionAsync(connection, transaction: null, ct).ConfigureAwait(false);
        ThrowIfNewerThanThisBuild(advisory);

        if (advisory == LatestSchemaVersion)
        {
            return;
        }

        using var transaction = connection.BeginTransaction(deferred: false);

        try
        {
            var current = await ReadUserVersionAsync(connection, transaction, ct).ConfigureAwait(false);

            // Re-checked under the lock, not only on the advisory read. Between the two reads
            // another process holding a newer build can migrate the file forward; the advisory
            // check would have seen the old version and waved it through.
            ThrowIfNewerThanThisBuild(current);

            foreach (var step in Migrations)
            {
                if (step.Version <= current)
                {
                    continue;
                }

                foreach (var sql in step.Statements)
                {
                    await ExecuteAsync(connection, transaction, sql, ct).ConfigureAwait(false);
                }

                // Bumped inside the same transaction as the step's statements. PRAGMA user_version
                // takes no parameter, so the value is interpolated; it comes from the private step
                // table above and is an int, so there is no injection surface.
                await ExecuteAsync(
                        connection,
                        transaction,
                        FormattableString.Invariant($"PRAGMA user_version = {step.Version};"),
                        ct)
                    .ConfigureAwait(false);
            }

            transaction.Commit();
        }
        catch
        {
            try
            {
                transaction.Rollback();
            }
            catch
            {
                // Swallow: a rollback failure (e.g. the connection is already broken) must
                // never replace the original schema-initialization exception being propagated
                // below - that exception is the one callers need to diagnose the real failure.
            }

            throw;
        }
    }

    /// <summary>
    /// Refuses a database some newer build already migrated, rather than opening it optimistically.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The check used to be <c>&gt;= LatestSchemaVersion</c>, which cannot tell "already current"
    /// apart from "ahead of me". A deployment rolled back to an older binary would then run every
    /// query in this assembly against a schema it has no model for - reading tables whose columns
    /// have moved and writing rows the newer build will later read back.
    /// </para>
    /// <para>
    /// There is no safe alternative to refusing. This build cannot migrate downward, and it cannot
    /// know what the newer version changed. Failing to open is loud, immediate and reversible by
    /// redeploying the newer binary; proceeding is silent and corrupts as it goes. The same rule
    /// already governs workflow snapshots elsewhere in this repository.
    /// </para>
    /// </remarks>
    private static void ThrowIfNewerThanThisBuild(int version)
    {
        if (version > LatestSchemaVersion)
        {
            var detail = FormattableString.Invariant(
                $"The database is at schema version {version}, which is newer than the version {LatestSchemaVersion} this build understands.");

            throw new NotSupportedException(
                detail
                    + " It was migrated by a later build and cannot be migrated back. Deploy that"
                    + " build again, or point this process at a different database.");
        }
    }

    private static async Task<int> ReadUserVersionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        command.Transaction = transaction;
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Initializes the database schema using a connection factory.
    /// </summary>
    /// <param name="connectionFactory">The connection factory.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task InitializeSchemaAsync(
        ISqliteConnectionFactory connectionFactory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);

        await using var connection = await connectionFactory.GetConnectionAsync(ct)
            .ConfigureAwait(false);
        await InitializeSchemaAsync(connection, ct).ConfigureAwait(false);
    }
}
