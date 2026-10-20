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

    internal static SqliteConnection Connection(string database) =>
        new(new SqliteConnectionStringBuilder { DataSource = database }.ToString());

    private static string Key(RepoIdentity repo, string prId) => $"reviewd/pr-thread/{repo.NormalizedKey}/{prId}";
}
