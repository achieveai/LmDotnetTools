using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.Sqlite;
using AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;
using Microsoft.Data.Sqlite;

namespace TodoEval.Runner.Metrics;

/// <summary>
/// Writes the host's SQLite conversation store (<c>conversations.db</c>) out as the
/// <c>conversations/&lt;threadId&gt;/</c> layout that <see cref="ConversationStoreReader"/>, the redactor
/// and every archived baseline use. Both sides are the host's own store types, so names and envelopes
/// match what the host's file store wrote. One difference: a forked thread's file carries its
/// inherited history inline, because the SQLite store resolves it on load.
/// </summary>
internal static class ConversationDbExport
{
    /// <summary>
    /// Exports every thread that has messages or metadata and returns how many it wrote. A missing
    /// database throws: a host that ran must have written one, and an empty harvest would otherwise
    /// read as "conversation not found" for every run.
    /// </summary>
    public static async Task<int> ExportAsync(string databasePath, string conversationsDir, CancellationToken ct)
    {
        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException(
                $"The eval host left no conversation store at '{databasePath}'; nothing can be harvested.",
                databasePath
            );
        }

        await using var source = new SqliteConversationStore(databasePath);
        _ = await source.LoadMetadataAsync(string.Empty, ct);
        var threadIds = await ReadThreadIdsAsync(databasePath, ct);
        var target = new FileConversationStore(conversationsDir);
        foreach (var threadId in threadIds)
        {
            // Messages before metadata: the file store reads a fork point from metadata on append, and
            // the loaded list already holds any inherited history.
            await target.AppendMessagesAsync(threadId, await source.LoadMessagesAsync(threadId, ct), ct);
            if (await source.LoadMetadataAsync(threadId, ct) is { } metadata)
            {
                await target.SaveMetadataAsync(threadId, metadata, ct);
            }

            if (await ConversationUsageProjection.LoadSnapshotAsync(source, threadId, ct) is { } usage)
            {
                var aggregate =
                    usage.Records.Count == 0 && usage.Aggregate is { } persisted
                        ? persisted
                        : ConversationUsageAggregate.Fold(
                            threadId,
                            usage.Records,
                            usage.FoldedRevision,
                            usage.Completeness
                        );
                await ConversationUsageProjection.SaveAsync(target, aggregate, usage.Records, ct);
            }
        }

        return threadIds.Count;
    }

    // The store's listing API hides agent-owned threads by default and never lists message-only ones,
    // so the thread ids come straight from the two tables that can hold a thread.
    private static async Task<IReadOnlyList<string>> ReadThreadIdsAsync(string databasePath, CancellationToken ct)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT thread_id FROM messages
            UNION
            SELECT thread_id FROM thread_metadata
            UNION
            SELECT thread_id FROM conversation_usage_state
            ORDER BY thread_id;
            """;
        var threadIds = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            threadIds.Add(reader.GetString(0));
        }

        return threadIds;
    }
}
