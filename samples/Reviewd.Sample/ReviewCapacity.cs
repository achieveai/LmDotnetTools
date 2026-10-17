using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Reviewd.Sample;

internal static class ReviewCapacity
{
    internal static async Task<JsonObject> PreviewAsync(
        string database,
        string workspace,
        string session,
        IReadOnlyList<string> repositories,
        JsonObject evidence,
        CancellationToken ct
    )
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly }.ToString()
        );
        await connection.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();
        using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT key,value FROM cache WHERE key LIKE 'reviewd/%'";
        var records = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var reader = await query.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                records.Add(reader.GetString(0), reader.GetString(1));
        var slots = new JsonArray();
        var lanes = new JsonObject();
        foreach (var repository in repositories)
        {
            var repo = PrCommands.ParseRepo(repository);
            var available = new JsonArray();
            lanes[repo.DisplayName] = available;
            for (var slot = 0; slot < 6; slot++)
            {
                var reasons = new HashSet<string>(StringComparer.Ordinal);
                var owners = new JsonArray();
                if (
                    records.TryGetValue(SlotRelease.OwnerKey(workspace, repo.NormalizedKey, slot), out var claimText)
                    && JsonNode.Parse(claimText)!["state"]?.GetValue<string>() != "released"
                )
                    reasons.Add("Durable current-owner claim remains held.");
                var native =
                    evidence["slots"]
                        ?.AsArray()
                        .Where(row =>
                            row?["repository"]?.GetValue<string>()
                                ?.Equals(repo.DisplayName, StringComparison.OrdinalIgnoreCase) == true
                            && row["slot"]?.GetValue<int>() == slot
                        )
                        .ToArray()
                    ?? [];
                if (
                    evidence["workspaceId"]?.GetValue<string>() != workspace
                    || evidence["sessionId"]?.GetValue<string>() != session
                    || evidence["sharedQuiescent"]?.GetValue<bool>() != true
                    || evidence["producerQuiescent"]?.GetValue<bool>() != true
                    || evidence["operationId"]?.GetValue<string>() is not { Length: > 0 }
                )
                    reasons.Add("Missing exact-workspace shared/native producer quiescence evidence.");
                if (
                    native.Length != 1
                    || native[0]?["nativeQuiescent"]?.GetValue<bool>() != true
                    || native[0]?["artifactsRetained"]?.GetValue<bool>() != true
                )
                    reasons.Add("Missing unique native slot quiescence and artifact-retention evidence.");
                var settledThreads = native.Length == 1 ? native[0]?["quiescentThreads"]?.AsArray() : null;
                foreach (
                    var record in records.Where(row =>
                        row.Key.StartsWith("reviewd/review-attempt/", StringComparison.Ordinal)
                        || row.Key.StartsWith("reviewd/review-run/", StringComparison.Ordinal)
                    )
                )
                {
                    var owner = JsonNode.Parse(record.Value)!;
                    if (
                        owner["workspaceId"]?.GetValue<string>() != workspace
                        || owner["repository"]
                            ?.GetValue<string>()
                            ?.Equals(repo.DisplayName, StringComparison.OrdinalIgnoreCase) != true
                        || owner["slot"]?.GetValue<int>() != slot
                    )
                        continue;
                    owners.Add(
                        new JsonObject
                        {
                            ["key"] = record.Key,
                            ["attemptDigest"] = SlotRelease.Digest(record.Value),
                            ["prId"] = owner["prId"]?.DeepClone(),
                            ["stage"] = owner["stage"]?.DeepClone(),
                        }
                    );
                    var pr = owner["prId"]?.GetValue<string>();
                    var attemptKey = $"reviewd/review-attempt/{repo.NormalizedKey}/{pr}";
                    var receiptKey = $"reviewd/review-run/{repo.NormalizedKey}/{pr}";
                    var completed =
                        records.TryGetValue(attemptKey, out var attemptText)
                        && records.TryGetValue(receiptKey, out var receiptText)
                        && VerifiedPair(JsonNode.Parse(attemptText)!, JsonNode.Parse(receiptText)!, workspace, slot)
                        && JsonNode.Parse(attemptText)!["cleanupState"]?.GetValue<string>() == "released"
                        && JsonNode.Parse(attemptText)!["claimToken"]?.GetValue<string>() is { Length: > 0 };
                    var adopted =
                        records.TryGetValue(
                            "reviewd/legacy-cleaned/" + SlotRelease.Digest(record.Value),
                            out var adoptedText
                        )
                        && JsonNode.Parse(adoptedText)!["cleanupState"]?.GetValue<string>() == "released";
                    if (
                        !completed
                        && !(
                            owner["stage"]?.GetValue<string>() == "failed-released"
                            && owner["cleanupState"]?.GetValue<string>() == "released"
                            && owner["claimToken"]?.GetValue<string>() is { Length: > 0 }
                        )
                        && !adopted
                        && (
                            !records.TryGetValue(SlotRelease.Key(record.Value), out var proofText)
                            || !SlotRelease.Allows(JsonNode.Parse(proofText)!, record.Value, "different-pr", session)
                        )
                    )
                        reasons.Add("Retained incomplete owner " + pr + " has no exact reusable-slot certificate.");
                    if (
                        owner["threadId"]?.GetValue<string>() is { } thread
                        && settledThreads?.Any(value => value?.GetValue<string>() == thread) != true
                    )
                        reasons.Add("Owner thread " + thread + " lacks current quiescence evidence.");
                }
                foreach (
                    var record in records.Where(row =>
                        row.Key.StartsWith("reviewd/batch-state/", StringComparison.Ordinal)
                    )
                )
                {
                    var batch = JsonNode.Parse(record.Value)!;
                    if (batch["scope"]?["workspace"]?.GetValue<string>() != workspace)
                        continue;
                    foreach (var entry in batch["entries"]?.AsArray() ?? [])
                        if (
                            entry
                                ?["repo"]?.GetValue<string>()
                                ?.Equals(repo.DisplayName, StringComparison.OrdinalIgnoreCase) == true
                            && entry["slot"]?.GetValue<int>() == slot
                            && entry["status"]?.GetValue<string>() is not ("verified" or "closed" or "stale")
                            && !(
                                records.TryGetValue(
                                    $"reviewd/review-attempt/{repo.NormalizedKey}/{entry["pr"]}",
                                    out var retiredAttempt
                                )
                                && records.TryGetValue(SlotRelease.Key(retiredAttempt), out var retirement)
                                && SlotRelease.IsResetRetirement(JsonNode.Parse(retirement)!, retiredAttempt, session)
                                && SlotRelease.Allows(
                                    JsonNode.Parse(retirement)!,
                                    retiredAttempt,
                                    "different-pr",
                                    session
                                )
                            )
                        )
                            reasons.Add("Historical batch still assigns PR " + entry["pr"] + " to this lane.");
                }
                if (reasons.Count == 0)
                    available.Add(slot);
                slots.Add(
                    new JsonObject
                    {
                        ["repository"] = repo.DisplayName,
                        ["slot"] = slot,
                        ["status"] = reasons.Count == 0 ? "candidate" : "held",
                        ["reasons"] = new JsonArray(
                            reasons.Select(reason => (JsonNode)JsonValue.Create(reason)!).ToArray()
                        ),
                        ["owners"] = owners,
                    }
                );
            }
        }
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["previewOnly"] = true,
            ["workspaceId"] = workspace,
            ["sessionId"] = session,
            ["slots"] = slots,
            ["lanes"] = lanes,
            ["boundary"] =
                "Evidence is supplied, not independently certified by this planner. Candidates require fresh authorization/preflight and a different PR/session for certified releases. No reservation, reset or reassignment performed.",
        };
    }

    private static bool VerifiedPair(JsonNode attempt, JsonNode receipt, string workspace, int slot) =>
        attempt["stage"]?.GetValue<string>() == "completed-verified"
        && receipt["stage"]?.GetValue<string>() == "completed-verified"
        && attempt["workspaceId"]?.GetValue<string>() == workspace
        && receipt["workspaceId"]?.GetValue<string>() == workspace
        && attempt["slot"]?.GetValue<int>() == slot
        && receipt["slot"]?.GetValue<int>() == slot
        && attempt["repository"]?.GetValue<string>() == receipt["repository"]?.GetValue<string>()
        && attempt["prId"]?.GetValue<string>() == receipt["prId"]?.GetValue<string>()
        && attempt["headSha"]?.GetValue<string>() is { Length: 40 } head
        && receipt["reviewedHeadSha"]?.GetValue<string>() == head
        && attempt["artifactCommit"]?["status"]?.GetValue<string>() == "committed"
        && receipt["artifactCommit"]?["status"]?.GetValue<string>() == "committed";
}
