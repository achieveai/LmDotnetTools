using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Reviewd.Sample;

/// <summary>
///     Which arm a review database belongs to. A shadow arm (<c>--shadow</c>) pins its <c>--db</c> on its first
///     executed run, and every later run on that database must be the same arm. The database is what keeps a shadow
///     arm apart from the live reviewer: its thread bindings, receipts and batch queue. A live run there could reuse a
///     read-only conversation, and a shadow receipt would stop the live review of that head as already reviewed.
/// </summary>
internal static class ReviewArm
{
    internal const string Key = "reviewd/arm";

    /// <summary>
    ///     Refuses a run from another arm than the one <paramref name="database" /> belongs to. An executed run
    ///     (<paramref name="pin" />) on an unpinned database pins it to its own arm in one insert, so a live and a
    ///     shadow run that start together cannot both claim it. A shadow run is refused when an unpinned database
    ///     already holds live reviews or a live batch queue; a database from before the pin holds no marker.
    /// </summary>
    internal static async Task CheckAsync(string database, bool shadow, string? variant, bool pin, CancellationToken ct)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = database,
                // Only a shadow arm may start a new database; a mistyped live --db must fail, not start empty.
                Mode = pin
                    ? (shadow ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite)
                    : SqliteOpenMode.ReadOnly,
                // A one-off check: a pooled handle would hold the file open after the run, under its own pool key.
                Pooling = false,
            }.ToString()
        );
        await connection.OpenAsync(ct);
        if (pin)
            _ = new AchieveAi.LmDotnetTools.Misc.Storage.SqliteKvStore(connection); // creates the cache table
        else if (!await HasCacheAsync(connection, ct))
            return; // nothing is stored yet, so nothing is pinned and nothing is live
        var pinned = await ReadAsync(connection, ct);
        if (pinned is null)
        {
            if (shadow && await HoldsLiveReviewsAsync(connection, ct))
                throw new ArgumentException(
                    "This --db holds live reviews. A shadow arm needs a --db of its own, so the live reviewer never "
                        + "reuses its read-only conversations or reads its reviews as posted."
                );
            if (!pin)
                return;
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO cache(key, value) VALUES (@key, @value) ON CONFLICT(key) DO NOTHING";
            insert.Parameters.AddWithValue("@key", Key);
            insert.Parameters.AddWithValue("@value", Arm(shadow, variant).ToJsonString());
            await insert.ExecuteNonQueryAsync(ct);
            pinned = await ReadAsync(connection, ct);
        }
        if (pinned is null)
            return;
        var pinnedShadow = pinned["shadow"]?.GetValue<bool>() == true;
        var pinnedVariant = pinned["variant"]?.GetValue<string>();
        if (pinnedShadow != shadow || pinnedVariant != variant)
            throw new ArgumentException(
                $"This --db belongs to {Describe(pinnedShadow, pinnedVariant)}; {Describe(shadow, variant)} needs a "
                    + "--db of its own."
            );
    }

    /// <summary>
    ///     Refuses an operator slot reap or reclaim of a shadow arm's <paramref name="database" /> through a session
    ///     that <c>sandbox create --read-only-egress</c> did not make: the slot reset runs workspace helpers the
    ///     shadow agent can edit. A live or unpinned database takes any session.
    /// </summary>
    internal static async Task RequireReadOnlySessionForShadowAsync(
        string database,
        Func<string> tokenStoreDir,
        string session,
        CancellationToken ct
    )
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString()
        );
        await connection.OpenAsync(ct);
        if (
            await HasCacheAsync(connection, ct)
            && await ReadAsync(connection, ct) is { } pinned
            && pinned["shadow"]?.GetValue<bool>() == true
        )
        {
            if (!ReadOnlySessions.IsMarked(tokenStoreDir(), session))
            {
                throw new ArgumentException(
                    $"This --db belongs to a shadow arm, so --session {session} must be a session made by "
                        + "sandbox create --read-only-egress on this host."
                );
            }
        }
    }

    private static JsonObject Arm(bool shadow, string? variant) => new() { ["shadow"] = shadow, ["variant"] = variant };

    private static string Describe(bool shadow, string? variant) =>
        !shadow ? "the live reviewer"
        : variant is null ? "a shadow arm"
        : $"shadow arm '{variant}'";

    private static async Task<JsonObject?> ReadAsync(SqliteConnection connection, CancellationToken ct)
    {
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT value FROM cache WHERE key=@key";
        query.Parameters.AddWithValue("@key", Key);
        return await query.ExecuteScalarAsync(ct) is string value ? JsonNode.Parse(value) as JsonObject : null;
    }

    private static async Task<bool> HasCacheAsync(SqliteConnection connection, CancellationToken ct)
    {
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='cache'";
        return await query.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task<bool> HoldsLiveReviewsAsync(SqliteConnection connection, CancellationToken ct)
    {
        using var query = connection.CreateCommand();
        query.CommandText =
            "SELECT 1 FROM cache WHERE ((key LIKE 'reviewd/review-run/%' OR key LIKE 'reviewd/review-attempt/%') "
            + "AND coalesce(json_extract(value,'$.shadow'),0) <> 1) "
            // A batch queue or round is live unless its scope says shadow, even before it holds any attempt.
            + "OR ((key LIKE 'reviewd/batch-queue/%' OR key LIKE 'reviewd/batch-state/%') "
            + "AND coalesce(json_extract(value,'$.scope.shadow'),0) <> 1) LIMIT 1";
        return await query.ExecuteScalarAsync(ct) is not null;
    }
}
