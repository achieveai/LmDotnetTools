using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Reviewd.Sample;

internal static class PrSelection
{
    internal static async Task<int> RunAsync(CliOptions options, CancellationToken ct, DateTimeOffset? now = null)
    {
        var database = options.Get("db") ?? "reviewd.db";
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly }.ToString()
        );
        await connection.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();
        var selected = new List<object>();
        var observedAt = now ?? DateTimeOffset.UtcNow;
        foreach (var repo in PrCommands.ParseRepos(options))
        {
            var prefix = repo.NormalizedKey;
            var snapshot = await ReadAsync($"reviewd/pr-snapshot/{prefix}");
            if (snapshot is null)
            {
                throw new InvalidOperationException($"No saved PR snapshot for {repo.DisplayName}; refresh first.");
            }
            foreach (var entry in snapshot.Value.EnumerateObject().OrderBy(entry => entry.Name, StringComparer.Ordinal))
            {
                var pr = entry.Value;
                var attempt = await ReadAsync($"reviewd/review-attempt/{prefix}/{entry.Name}");
                if (
                    attempt is not null
                    && (
                        !attempt.Value.TryGetProperty("stage", out var attemptStage)
                        || attemptStage.GetString() != "completed-verified"
                    )
                )
                {
                    continue;
                }
                var review = await ReadAsync($"reviewd/review-run/{prefix}/{entry.Name}");
                var thread = await ReadAsync($"reviewd/pr-thread/{prefix}/{entry.Name}");
                if (IsEligible(pr, review, thread is not null, observedAt))
                {
                    selected.Add(
                        new
                        {
                            repository = repo.DisplayName,
                            current = pr,
                            threadId = thread,
                            reason = review is null ? "unreviewed" : "code-changed-after-cooldown",
                        }
                    );
                }
            }
        }
        foreach (var row in selected)
        {
            Console.WriteLine(JsonSerializer.Serialize(row));
        }
        Console.Error.WriteLine($"Picked {selected.Count} eligible PR(s) from saved snapshots; no reviews started.");
        return 0;

        async Task<JsonElement?> ReadAsync(string key)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT value FROM cache WHERE key = @key";
            command.Parameters.AddWithValue("@key", key);
            var value = await command.ExecuteScalarAsync(ct);
            return value is string json ? JsonSerializer.Deserialize<JsonElement>(json) : null;
        }
    }

    internal static bool IsEligible(JsonElement pr, JsonElement? review, bool hasThread, DateTimeOffset now)
    {
        if (
            pr.ValueKind != JsonValueKind.Object
            || !pr.TryGetProperty("Lifecycle", out var lifecycle)
            || lifecycle.ValueKind != JsonValueKind.String
            || lifecycle.GetString() != "Open"
            || !pr.TryGetProperty("IsDraft", out var draft)
            || draft.ValueKind != JsonValueKind.False
            || !pr.TryGetProperty("HeadSha", out var head)
            || head.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(head.GetString())
        )
        {
            return false;
        }
        if (review is null)
        {
            // A binding without a completed receipt may be an in-flight or failed submission.
            return !hasThread;
        }
        var receipt = review.Value;
        if (
            receipt.ValueKind != JsonValueKind.Object
            || !receipt.TryGetProperty("stage", out var stage)
            || stage.ValueKind != JsonValueKind.String
            || stage.GetString() != "completed-verified"
            || !receipt.TryGetProperty("reviewedHeadSha", out var reviewedHead)
            || reviewedHead.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(reviewedHead.GetString())
            || string.Equals(head.GetString(), reviewedHead.GetString(), StringComparison.OrdinalIgnoreCase)
            || !receipt.TryGetProperty("reviewCompletedAt", out var completed)
        )
        {
            return false;
        }
        DateTimeOffset completedAt;
        if (completed.ValueKind == JsonValueKind.Number && completed.TryGetDouble(out var seconds))
        {
            if (
                !double.IsFinite(seconds)
                || seconds < (DateTimeOffset.MinValue - DateTimeOffset.UnixEpoch).TotalSeconds
                || seconds >= (DateTimeOffset.MaxValue - DateTimeOffset.UnixEpoch).TotalSeconds
            )
            {
                return false;
            }
            completedAt = DateTimeOffset.UnixEpoch.AddSeconds(seconds);
        }
        else if (completed.ValueKind != JsonValueKind.String || !completed.TryGetDateTimeOffset(out completedAt))
        {
            return false;
        }
        return now - completedAt >= TimeSpan.FromMinutes(30);
    }
}
