using System.Collections.Immutable;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Identity;
using Microsoft.Data.Sqlite;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.Sqlite;

/// <summary>
/// SQLite implementation of conversation, run-ledger, lifecycle, and durable input-admission persistence.
/// Uses a factory pattern for connection pooling and lazy schema initialization.
/// </summary>
public sealed class SqliteConversationStore
    : IConversationStore,
        IConversationOwnershipStore,
        IRunLedgerStore,
        IRunLifecycleStore,
        IInputAcceptanceStore,
        IAsyncDisposable
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly bool _ownsFactory;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private readonly SemaphoreSlim _metadataWriteLock = new(1, 1);
    private bool _schemaInitialized;
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <summary>
    /// Creates a SqliteConversationStore with the specified connection factory.
    /// </summary>
    /// <param name="connectionFactory">The connection factory to use.</param>
    /// <param name="ownsFactory">If true, the store will dispose the factory when disposed.</param>
    public SqliteConversationStore(ISqliteConnectionFactory connectionFactory, bool ownsFactory = false)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
        _ownsFactory = ownsFactory;
    }

    /// <summary>
    /// Creates a SqliteConversationStore with a new connection factory for the specified database path.
    /// </summary>
    /// <param name="databasePath">Path to the SQLite database file.</param>
    /// <param name="maxConnections">Maximum number of concurrent connections.</param>
    public SqliteConversationStore(string databasePath, int maxConnections = 5)
        : this(new SqliteConnectionFactory(databasePath, maxConnections), ownsFactory: true) { }

    /// <inheritdoc />
    public async Task AppendMessagesAsync(
        string threadId,
        IReadOnlyList<PersistedMessage> messages,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(messages);

        if (messages.Count == 0)
        {
            return;
        }

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.GetConnectionAsync(ct).ConfigureAwait(false);

        // BEGIN IMMEDIATE, not deferred: the sequence is read (MAX) and then extended (INSERT) inside
        // this transaction, and two processes appending to the same thread must serialize on the
        // write lock from the READ onward or both would compute the same next Seq.
        using var transaction = connection.BeginTransaction(deferred: false);

        try
        {
            var next = await BackfillLegacySeqAsync(connection, transaction, threadId, ct).ConfigureAwait(false);

            foreach (var message in MessageSequence.BatchOrder(messages))
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO messages (
                        id, thread_id, run_id, parent_run_id, generation_id,
                        message_order_idx, timestamp, message_type, role, from_agent, message_json, seq
                    ) VALUES (
                        $id, $thread_id, $run_id, $parent_run_id, $generation_id,
                        $message_order_idx, $timestamp, $message_type, $role, $from_agent, $message_json, $seq
                    );
                    """;

                // The store owns Seq; whatever the caller put on the row is ignored.
                _ = command.Parameters.AddWithValue("$seq", ++next);
                _ = command.Parameters.AddWithValue("$id", message.Id);
                _ = command.Parameters.AddWithValue("$thread_id", message.ThreadId);
                _ = command.Parameters.AddWithValue("$run_id", message.RunId);
                _ = command.Parameters.AddWithValue("$parent_run_id", (object?)message.ParentRunId ?? DBNull.Value);
                _ = command.Parameters.AddWithValue("$generation_id", (object?)message.GenerationId ?? DBNull.Value);
                _ = command.Parameters.AddWithValue(
                    "$message_order_idx",
                    (object?)message.MessageOrderIdx ?? DBNull.Value
                );
                _ = command.Parameters.AddWithValue("$timestamp", message.Timestamp);
                _ = command.Parameters.AddWithValue("$message_type", message.MessageType);
                _ = command.Parameters.AddWithValue("$role", message.Role);
                _ = command.Parameters.AddWithValue("$from_agent", (object?)message.FromAgent ?? DBNull.Value);
                _ = command.Parameters.AddWithValue("$message_json", message.MessageJson);

                _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Numbers every row of <paramref name="threadId"/> that predates the <c>seq</c> column, in
    /// <c>(timestamp, message_order_idx, rowid)</c> order after the highest existing Seq, and returns
    /// the thread's watermark afterwards. Idempotent: a thread with no null rows is one
    /// <c>MAX(seq)</c> read. Runs inside the caller's write transaction so the backfill and the
    /// append that triggered it commit together.
    /// </summary>
    private static async Task<long> BackfillLegacySeqAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string threadId,
        CancellationToken ct
    )
    {
        long watermark;
        using (var max = connection.CreateCommand())
        {
            max.Transaction = transaction;
            max.CommandText = "SELECT COALESCE(MAX(seq), 0) FROM messages WHERE thread_id = $thread_id;";
            _ = max.Parameters.AddWithValue("$thread_id", threadId);
            watermark = Convert.ToInt64(
                await max.ExecuteScalarAsync(ct).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture
            );
        }

        var legacyIds = new List<string>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT id FROM messages
                WHERE thread_id = $thread_id AND seq IS NULL
                ORDER BY timestamp ASC, message_order_idx ASC, rowid ASC;
                """;
            _ = select.Parameters.AddWithValue("$thread_id", threadId);
            await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                legacyIds.Add(reader.GetString(0));
            }
        }

        foreach (var id in legacyIds)
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE messages SET seq = $seq WHERE id = $id AND thread_id = $thread_id;";
            _ = update.Parameters.AddWithValue("$seq", ++watermark);
            _ = update.Parameters.AddWithValue("$id", id);
            _ = update.Parameters.AddWithValue("$thread_id", threadId);
            _ = await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return watermark;
    }

    /// <inheritdoc />
    public async Task<long> GetMessageWatermarkAsync(string threadId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.GetConnectionAsync(ct).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(seq), 0) FROM messages WHERE thread_id = $thread_id;";
        _ = command.Parameters.AddWithValue("$thread_id", threadId);

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(ct).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture
        );
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PersistedMessage>> LoadMessageRangeAsync(
        string threadId,
        long fromSeq,
        long toSeq,
        int limit,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(threadId);

        if (limit <= 0 || toSeq < fromSeq)
        {
            return [];
        }

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.GetConnectionAsync(ct).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            {MessageSelectSql}
            WHERE thread_id = $thread_id AND seq >= $from_seq AND seq <= $to_seq
            ORDER BY seq ASC
            LIMIT $limit;
            """;
        _ = command.Parameters.AddWithValue("$thread_id", threadId);
        _ = command.Parameters.AddWithValue("$from_seq", fromSeq);
        _ = command.Parameters.AddWithValue("$to_seq", toSeq);
        _ = command.Parameters.AddWithValue("$limit", limit);

        var messages = new List<PersistedMessage>();

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            messages.Add(ReadMessage(reader));
        }

        return messages;
    }

    /// <inheritdoc />
    public async Task ReplaceMessageAsync(string threadId, PersistedMessage replacement, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(replacement);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.GetConnectionAsync(ct).ConfigureAwait(false);

        // Preserve the existing timestamp and seq (neither is in the SET list) so load ordering stays
        // stable when a deferred placeholder is later resolved: a replacement is a mutation in place,
        // not an append, and must not move the watermark.
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE messages SET
                run_id = $run_id,
                parent_run_id = $parent_run_id,
                generation_id = $generation_id,
                message_order_idx = $message_order_idx,
                message_type = $message_type,
                role = $role,
                from_agent = $from_agent,
                message_json = $message_json
            WHERE id = $id AND thread_id = $thread_id;
            """;

        _ = command.Parameters.AddWithValue("$id", replacement.Id);
        _ = command.Parameters.AddWithValue("$thread_id", threadId);
        _ = command.Parameters.AddWithValue("$run_id", replacement.RunId);
        _ = command.Parameters.AddWithValue("$parent_run_id", (object?)replacement.ParentRunId ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$generation_id", (object?)replacement.GenerationId ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$message_order_idx", (object?)replacement.MessageOrderIdx ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$message_type", replacement.MessageType);
        _ = command.Parameters.AddWithValue("$role", replacement.Role);
        _ = command.Parameters.AddWithValue("$from_agent", (object?)replacement.FromAgent ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$message_json", replacement.MessageJson);

        var rows = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (rows == 0)
        {
            throw new InvalidOperationException($"Message '{replacement.Id}' not found in thread '{threadId}'.");
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PersistedMessage>> LoadMessagesAsync(
        string threadId,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(threadId);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.GetConnectionAsync(ct).ConfigureAwait(false);

        // Append order: sequenced rows first by seq, then any legacy rows (seq IS NULL) by the
        // (timestamp, idx) order the backfill will later assign them - see MessageSequence.Order.
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            {MessageSelectSql}
            WHERE thread_id = $thread_id
            ORDER BY (seq IS NULL) ASC, seq ASC, timestamp ASC, message_order_idx ASC;
            """;
        _ = command.Parameters.AddWithValue("$thread_id", threadId);

        var messages = new List<PersistedMessage>();

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            messages.Add(ReadMessage(reader));
        }

        return messages;
    }

    /// <inheritdoc />
    public async Task SaveMetadataAsync(string threadId, ThreadMetadata metadata, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(metadata);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.GetConnectionAsync(ct).ConfigureAwait(false);

        // Serialize extensible fields to JSON
        var metadataJson = SerializeMetadataExtensions(metadata);

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO thread_metadata (
                thread_id, current_run_id, last_updated, metadata_json,
                tenant_id, owner_user_id, owner_app_id, visibility)
            VALUES (
                $thread_id, $current_run_id, $last_updated, $metadata_json,
                $tenant_id, $owner_user_id, $owner_app_id, $visibility)
            ON CONFLICT(thread_id) DO UPDATE SET
                current_run_id = excluded.current_run_id,
                last_updated = excluded.last_updated,
                metadata_json = excluded.metadata_json,
                tenant_id = excluded.tenant_id,
                owner_user_id = excluded.owner_user_id,
                owner_app_id = excluded.owner_app_id,
                visibility = excluded.visibility;
            """;

        _ = command.Parameters.AddWithValue("$thread_id", threadId);
        _ = command.Parameters.AddWithValue("$current_run_id", (object?)metadata.CurrentRunId ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$last_updated", metadata.LastUpdated);
        _ = command.Parameters.AddWithValue("$metadata_json", (object?)metadataJson ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$tenant_id", (object?)metadata.TenantId ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$owner_user_id", (object?)metadata.OwnerUserId ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$owner_app_id", (object?)metadata.OwnerAppId ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$visibility", (object?)metadata.Visibility?.ToString() ?? DBNull.Value);

        _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ThreadMetadata?> LoadMetadataAsync(string threadId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.GetConnectionAsync(ct).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT thread_id, current_run_id, last_updated, metadata_json,
                   tenant_id, owner_user_id, owner_app_id, visibility
            FROM thread_metadata
            WHERE thread_id = $thread_id;
            """;
        _ = command.Parameters.AddWithValue("$thread_id", threadId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return !await reader.ReadAsync(ct).ConfigureAwait(false) ? null : ReadMetadata(reader);
    }

    /// <inheritdoc />
    public async Task UpdateMetadataAsync(
        string threadId,
        Func<ThreadMetadata?, ThreadMetadata> update,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(update);

        // Serialize the read-modify-write so concurrent property-bag updates for the same thread cannot
        // clobber each other (matches the other stores' atomic UpdateMetadataAsync).
        await _metadataWriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = await LoadMetadataAsync(threadId, ct).ConfigureAwait(false);
            var updated = update(existing);
            await SaveMetadataAsync(threadId, updated, ct).ConfigureAwait(false);
        }
        finally
        {
            _ = _metadataWriteLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task DeleteThreadAsync(string threadId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.GetConnectionAsync(ct).ConfigureAwait(false);

        using var transaction = connection.BeginTransaction();

        try
        {
            using var deleteMessagesCmd = connection.CreateCommand();
            deleteMessagesCmd.Transaction = transaction;
            deleteMessagesCmd.CommandText = "DELETE FROM messages WHERE thread_id = $thread_id;";
            _ = deleteMessagesCmd.Parameters.AddWithValue("$thread_id", threadId);
            _ = await deleteMessagesCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            using var deleteMetadataCmd = connection.CreateCommand();
            deleteMetadataCmd.Transaction = transaction;
            deleteMetadataCmd.CommandText = "DELETE FROM thread_metadata WHERE thread_id = $thread_id;";
            _ = deleteMetadataCmd.Parameters.AddWithValue("$thread_id", threadId);
            _ = await deleteMetadataCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
        int limit = 50,
        int offset = 0,
        ConversationListOptions? options = null,
        CancellationToken ct = default
    )
    {
        var listOptions = options ?? ConversationListOptions.Default;
        RefuseUnsupportedSortOrder(listOptions);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.GetConnectionAsync(ct).ConfigureAwait(false);

        using var command = connection.CreateCommand();

        // The exclusion is a WHERE clause, not a post-pass over the returned rows: LIMIT/OFFSET is
        // applied by this one statement to the fully filtered set, which is the whole point. See
        // ConversationListOptions for the production failure that a post-pass produced.
        var exclusionClause = BuildPrefixExclusionClause(command, listOptions, "thread_id");

        command.CommandText = FormattableString.Invariant(
            $"""
            SELECT thread_id, current_run_id, last_updated, metadata_json,
                   tenant_id, owner_user_id, owner_app_id, visibility
            FROM thread_metadata
            WHERE {exclusionClause}
            -- thread_id breaks ties so LIMIT/OFFSET pages a total order. Without it two rows
            -- sharing a last_updated are ordered by whatever SQLite returns, which may differ
            -- between the page-1 and page-2 statements: one row comes back twice and another
            -- never comes back at all. Must match ConversationListOptions.Order.
            ORDER BY last_updated DESC, thread_id DESC
            LIMIT $limit OFFSET $offset;
            """
        );
        _ = command.Parameters.AddWithValue("$limit", limit);
        _ = command.Parameters.AddWithValue("$offset", offset);

        var metadataList = new List<ThreadMetadata>();

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            metadataList.Add(ReadMetadata(reader));
        }

        return metadataList;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
        ConversationListScope scope,
        int limit = 50,
        int offset = 0,
        ConversationListOptions? options = null,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(scope);

        var listOptions = options ?? ConversationListOptions.Default;
        RefuseUnsupportedSortOrder(listOptions);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.GetConnectionAsync(ct).ConfigureAwait(false);

        // The grant branch is a bound id list rather than an EXISTS over resource_grants: that
        // table is not guaranteed to live in this database file. See ConversationListScope.
        //
        // The list arrives as ONE json parameter rather than one parameter per id. Binding per id
        // walked straight into SQLITE_MAX_VARIABLE_NUMBER, so a principal with enough individual
        // grants got a query failure instead of a listing, and got it at exactly the moment heavy
        // sharing made the listing matter most. The limit is 32,766 on the bundled engine (999 on
        // builds before SQLite 3.32); neither number is worth relying on, which is why the shape
        // changed rather than the batch size. Chunking the IN
        // list was the other option and is worse here: LIMIT/OFFSET is applied by this one
        // statement to the fully filtered, fully ordered set, so splitting it would mean paging in
        // memory over a merged result - the short-page bug this whole design exists to avoid.
        const string grantClause = "($userId IS NOT NULL AND t.thread_id IN (SELECT value FROM json_each($grants)))";

        using var command = connection.CreateCommand();

        // The presentation exclusion is ANDed over the whole authorization disjunction, not folded
        // into one of its branches - it narrows what this surface displays regardless of WHICH
        // branch admitted the row. It is also a WHERE clause rather than a post-pass, so LIMIT/OFFSET
        // still applies to the fully filtered set. See ConversationListOptions.
        var exclusionClause = BuildPrefixExclusionClause(command, listOptions, "t.thread_id");

        // The `@userId IS NOT NULL` guards are the SQL spelling of spec 7.4 step 3: without them an
        // app-only principal would fall through into the grant branch with a NULL subject. They do
        // NOT protect against a null OWNER - SQL already handles that, because NULL = $userId
        // evaluates to NULL and never satisfies the WHERE.
        command.CommandText = FormattableString.Invariant(
            $"""
            SELECT thread_id, current_run_id, last_updated, metadata_json,
                   tenant_id, owner_user_id, owner_app_id, visibility
            FROM thread_metadata t
            WHERE ( ( t.tenant_id = $tenantId
                      AND ( $isTenantAdmin = 1
                            OR ($userId IS NOT NULL AND t.owner_user_id = $userId)
                            OR ($userId IS NULL AND $appId IS NOT NULL AND t.owner_app_id = $appId)
                            OR ($userId IS NOT NULL AND t.visibility = $tenantPublished)
                            OR {grantClause} ) )
                    OR ( $includeUntenanted = 1 AND t.tenant_id IS NULL ) )
              AND {exclusionClause}
            -- Same total order as the unscoped overload above, and as
            -- ConversationListOptions.Order: a scoped listing must not page differently.
            ORDER BY t.last_updated DESC, t.thread_id DESC
            LIMIT $limit OFFSET $offset;
            """
        );

        _ = command.Parameters.AddWithValue("$tenantId", scope.TenantId);
        _ = command.Parameters.AddWithValue("$includeUntenanted", scope.IncludeUntenanted ? 1 : 0);
        _ = command.Parameters.AddWithValue("$userId", (object?)scope.UserId ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$appId", (object?)scope.AppId ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$isTenantAdmin", scope.IsTenantAdmin ? 1 : 0);
        _ = command.Parameters.AddWithValue("$tenantPublished", Visibility.TenantPublished.ToString());
        _ = command.Parameters.AddWithValue("$limit", limit);
        _ = command.Parameters.AddWithValue("$offset", offset);

        // An app-only principal never consults grants (7.4 step 3), so it binds the empty array
        // rather than its own set - json_each over "[]" yields no rows, which is the same answer
        // the old "0" literal gave and one fewer branch to keep in step with the SQL.
        _ = command.Parameters.AddWithValue(
            "$grants",
            JsonSerializer.Serialize(
                scope.UserId is null ? [] : (IEnumerable<string>)scope.GrantedThreadIds,
                JsonOptions
            )
        );

        var metadataList = new List<ThreadMetadata>();

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            metadataList.Add(ReadMetadata(reader));
        }

        return metadataList;
    }

    /// <summary>
    /// Refuses a sort order this store cannot answer, loudly, at the call site.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>thread_metadata</c> has no <c>created_at</c> column - the record itself has no creation
    /// field either, which is why <see cref="ConversationListOptions.CreationTimestampOf"/> derives
    /// one from the thread id. That derivation is not reproducible in SQL without a fragile
    /// <c>substr</c>/<c>CAST</c> expression that would have to re-implement, in a second language,
    /// the "prefix, then a delimiter, then digits, else fall back to last_updated" rule - and would
    /// then be free to diverge from it silently, on exactly the deployments large enough that nobody
    /// checks the order by hand.
    /// </para>
    /// <para>
    /// So this throws instead of approximating, following the precedent the scoped listing overload
    /// already set with its throwing default implementation: a store that cannot answer a listing
    /// must say so where it is called, rather than answer something plausible. Adding a real
    /// <c>created_at</c> column, backfilled from the id, is the fix that makes this path work; until
    /// then the exception names both the missing column and the helper any implementation must agree
    /// with. This is not reachable from the conversation sidebar today - the sample registers
    /// <see cref="FileConversationStore"/> - but it is documented rather than hidden, because a
    /// deployment that swaps in SQLite would otherwise discover it as a silently different ordering.
    /// </para>
    /// </remarks>
    /// <param name="options">The resolved (never null) listing options.</param>
    /// <exception cref="NotSupportedException">
    /// The requested sort order has no faithful SQL expression here.
    /// </exception>
    private static void RefuseUnsupportedSortOrder(ConversationListOptions options)
    {
        if (options.SortOrder == ConversationSortOrder.LastUsed)
        {
            return;
        }

        throw new NotSupportedException(
            $"{nameof(SqliteConversationStore)} cannot order a listing by "
                + $"{nameof(ConversationSortOrder)}.{options.SortOrder}: the thread_metadata table "
                + "has no created_at column, and deriving one in SQL would be a second, silently "
                + "divergent copy of "
                + $"{nameof(ConversationListOptions)}.{nameof(ConversationListOptions.CreationTimestampOf)}. "
                + "Add a backfilled created_at column before enabling this ordering on SQLite."
        );
    }

    /// <summary>
    /// The SQL <c>WHERE</c> conjunct that removes every excluded thread-id prefix, with each prefix
    /// bound as a parameter. Returns the literal <c>1 = 1</c> when nothing is excluded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The comparison is <c>substr(id, 1, length($p)) &lt;&gt; $p</c> rather than
    /// <c>id NOT LIKE $p || '%'</c> on purpose. <c>LIKE</c> would treat <c>%</c> and <c>_</c> inside
    /// a caller-supplied prefix as wildcards, so a prefix containing either would silently exclude
    /// more rows than it names, and the <c>ESCAPE</c> clause needed to prevent that means escaping
    /// the prefix in C# - a step that is easy to omit and impossible to notice, because the query
    /// still succeeds and just returns the wrong set. <c>substr</c> needs no escaping at all and is
    /// a byte-for-byte prefix comparison, which is the same <see cref="StringComparison.Ordinal"/>
    /// test <see cref="ConversationListOptions.Admits"/> performs in memory.
    /// </para>
    /// <para>
    /// An empty prefix is skipped, matching <see cref="ConversationListOptions.Admits"/>: SQL would
    /// read it as "exclude everything" and hand back an empty listing, which is the worst available
    /// reading of a blank configuration entry.
    /// </para>
    /// </remarks>
    /// <param name="command">The command the prefix parameters are bound to.</param>
    /// <param name="options">The resolved (never null) listing options.</param>
    /// <param name="threadIdColumn">
    /// How the thread-id column is spelled in this statement (aliased or not).
    /// </param>
    private static string BuildPrefixExclusionClause(
        SqliteCommand command,
        ConversationListOptions options,
        string threadIdColumn
    )
    {
        var conjuncts = new List<string>();
        var index = 0;

        foreach (var prefix in options.ExcludedThreadIdPrefixes)
        {
            if (string.IsNullOrEmpty(prefix))
            {
                continue;
            }

            var parameterName = FormattableString.Invariant($"$excludedPrefix{index}");
            index++;

            _ = command.Parameters.AddWithValue(parameterName, prefix);
            conjuncts.Add(
                FormattableString.Invariant($"substr({threadIdColumn}, 1, length({parameterName})) <> {parameterName}")
            );
        }

        // "1 = 1" rather than an empty string so the caller can interpolate this unconditionally and
        // the statement stays syntactically valid with nothing excluded.
        return conjuncts.Count == 0 ? "1 = 1" : string.Join(" AND ", conjuncts);
    }

    /// <inheritdoc />
    public async Task<int> StampUnownedThreadsAsync(string quarantineTenantId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(quarantineTenantId);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.GetConnectionAsync(ct).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE thread_metadata SET tenant_id = $tenantId WHERE tenant_id IS NULL;";
        _ = command.Parameters.AddWithValue("$tenantId", quarantineTenantId);

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListThreadIdsByTenantAsync(
        string tenantId,
        IReadOnlyCollection<string>? threadIds,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.GetConnectionAsync(ct).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        var restriction = BuildThreadIdRestriction(command, threadIds);
        command.CommandText = FormattableString.Invariant(
            $"SELECT thread_id FROM thread_metadata WHERE tenant_id = $tenantId{restriction} ORDER BY thread_id;"
        );
        _ = command.Parameters.AddWithValue("$tenantId", tenantId);

        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    /// <inheritdoc />
    public async Task<int> AdoptThreadsAsync(
        string fromTenantId,
        string toTenantId,
        string? ownerUserId,
        IReadOnlyCollection<string>? threadIds,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromTenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toTenantId);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.GetConnectionAsync(ct).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        var restriction = BuildThreadIdRestriction(command, threadIds);

        // Selecting on the SOURCE tenant is what makes a repeated call idempotent: a row already
        // adopted into a real tenant no longer matches, so it is never re-stamped. COALESCE keeps
        // an owner already assigned when the second call names none.
        command.CommandText = FormattableString.Invariant(
            $"""
            UPDATE thread_metadata
               SET tenant_id = $toTenantId,
                   owner_user_id = COALESCE($ownerUserId, owner_user_id)
             WHERE tenant_id = $fromTenantId{restriction};
            """
        );
        _ = command.Parameters.AddWithValue("$fromTenantId", fromTenantId);
        _ = command.Parameters.AddWithValue("$toTenantId", toTenantId);
        _ = command.Parameters.AddWithValue("$ownerUserId", (object?)ownerUserId ?? DBNull.Value);

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the optional <c>AND thread_id IN (...)</c> restriction and binds its parameters.
    /// Returns an empty string when the caller named no ids, which means "every eligible row".
    /// </summary>
    private static string BuildThreadIdRestriction(SqliteCommand command, IReadOnlyCollection<string>? threadIds)
    {
        if (threadIds is null)
        {
            return string.Empty;
        }

        if (threadIds.Count == 0)
        {
            // An explicitly EMPTY list means "these zero resources", not "all of them". Returning
            // the unrestricted form here would turn a caller's empty selection into a full adoption.
            return " AND 0";
        }

        // ONE json parameter, not one per id. An adoption naming a large thread set - which a
        // legacy-tenant migration, the exact operation this helper exists for, routinely does -
        // would otherwise exceed SQLITE_MAX_VARIABLE_NUMBER and fail the whole batch rather than
        // adopt fewer rows.
        _ = command.Parameters.AddWithValue("$threadIds", JsonSerializer.Serialize(threadIds, JsonOptions));

        return " AND thread_id IN (SELECT value FROM json_each($threadIds))";
    }

    /// <inheritdoc />
    public async Task UpsertRunLedgerAsync(RunLedgerEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.GetConnectionAsync(ct).ConfigureAwait(false);

        var inputIdsJson = JsonSerializer.Serialize(entry.InputIds, JsonOptions);

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO run_ledger (run_id, thread_id, status, input_ids, created_at, updated_at)
            VALUES ($run_id, $thread_id, $status, $input_ids, $created_at, $updated_at)
            ON CONFLICT(run_id) DO UPDATE SET
                thread_id = excluded.thread_id,
                status = excluded.status,
                input_ids = excluded.input_ids,
                created_at = excluded.created_at,
                updated_at = excluded.updated_at;
            """;

        _ = command.Parameters.AddWithValue("$run_id", entry.RunId);
        _ = command.Parameters.AddWithValue("$thread_id", entry.ThreadId);
        _ = command.Parameters.AddWithValue("$status", entry.Status.ToString());
        _ = command.Parameters.AddWithValue("$input_ids", inputIdsJson);
        _ = command.Parameters.AddWithValue("$created_at", entry.CreatedAt.ToUnixTimeMilliseconds());
        _ = command.Parameters.AddWithValue("$updated_at", entry.UpdatedAt.ToUnixTimeMilliseconds());

        _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RunLedgerEntry?> LoadRunLedgerAsync(string runId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runId);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.GetConnectionAsync(ct).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT run_id, thread_id, status, input_ids, created_at, updated_at
            FROM run_ledger
            WHERE run_id = $run_id;
            """;
        _ = command.Parameters.AddWithValue("$run_id", runId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return !await reader.ReadAsync(ct).ConfigureAwait(false) ? null : ReadRunLedgerEntry(reader);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RunLedgerEntry>> ListRunLedgerAsync(string threadId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.GetConnectionAsync(ct).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT run_id, thread_id, status, input_ids, created_at, updated_at
            FROM run_ledger
            WHERE thread_id = $thread_id
            ORDER BY created_at DESC;
            """;
        _ = command.Parameters.AddWithValue("$thread_id", threadId);

        var entries = new List<RunLedgerEntry>();

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            entries.Add(ReadRunLedgerEntry(reader));
        }

        return entries;
    }

    /// <inheritdoc />
    public async Task RecordAcceptedInputAsync(
        string threadId,
        string inputId,
        DateTimeOffset acceptedAt,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(inputId);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.GetConnectionAsync(ct).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO accepted_inputs (thread_id, input_id, accepted_at)
            VALUES ($thread_id, $input_id, $accepted_at)
            ON CONFLICT(thread_id, input_id) DO UPDATE SET
                accepted_at = excluded.accepted_at;
            """;

        _ = command.Parameters.AddWithValue("$thread_id", threadId);
        _ = command.Parameters.AddWithValue("$input_id", inputId);
        _ = command.Parameters.AddWithValue("$accepted_at", acceptedAt.ToUnixTimeMilliseconds());

        _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> TryReserveAcceptedInputAsync(
        string threadId,
        string inputId,
        DateTimeOffset acceptedAt,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(inputId);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.GetConnectionAsync(ct).ConfigureAwait(false);

        // DO NOTHING (rather than the upsert above) is what makes this a reservation: the PRIMARY KEY on
        // (thread_id, input_id) decides the winner inside SQLite, and the affected-row count reports the
        // decision back. No read-then-write, so no window for a second caller to slip through.
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO accepted_inputs (thread_id, input_id, accepted_at)
            VALUES ($thread_id, $input_id, $accepted_at)
            ON CONFLICT(thread_id, input_id) DO NOTHING;
            """;

        _ = command.Parameters.AddWithValue("$thread_id", threadId);
        _ = command.Parameters.AddWithValue("$input_id", inputId);
        _ = command.Parameters.AddWithValue("$accepted_at", acceptedAt.ToUnixTimeMilliseconds());

        var rows = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return rows == 1;
    }

    /// <inheritdoc />
    public async Task RemoveAcceptedInputAsync(string threadId, string inputId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(inputId);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.GetConnectionAsync(ct).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM accepted_inputs WHERE thread_id = $thread_id AND input_id = $input_id;
            """;
        _ = command.Parameters.AddWithValue("$thread_id", threadId);
        _ = command.Parameters.AddWithValue("$input_id", inputId);

        _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>> ListAcceptedInputIdsAsync(string threadId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.GetConnectionAsync(ct).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT input_id FROM accepted_inputs WHERE thread_id = $thread_id;
            """;
        _ = command.Parameters.AddWithValue("$thread_id", threadId);

        var inputIds = new HashSet<string>();

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            _ = inputIds.Add(reader.GetString(0));
        }

        return inputIds;
    }

    /// <inheritdoc />
    public async Task<InputAcceptance?> TryReserveAcceptanceAsync(
        InputAcceptance acceptance,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(acceptance);
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        await using var connection = await _connectionFactory.GetConnectionAsync(ct).ConfigureAwait(false);

        while (true)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO input_acceptances (
                    thread_id, input_id, accepted_at, state, spawning_suppressed, idempotency_honored, reservation_id)
                VALUES ($thread_id, $input_id, $accepted_at, $state, $suppressed, $honored, $reservation_id)
                ON CONFLICT(thread_id, input_id) DO NOTHING;
                """;
            BindAcceptance(command, acceptance);
            if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1)
            {
                return null;
            }

            var existing = await ReadAcceptanceAsync(connection, acceptance.ThreadId, acceptance.InputId, ct)
                .ConfigureAwait(false);
            if (existing != null)
            {
                return existing;
            }
        }
    }

    /// <inheritdoc />
    public async Task<InputAcceptance?> GetAcceptanceAsync(
        string threadId,
        string inputId,
        CancellationToken ct = default
    )
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        await using var connection = await _connectionFactory.GetConnectionAsync(ct).ConfigureAwait(false);
        return await ReadAcceptanceAsync(connection, threadId, inputId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> TryRecordOutcomeAsync(InputAcceptance acceptance, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(acceptance);
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        await using var connection = await _connectionFactory.GetConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE input_acceptances
            SET accepted_at = $accepted_at, state = $state, spawning_suppressed = $suppressed,
                idempotency_honored = $honored
            WHERE thread_id = $thread_id AND input_id = $input_id AND reservation_id = $reservation_id;
            """;
        BindAcceptance(command, acceptance);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc />
    public async Task<bool> TryReleaseAcceptanceAsync(
        string threadId,
        string inputId,
        Guid reservationId,
        CancellationToken ct = default
    )
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        await using var connection = await _connectionFactory.GetConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM input_acceptances
            WHERE thread_id = $thread_id AND input_id = $input_id AND reservation_id = $reservation_id;
            """;
        _ = command.Parameters.AddWithValue("$thread_id", threadId);
        _ = command.Parameters.AddWithValue("$input_id", inputId);
        _ = command.Parameters.AddWithValue("$reservation_id", reservationId.ToString("N"));
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    private static void BindAcceptance(SqliteCommand command, InputAcceptance acceptance)
    {
        _ = command.Parameters.AddWithValue("$thread_id", acceptance.ThreadId);
        _ = command.Parameters.AddWithValue("$input_id", acceptance.InputId);
        _ = command.Parameters.AddWithValue("$accepted_at", acceptance.AcceptedAt.ToUnixTimeMilliseconds());
        _ = command.Parameters.AddWithValue("$state", acceptance.State.ToString());
        _ = command.Parameters.AddWithValue("$suppressed", acceptance.SpawningSuppressed ? 1 : 0);
        _ = command.Parameters.AddWithValue("$honored", acceptance.IdempotencyHonored ? 1 : 0);
        _ = command.Parameters.AddWithValue("$reservation_id", acceptance.ReservationId.ToString("N"));
    }

    private static async Task<InputAcceptance?> ReadAcceptanceAsync(
        SqliteConnection connection,
        string threadId,
        string inputId,
        CancellationToken ct
    )
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT accepted_at, state, spawning_suppressed, idempotency_honored, reservation_id
            FROM input_acceptances
            WHERE thread_id = $thread_id AND input_id = $input_id;
            """;
        _ = command.Parameters.AddWithValue("$thread_id", threadId);
        _ = command.Parameters.AddWithValue("$input_id", inputId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new InputAcceptance(
            threadId,
            inputId,
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
            Enum.Parse<InputAcceptanceState>(reader.GetString(1)),
            reader.GetInt64(2) != 0,
            reader.GetInt64(3) != 0,
            Guid.Parse(reader.GetString(4))
        );
    }

    /// <inheritdoc />
    public async Task RecordRunStartedAsync(RunLifecycleState state, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        RunLifecycleGuards.ValidateStart(state);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.GetConnectionAsync(ct).ConfigureAwait(false);

        using var command = connection.CreateCommand();

        // The WHERE clause on the upsert is what refuses to resurrect a terminal run: the insert
        // conflicts, the update matches no row, and the affected count comes back zero.
        command.CommandText = """
            INSERT INTO run_lifecycle (
                run_id, thread_id, generation_id, parent_run_id, parent_thread_id,
                spawning_tool_call_id, sub_agent_id, cause_kind, cause_tool_call_id,
                phase, outcome, turn_count, started_at, updated_at, terminal_at)
            VALUES (
                $run_id, $thread_id, $generation_id, $parent_run_id, $parent_thread_id,
                $spawning_tool_call_id, $sub_agent_id, $cause_kind, $cause_tool_call_id,
                $phase, NULL, $turn_count, $started_at, $started_at, NULL)
            ON CONFLICT(run_id) DO UPDATE SET
                thread_id = excluded.thread_id,
                generation_id = excluded.generation_id,
                parent_run_id = excluded.parent_run_id,
                parent_thread_id = excluded.parent_thread_id,
                spawning_tool_call_id = excluded.spawning_tool_call_id,
                sub_agent_id = excluded.sub_agent_id,
                cause_kind = excluded.cause_kind,
                cause_tool_call_id = excluded.cause_tool_call_id,
                started_at = excluded.started_at,
                updated_at = excluded.updated_at
            WHERE run_lifecycle.phase = $running;
            """;

        _ = command.Parameters.AddWithValue("$run_id", state.RunId);
        _ = command.Parameters.AddWithValue("$thread_id", state.ThreadId);
        _ = command.Parameters.AddWithValue("$generation_id", state.GenerationId);
        _ = command.Parameters.AddWithValue("$parent_run_id", (object?)state.ParentRunId ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$parent_thread_id", (object?)state.ParentThreadId ?? DBNull.Value);
        _ = command.Parameters.AddWithValue(
            "$spawning_tool_call_id",
            (object?)state.SpawningToolCallId ?? DBNull.Value
        );
        _ = command.Parameters.AddWithValue("$sub_agent_id", (object?)state.SubAgentId ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$cause_kind", state.CauseKind);
        _ = command.Parameters.AddWithValue("$cause_tool_call_id", (object?)state.CauseToolCallId ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$phase", nameof(RunLifecyclePhase.Running));
        _ = command.Parameters.AddWithValue("$running", nameof(RunLifecyclePhase.Running));
        _ = command.Parameters.AddWithValue("$turn_count", state.TurnCount);
        _ = command.Parameters.AddWithValue("$started_at", state.StartedAt.ToUnixTimeMilliseconds());

        var affected = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (affected == 0)
        {
            throw new InvalidOperationException(
                $"Run '{state.RunId}' already reached a terminal boundary; it cannot be restarted."
            );
        }
    }

    /// <inheritdoc />
    public async Task<RunLifecycleState?> LoadRunLifecycleAsync(string runId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runId);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.GetConnectionAsync(ct).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = $"{RunLifecycleSelectSql} WHERE run_id = $run_id;";
        _ = command.Parameters.AddWithValue("$run_id", runId);

        RunLifecycleState? state = null;
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                state = ReadRunLifecycle(reader);
            }
        }

        if (state == null)
        {
            return null;
        }

        var deferrals = await LoadDeferredCallsAsync(connection, [state.RunId], ct).ConfigureAwait(false);
        return Attach(state, deferrals);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RunLifecycleState>> ListRunLifecycleAsync(
        string threadId,
        CancellationToken ct = default
    ) => ListRunLifecycleCoreAsync(threadId, runningOnly: false, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<RunLifecycleState>> ListNonTerminalRunsAsync(
        string threadId,
        CancellationToken ct = default
    ) => ListRunLifecycleCoreAsync(threadId, runningOnly: true, ct);

    /// <inheritdoc />
    public async Task<bool> TryMarkRunTerminalAsync(
        string runId,
        string outcome,
        int turnCount,
        DateTimeOffset terminalAt,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(runId);
        ArgumentException.ThrowIfNullOrEmpty(outcome);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.GetConnectionAsync(ct).ConfigureAwait(false);

        using var command = connection.CreateCommand();

        // One conditional UPDATE, not a read followed by a write: the phase predicate is what makes
        // exactly one of two concurrent terminalizations see a row count of 1.
        command.CommandText = """
            UPDATE run_lifecycle
            SET phase = $terminal,
                outcome = $outcome,
                turn_count = $turn_count,
                terminal_at = $terminal_at,
                updated_at = $terminal_at
            WHERE run_id = $run_id AND phase = $running;
            """;

        _ = command.Parameters.AddWithValue("$terminal", nameof(RunLifecyclePhase.Terminal));
        _ = command.Parameters.AddWithValue("$running", nameof(RunLifecyclePhase.Running));
        _ = command.Parameters.AddWithValue("$outcome", outcome);
        _ = command.Parameters.AddWithValue("$turn_count", turnCount);
        _ = command.Parameters.AddWithValue("$terminal_at", terminalAt.ToUnixTimeMilliseconds());
        _ = command.Parameters.AddWithValue("$run_id", runId);

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc />
    public async Task<DeferredToolCallRecord> RecordDeferredToolCallAsync(
        string runId,
        DeferredToolCallRecord record,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(runId);
        ArgumentNullException.ThrowIfNull(record);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.GetConnectionAsync(ct).ConfigureAwait(false);

        using var transaction = connection.BeginTransaction();
        try
        {
            var threadId =
                await ReadScalarStringAsync(
                        connection,
                        transaction,
                        "SELECT thread_id FROM run_lifecycle WHERE run_id = $run_id;",
                        [("$run_id", runId)],
                        ct
                    )
                    .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Run '{runId}' was never recorded as started; cannot record deferral " + $"'{record.ToolCallId}'."
                );

            var existing = await LoadDeferredCallAsync(connection, transaction, threadId, record.ToolCallId, ct)
                .ConfigureAwait(false);
            if (existing != null)
            {
                transaction.Commit();
                return existing;
            }

            using var countCommand = connection.CreateCommand();
            countCommand.Transaction = transaction;
            countCommand.CommandText = "SELECT COUNT(*) FROM run_deferred_calls WHERE run_id = $run_id;";
            _ = countCommand.Parameters.AddWithValue("$run_id", runId);
            var ordinal =
                Convert.ToInt32(
                    await countCommand.ExecuteScalarAsync(ct).ConfigureAwait(false),
                    System.Globalization.CultureInfo.InvariantCulture
                ) + 1;

            using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = """
                INSERT INTO run_deferred_calls (
                    thread_id, tool_call_id, run_id, tool_name, generation_id, ordinal,
                    deferred_at, resolved_at, resolution_fingerprint, child_run_id)
                VALUES (
                    $thread_id, $tool_call_id, $run_id, $tool_name, $generation_id, $ordinal,
                    $deferred_at, NULL, NULL, NULL);
                """;
            _ = insertCommand.Parameters.AddWithValue("$thread_id", threadId);
            _ = insertCommand.Parameters.AddWithValue("$tool_call_id", record.ToolCallId);
            _ = insertCommand.Parameters.AddWithValue("$run_id", runId);
            _ = insertCommand.Parameters.AddWithValue("$tool_name", record.ToolName);
            _ = insertCommand.Parameters.AddWithValue("$generation_id", (object?)record.GenerationId ?? DBNull.Value);
            _ = insertCommand.Parameters.AddWithValue("$ordinal", ordinal);
            _ = insertCommand.Parameters.AddWithValue("$deferred_at", record.DeferredAt.ToUnixTimeMilliseconds());
            _ = await insertCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            await TouchRunAsync(connection, transaction, runId, record.DeferredAt, ct).ConfigureAwait(false);

            transaction.Commit();
            return record with { Ordinal = ordinal };
        }
        catch
        {
            RollbackQuietly(transaction);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<DeferredResolutionOutcome> TryResolveDeferredToolCallAsync(
        string threadId,
        string toolCallId,
        string resolutionFingerprint,
        string? childRunId,
        DateTimeOffset resolvedAt,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(toolCallId);
        ArgumentException.ThrowIfNullOrEmpty(resolutionFingerprint);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.GetConnectionAsync(ct).ConfigureAwait(false);

        using var transaction = connection.BeginTransaction();
        try
        {
            using var updateCommand = connection.CreateCommand();
            updateCommand.Transaction = transaction;

            // `resolved_at IS NULL` is the guard: a second resolution updates nothing and falls
            // through to the read below, which decides whether it was a retry or a disagreement.
            updateCommand.CommandText = """
                UPDATE run_deferred_calls
                SET resolved_at = $resolved_at,
                    resolution_fingerprint = $fingerprint,
                    child_run_id = $child_run_id
                WHERE thread_id = $thread_id
                  AND tool_call_id = $tool_call_id
                  AND resolved_at IS NULL;
                """;
            _ = updateCommand.Parameters.AddWithValue("$resolved_at", resolvedAt.ToUnixTimeMilliseconds());
            _ = updateCommand.Parameters.AddWithValue("$fingerprint", resolutionFingerprint);
            _ = updateCommand.Parameters.AddWithValue("$child_run_id", (object?)childRunId ?? DBNull.Value);
            _ = updateCommand.Parameters.AddWithValue("$thread_id", threadId);
            _ = updateCommand.Parameters.AddWithValue("$tool_call_id", toolCallId);

            DeferredResolutionOutcome outcome;
            if (await updateCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1)
            {
                var owningRunId = await ReadScalarStringAsync(
                        connection,
                        transaction,
                        """
                        SELECT run_id FROM run_deferred_calls
                        WHERE thread_id = $thread_id AND tool_call_id = $tool_call_id;
                        """,
                        [("$thread_id", threadId), ("$tool_call_id", toolCallId)],
                        ct
                    )
                    .ConfigureAwait(false);
                if (owningRunId != null)
                {
                    await TouchRunAsync(connection, transaction, owningRunId, resolvedAt, ct).ConfigureAwait(false);
                }

                outcome = DeferredResolutionOutcome.Resolved;
            }
            else
            {
                var committed = await LoadDeferredCallAsync(connection, transaction, threadId, toolCallId, ct)
                    .ConfigureAwait(false);

                outcome =
                    committed == null ? DeferredResolutionOutcome.NotFound
                    : string.Equals(committed.ResolutionFingerprint, resolutionFingerprint, StringComparison.Ordinal)
                        ? DeferredResolutionOutcome.Duplicate
                    : DeferredResolutionOutcome.Conflict;
            }

            transaction.Commit();
            return outcome;
        }
        catch
        {
            RollbackQuietly(transaction);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<string?> AttachDeferredChildRunAsync(
        string threadId,
        string toolCallId,
        string childRunId,
        DateTimeOffset attachedAt,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(toolCallId);
        ArgumentException.ThrowIfNullOrEmpty(childRunId);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.GetConnectionAsync(ct).ConfigureAwait(false);

        using var transaction = connection.BeginTransaction();
        try
        {
            using var updateCommand = connection.CreateCommand();
            updateCommand.Transaction = transaction;

            // The two guards are the whole contract: `resolved_at IS NOT NULL` refuses to name a
            // continuation for a result that has not arrived, and `child_run_id IS NULL` refuses to
            // displace one that is already named. A no-op update falls through to the read below,
            // which reports whichever name stands.
            updateCommand.CommandText = """
                UPDATE run_deferred_calls
                SET child_run_id = $child_run_id
                WHERE thread_id = $thread_id
                  AND tool_call_id = $tool_call_id
                  AND resolved_at IS NOT NULL
                  AND child_run_id IS NULL;
                """;
            _ = updateCommand.Parameters.AddWithValue("$child_run_id", childRunId);
            _ = updateCommand.Parameters.AddWithValue("$thread_id", threadId);
            _ = updateCommand.Parameters.AddWithValue("$tool_call_id", toolCallId);

            string? standing;
            if (await updateCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1)
            {
                var owningRunId = await ReadScalarStringAsync(
                        connection,
                        transaction,
                        """
                        SELECT run_id FROM run_deferred_calls
                        WHERE thread_id = $thread_id AND tool_call_id = $tool_call_id;
                        """,
                        [("$thread_id", threadId), ("$tool_call_id", toolCallId)],
                        ct
                    )
                    .ConfigureAwait(false);
                if (owningRunId != null)
                {
                    await TouchRunAsync(connection, transaction, owningRunId, attachedAt, ct).ConfigureAwait(false);
                }

                standing = childRunId;
            }
            else
            {
                var committed = await LoadDeferredCallAsync(connection, transaction, threadId, toolCallId, ct)
                    .ConfigureAwait(false);

                standing =
                    committed == null
                        ? null
                        : RunLifecycleGuards.ClassifyChildRunAttach(committed, childRunId).Standing;
            }

            transaction.Commit();
            return standing;
        }
        catch
        {
            RollbackQuietly(transaction);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _schemaLock.Dispose();
        _metadataWriteLock.Dispose();

        if (_ownsFactory)
        {
            await _connectionFactory.DisposeAsync().ConfigureAwait(false);
        }
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

            await SqliteSchemaInitializer.InitializeSchemaAsync(_connectionFactory, ct).ConfigureAwait(false);
            _schemaInitialized = true;
        }
        finally
        {
            _ = _schemaLock.Release();
        }
    }

    private const string MessageSelectSql = """
        SELECT id, thread_id, run_id, parent_run_id, generation_id,
               message_order_idx, timestamp, message_type, role, from_agent, message_json, seq
        FROM messages
        """;

    private static PersistedMessage ReadMessage(SqliteDataReader reader)
    {
        return new PersistedMessage
        {
            Seq = reader.IsDBNull(11) ? null : reader.GetInt64(11),
            Id = reader.GetString(0),
            ThreadId = reader.GetString(1),
            RunId = reader.GetString(2),
            ParentRunId = reader.IsDBNull(3) ? null : reader.GetString(3),
            GenerationId = reader.IsDBNull(4) ? null : reader.GetString(4),
            MessageOrderIdx = reader.IsDBNull(5) ? null : reader.GetInt32(5),
            Timestamp = reader.GetInt64(6),
            MessageType = reader.GetString(7),
            Role = reader.GetString(8),
            FromAgent = reader.IsDBNull(9) ? null : reader.GetString(9),
            MessageJson = reader.GetString(10),
        };
    }

    private const string RunLifecycleSelectSql = """
        SELECT run_id, thread_id, generation_id, parent_run_id, parent_thread_id,
               spawning_tool_call_id, sub_agent_id, cause_kind, cause_tool_call_id,
               phase, outcome, turn_count, started_at, updated_at, terminal_at
        FROM run_lifecycle
        """;

    private const string RunDeferredCallSelectSql = """
        SELECT tool_call_id, tool_name, generation_id, ordinal,
               deferred_at, resolved_at, resolution_fingerprint, child_run_id, run_id
        FROM run_deferred_calls
        """;

    private async Task<IReadOnlyList<RunLifecycleState>> ListRunLifecycleCoreAsync(
        string threadId,
        bool runningOnly,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(threadId);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.GetConnectionAsync(ct).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        // run_id breaks ties so two runs started in the same millisecond come back in a fixed
        // order rather than whatever order the table scan happens to produce.
        command.CommandText = runningOnly
            ? $"{RunLifecycleSelectSql} WHERE thread_id = $thread_id AND phase = $running ORDER BY started_at, run_id;"
            : $"{RunLifecycleSelectSql} WHERE thread_id = $thread_id ORDER BY started_at DESC, run_id;";
        _ = command.Parameters.AddWithValue("$thread_id", threadId);
        if (runningOnly)
        {
            _ = command.Parameters.AddWithValue("$running", nameof(RunLifecyclePhase.Running));
        }

        var states = new List<RunLifecycleState>();
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                states.Add(ReadRunLifecycle(reader));
            }
        }

        if (states.Count == 0)
        {
            return states;
        }

        var deferrals = await LoadDeferredCallsAsync(connection, [.. states.Select(s => s.RunId)], ct)
            .ConfigureAwait(false);

        return [.. states.Select(s => Attach(s, deferrals))];
    }

    /// <summary>
    /// Reads every deferral belonging to the given runs, grouped by run and ordered within each.
    /// </summary>
    /// <remarks>
    /// One query for the whole set rather than one per run: listing a thread's lifecycle is a
    /// read the status surfaces do often, and a per-run round trip turns it into N+1.
    /// </remarks>
    private static async Task<Dictionary<string, List<DeferredToolCallRecord>>> LoadDeferredCallsAsync(
        SqliteConnection connection,
        IReadOnlyList<string> runIds,
        CancellationToken ct
    )
    {
        var byRun = new Dictionary<string, List<DeferredToolCallRecord>>(StringComparer.Ordinal);
        if (runIds.Count == 0)
        {
            return byRun;
        }

        using var command = connection.CreateCommand();
        var placeholders = new string[runIds.Count];
        for (var i = 0; i < runIds.Count; i++)
        {
            placeholders[i] = $"$run_{i}";
            _ = command.Parameters.AddWithValue(placeholders[i], runIds[i]);
        }

        command.CommandText =
            $"{RunDeferredCallSelectSql} WHERE run_id IN ({string.Join(", ", placeholders)}) ORDER BY ordinal;";

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var runId = reader.GetString(8);
            if (!byRun.TryGetValue(runId, out var records))
            {
                records = [];
                byRun[runId] = records;
            }

            records.Add(ReadDeferredCall(reader));
        }

        return byRun;
    }

    private static async Task<DeferredToolCallRecord?> LoadDeferredCallAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string threadId,
        string toolCallId,
        CancellationToken ct
    )
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"{RunDeferredCallSelectSql} WHERE thread_id = $thread_id AND tool_call_id = $tool_call_id;";
        _ = command.Parameters.AddWithValue("$thread_id", threadId);
        _ = command.Parameters.AddWithValue("$tool_call_id", toolCallId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadDeferredCall(reader) : null;
    }

    private static async Task<string?> ReadScalarStringAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        IReadOnlyList<(string Name, string Value)> parameters,
        CancellationToken ct
    )
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            _ = command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    /// <summary>
    /// Advances a run's <c>updated_at</c> so a reader can tell a run that is still moving from one
    /// that has been sitting on an unresolved deferral since it started.
    /// </summary>
    private static async Task TouchRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        DateTimeOffset at,
        CancellationToken ct
    )
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE run_lifecycle SET updated_at = $updated_at WHERE run_id = $run_id;";
        _ = command.Parameters.AddWithValue("$updated_at", at.ToUnixTimeMilliseconds());
        _ = command.Parameters.AddWithValue("$run_id", runId);

        _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static void RollbackQuietly(SqliteTransaction transaction)
    {
        try
        {
            transaction.Rollback();
        }
        catch
        {
            // Swallow: a rollback failure (typically a connection that is already broken) must not
            // replace the original exception, which is the one the caller needs to diagnose.
        }
    }

    private static RunLifecycleState Attach(
        RunLifecycleState state,
        IReadOnlyDictionary<string, List<DeferredToolCallRecord>> deferrals
    ) => deferrals.TryGetValue(state.RunId, out var records) ? state with { DeferredToolCalls = records } : state;

    private static RunLifecycleState ReadRunLifecycle(SqliteDataReader reader) =>
        new()
        {
            RunId = reader.GetString(0),
            ThreadId = reader.GetString(1),
            GenerationId = reader.GetString(2),
            ParentRunId = reader.IsDBNull(3) ? null : reader.GetString(3),
            ParentThreadId = reader.IsDBNull(4) ? null : reader.GetString(4),
            SpawningToolCallId = reader.IsDBNull(5) ? null : reader.GetString(5),
            SubAgentId = reader.IsDBNull(6) ? null : reader.GetString(6),
            CauseKind = reader.GetString(7),
            CauseToolCallId = reader.IsDBNull(8) ? null : reader.GetString(8),
            Phase = Enum.Parse<RunLifecyclePhase>(reader.GetString(9)),
            Outcome = reader.IsDBNull(10) ? null : reader.GetString(10),
            TurnCount = reader.GetInt32(11),
            StartedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(12)),
            UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(13)),
            TerminalAt = reader.IsDBNull(14) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(14)),
        };

    private static DeferredToolCallRecord ReadDeferredCall(SqliteDataReader reader) =>
        new()
        {
            ToolCallId = reader.GetString(0),
            ToolName = reader.GetString(1),
            GenerationId = reader.IsDBNull(2) ? null : reader.GetString(2),
            Ordinal = reader.GetInt32(3),
            DeferredAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
            ResolvedAt = reader.IsDBNull(5) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
            ResolutionFingerprint = reader.IsDBNull(6) ? null : reader.GetString(6),
            ChildRunId = reader.IsDBNull(7) ? null : reader.GetString(7),
        };

    private static RunLedgerEntry ReadRunLedgerEntry(SqliteDataReader reader)
    {
        var runId = reader.GetString(0);
        var threadId = reader.GetString(1);
        var status = Enum.Parse<RunStatus>(reader.GetString(2));
        var inputIds = JsonSerializer.Deserialize<List<string>>(reader.GetString(3), JsonOptions) ?? [];
        var createdAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4));
        var updatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5));

        return new RunLedgerEntry(threadId, runId, status, inputIds, createdAt, updatedAt);
    }

    private static ThreadMetadata ReadMetadata(SqliteDataReader reader)
    {
        var threadId = reader.GetString(0);
        var currentRunId = reader.IsDBNull(1) ? null : reader.GetString(1);
        var lastUpdated = reader.GetInt64(2);
        var metadataJson = reader.IsDBNull(3) ? null : reader.GetString(3);

        var (sessionMappings, latestRunId, properties) = DeserializeMetadataExtensions(metadataJson);

        return new ThreadMetadata
        {
            ThreadId = threadId,
            CurrentRunId = currentRunId,
            LatestRunId = latestRunId,
            LastUpdated = lastUpdated,
            SessionMappings = sessionMappings,
            Properties = properties,
            TenantId = reader.IsDBNull(4) ? null : reader.GetString(4),
            OwnerUserId = reader.IsDBNull(5) ? null : reader.GetString(5),
            OwnerAppId = reader.IsDBNull(6) ? null : reader.GetString(6),
            Visibility = ParseVisibility(reader.IsDBNull(7) ? null : reader.GetString(7)),
        };
    }

    /// <summary>
    /// Reads the stored visibility. A null - and anything unrecognised - reads as null, which the
    /// policy treats as <see cref="Visibility.Private"/>: an unreadable value must never widen
    /// access.
    /// </summary>
    private static Visibility? ParseVisibility(string? stored) =>
        Enum.TryParse<Visibility>(stored, ignoreCase: false, out var parsed) ? parsed : null;

    private static string? SerializeMetadataExtensions(ThreadMetadata metadata)
    {
        if (metadata.SessionMappings == null && metadata.LatestRunId == null && metadata.Properties == null)
        {
            return null;
        }

        var extensionData = new MetadataExtensionData
        {
            LatestRunId = metadata.LatestRunId,
            SessionMappings = metadata.SessionMappings,
            Properties = metadata.Properties?.ToDictionary(x => x.Key, x => x.Value),
        };

        return JsonSerializer.Serialize(extensionData, JsonOptions);
    }

    private static (
        IReadOnlyDictionary<string, string>?,
        string?,
        ImmutableDictionary<string, object>?
    ) DeserializeMetadataExtensions(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return (null, null, null);
        }

        try
        {
            var data = JsonSerializer.Deserialize<MetadataExtensionData>(json, JsonOptions);
            if (data == null)
            {
                return (null, null, null);
            }

            var properties = data.Properties?.ToImmutableDictionary();
            return (data.SessionMappings, data.LatestRunId, properties);
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }

    private sealed class MetadataExtensionData
    {
        public string? LatestRunId { get; set; }
        public IReadOnlyDictionary<string, string>? SessionMappings { get; set; }
        public Dictionary<string, object>? Properties { get; set; }
    }
}
