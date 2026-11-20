using AchieveAi.LmDotnetTools.Misc.Storage;
using CodeReviewDaemon.Sample.Persistence.Models;
using Microsoft.Data.Sqlite;

namespace Reviewd.Sample;

internal static class PrThreadBindings
{
    public static async Task<string?> GetAsync(string database, RepoIdentity repo, string prId, CancellationToken ct)
    {
        using var connection = Connection(database);
        return await new SqliteKvStore(connection).GetAsync<string>(Key(repo, prId), ct);
    }

    public static async Task SetAsync(
        string database,
        RepoIdentity repo,
        string prId,
        string threadId,
        CancellationToken ct
    )
    {
        using var connection = Connection(database);
        var store = new SqliteKvStore(connection);
        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO cache(key, value) VALUES (@key, @value) ON CONFLICT(key) DO NOTHING";
        insert.Parameters.AddWithValue("@key", Key(repo, prId));
        insert.Parameters.AddWithValue("@value", System.Text.Json.JsonSerializer.Serialize(threadId));
        await insert.ExecuteNonQueryAsync(ct);
        var existing = await store.GetAsync<string>(Key(repo, prId), ct);
        if (existing != threadId)
        {
            throw new InvalidOperationException(
                $"PR {prId} already has conversation '{existing}'; no binding was replaced."
            );
        }
    }

    /// <summary>Removes the binding only while it still names <paramref name="threadId"/>.</summary>
    public static async Task ClearAsync(
        string database,
        RepoIdentity repo,
        string prId,
        string threadId,
        CancellationToken ct
    )
    {
        using var connection = Connection(database);
        await connection.OpenAsync(ct);
        using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM cache WHERE key = @key AND value = @value";
        delete.Parameters.AddWithValue("@key", Key(repo, prId));
        delete.Parameters.AddWithValue("@value", System.Text.Json.JsonSerializer.Serialize(threadId));
        await delete.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Opens Reviewd's database, creating it if it is missing, in WAL mode. This is the only open that may create the
    /// file, so every database Reviewd makes starts in WAL. The mode is stored in the file, so on a database already
    /// in WAL (the live ones) this is a no-op.
    /// </summary>
    /// <remarks>
    /// A rollback-mode commit deletes its <c>-journal</c>. On Windows, an anti-virus scan holding that file makes the
    /// delete fail; the commit then reports a disk I/O error and leaves a hot journal that every read-only connection
    /// refuses with "attempt to write a readonly database". WAL never deletes a journal on commit.
    /// </remarks>
    internal static SqliteConnection Connection(string database)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL";
            _ = command.ExecuteScalar();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static string Key(RepoIdentity repo, string prId) => $"reviewd/pr-thread/{repo.NormalizedKey}/{prId}";
}
