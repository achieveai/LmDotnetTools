using System.Text.Json;
using System.Text.Json.Nodes;
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
                var earlierHead =
                    attempt is not null
                    && IsReleasedForEarlierHead(
                        attempt.Value,
                        pr.TryGetProperty("HeadSha", out var currentHead) ? currentHead.GetString() : null
                    );
                var unaccepted = attempt is not null && HoldsNoAcceptedInput(attempt.Value);
                if (attempt is not null && !IsSettled(attempt.Value) && !earlierHead && !unaccepted)
                {
                    continue;
                }
                var review = await ReadAsync($"reviewd/review-run/{prefix}/{entry.Name}");
                var thread = await ReadAsync($"reviewd/pr-thread/{prefix}/{entry.Name}");
                // The binding an attempt with no accepted input left names its own unsent conversation.
                var ownUnsentThread =
                    unaccepted
                    && thread?.GetString() is { Length: > 0 } bound
                    && attempt!.Value.TryGetProperty("threadId", out var attemptThread)
                    && attemptThread.ValueKind == JsonValueKind.String
                    && attemptThread.GetString() == bound;
                if (
                    // Neither that binding nor an earlier attempt's is an in-flight submission for this head. The
                    // runner still asks the host whether the input was accepted and whether the conversation is
                    // running before it sends anything.
                    IsEligible(pr, review, thread is not null && !earlierHead && !ownUnsentThread, observedAt)
                    && (
                        options.Get("max-age-hours") is not { } age
                        || IsWithinCreationAge(
                            pr.TryGetProperty("CreatedAt", out var created)
                                ? JsonNode.Parse(created.GetRawText())
                                : null,
                            int.Parse(age, System.Globalization.CultureInfo.InvariantCulture),
                            observedAt
                        )
                    )
                )
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
        if (options.Get("selection-out") is { } selectionOut)
        {
            await WriteSelectionAsync(selectionOut, selected, options.Get("max-age-hours"), observedAt, ct);
            Console.Error.WriteLine($"Wrote batch selection {selectionOut}: every repo uses lanes 0-5.");
        }
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

    internal static bool IsWithinCreationAge(JsonNode? createdAt, int hours, DateTimeOffset now)
    {
        if (hours <= 0 || createdAt is not JsonValue value || !value.TryGetValue<string>(out var text))
            return false;
        // Require an explicit RFC3339 offset. DateTimeOffset alone also accepts local/offset-free dates.
        if (
            !System.Text.RegularExpressions.Regex.IsMatch(
                text,
                @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant
            )
            || !DateTimeOffset.TryParse(
                text,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var created
            )
        )
            return false;
        var age = now - created;
        return age >= TimeSpan.Zero && age.TotalHours <= hours;
    }

    /// <summary>
    /// Writes a <c>review batch</c> selection for the picked PRs, giving every repo all six lanes. The file
    /// is replaced atomically because a running batch re-reads it every few seconds.
    /// </summary>
    private static async Task WriteSelectionAsync(
        string path,
        IEnumerable<object> selected,
        string? maxAgeHours,
        DateTimeOffset observedAt,
        CancellationToken ct
    )
    {
        var lanes = new JsonObject();
        var repositories = new JsonObject();
        foreach (var row in selected.Select(row => JsonSerializer.SerializeToNode(row)!))
        {
            var repo = row["repository"]!.GetValue<string>();
            if (repositories[repo] is not JsonArray queue)
            {
                repositories[repo] = queue = [];
                lanes[repo] = new JsonArray(0, 1, 2, 3, 4, 5);
            }
            queue.Add(
                new JsonObject
                {
                    ["repository"] = repo,
                    ["prId"] = row["current"]!["PrId"]!.ToString(),
                    ["headSha"] = row["current"]!["HeadSha"]!.GetValue<string>(),
                }
            );
        }
        var selection = new JsonObject
        {
            ["requestId"] = $"pick-{observedAt:yyyyMMddHHmmss}",
            ["lanes"] = lanes,
            ["repositories"] = repositories,
        };
        if (maxAgeHours is not null)
            selection["maxAgeHours"] = int.Parse(maxAgeHours, System.Globalization.CultureInfo.InvariantCulture);
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(
            temporary,
            selection.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            ct
        );
        File.Move(temporary, path, overwrite: true);
    }

    // An attempt still in flight or retained keeps its PR out of a new selection. A verified review is settled,
    // and so is a failed-released attempt whose slot was released on its claim: the batch admits that one fresh,
    // so pick must offer it again or the PR is never reviewed.
    internal static bool IsSettled(JsonElement attempt) =>
        attempt.TryGetProperty("stage", out var stage)
        && (
            stage.GetString() == "completed-verified"
            || (
                stage.GetString() == "failed-released"
                && attempt.TryGetProperty("cleanupState", out var cleanup)
                && cleanup.GetString() == "released"
                && attempt.TryGetProperty("claimToken", out var claim)
                && !string.IsNullOrEmpty(claim.GetString())
            )
        );

    // The reaper and reclaim-slot release a slot mid-review and leave the attempt's stage as it was. Once the PR
    // has a new head that attempt reviewed older code: inspect refuses to publish it, so it can never finish, and
    // the new head is a fresh review. The same head stays out: its review may already have posted.
    internal static bool IsReleasedForEarlierHead(JsonElement attempt, string? currentHead) =>
        attempt.TryGetProperty("cleanupState", out var cleanup)
        && cleanup.GetString() == "released"
        && attempt.TryGetProperty("claimToken", out var claim)
        && !string.IsNullOrEmpty(claim.GetString())
        && attempt.TryGetProperty("headSha", out var head)
        && head.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(head.GetString())
        && !string.IsNullOrWhiteSpace(currentHead)
        && !string.Equals(head.GetString(), currentHead, StringComparison.OrdinalIgnoreCase);

    // A send that failed (a cold gateway answered 503) leaves its attempt at review-send-pending until the reaper
    // releases the slot, with no accepted input recorded. Nothing of it can have reached the PR unless the host
    // accepted the send after all; reviewIdempotencyKey names that send, and the runner asks the host about it
    // before admitting the PR again. An attempt with a recorded input stays out: its review may have posted.
    internal static bool HoldsNoAcceptedInput(JsonElement attempt)
    {
        return attempt.TryGetProperty("cleanupState", out var cleanup)
            && cleanup.GetString() == "released"
            && attempt.TryGetProperty("claimToken", out var claim)
            && !string.IsNullOrEmpty(claim.GetString())
            && !(attempt.TryGetProperty("stage", out var stage) && stage.GetString() == "completed-verified")
            && Absent("reviewInputId")
            && Absent("confirmationInputId")
            && Absent("confirmationIdempotencyKey");

        bool Absent(string name) =>
            !attempt.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null;
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
