namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.Sqlite;

/// <summary>SQLite-backed <see cref="INotifyWaitStore"/>. Upserts by wait_id; deletes on terminal.</summary>
public sealed class SqliteNotifyWaitStore : INotifyWaitStore
{
    private readonly ISqliteConnectionFactory _factory;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private bool _schemaInitialized;

    public SqliteNotifyWaitStore(ISqliteConnectionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    /// <summary>
    /// Lazily initializes the schema on first use so callers don't need to remember to invoke
    /// <see cref="SqliteSchemaInitializer"/> themselves before constructing this store — mirrors
    /// <c>SqliteConversationStore.EnsureSchemaAsync</c>'s double-checked-lock pattern.
    /// </summary>
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

    public async Task SaveAsync(NotifyWaitRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _factory.GetConnectionAsync(ct).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO notify_waits
                (wait_id, thread_id, kind, args, label, max_fires, fires_so_far, timeout_at, armed_at, status)
            VALUES ($id, $thread, $kind, $args, $label, $max, $fires, $timeout, $armed, $status)
            ON CONFLICT(wait_id) DO UPDATE SET
                args = excluded.args,
                label = excluded.label,
                max_fires = excluded.max_fires,
                fires_so_far = excluded.fires_so_far,
                timeout_at = excluded.timeout_at,
                armed_at = excluded.armed_at,
                status = excluded.status;
            """;
        _ = command.Parameters.AddWithValue("$id", record.WaitId);
        _ = command.Parameters.AddWithValue("$thread", record.ThreadId);
        _ = command.Parameters.AddWithValue("$kind", record.Kind);
        _ = command.Parameters.AddWithValue("$args", record.Args);
        _ = command.Parameters.AddWithValue("$label", (object?)record.Label ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$max", (object?)record.MaxFires ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$fires", record.FiresSoFar);
        _ = command.Parameters.AddWithValue("$timeout", record.TimeoutAtUnixMs);
        _ = command.Parameters.AddWithValue("$armed", record.ArmedAtUnixMs);
        _ = command.Parameters.AddWithValue("$status", record.Status);
        _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string waitId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(waitId);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _factory.GetConnectionAsync(ct).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM notify_waits WHERE wait_id = $id;";
        _ = command.Parameters.AddWithValue("$id", waitId);
        _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<NotifyWaitRecord>> LoadActiveAsync(
        string threadId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _factory.GetConnectionAsync(ct).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT wait_id, thread_id, kind, args, label, max_fires, fires_so_far, timeout_at, armed_at, status
            FROM notify_waits WHERE thread_id = $thread AND status = 'active';
            """;
        _ = command.Parameters.AddWithValue("$thread", threadId);

        var results = new List<NotifyWaitRecord>();

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new NotifyWaitRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt64(7),
                reader.GetInt64(8),
                reader.GetString(9)));
        }

        return results;
    }
}
