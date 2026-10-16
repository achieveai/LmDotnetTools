using System.Globalization;
using System.Text.Json.Nodes;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Persistence.Models;
using Microsoft.Data.Sqlite;

namespace Reviewd.Sample;

/// <summary>
///     <c>review run</c> without <c>--slot</c>. Each allowed slot is tried once, in order: a free slot is used, a
///     stale owner is reclaimed through the existing cleanup-only path and its slot used, and a held slot is skipped
///     with its reason. Nothing is claimed for a skipped slot. A PR that already owns a slot keeps it.
/// </summary>
internal static class AutoSlot
{
    private static readonly string[] LiveBatchStatuses = ["queued", "launching", "retry"];

    internal static async Task<int> RunAsync(
        CliOptions options,
        ReviewdConfig config,
        LmStreamingS2SClient client,
        Func<string, JsonObject, string, CancellationToken, Task<JsonObject>> sandbox,
        CancellationToken ct,
        Func<CancellationToken, Task>? delay
    )
    {
        var repo = PrCommands.ParseRepo(options.Required("repo"));
        var pr = options.Required("pr");
        var database = options.Required("db");
        var workspace = options.Required("workspace");
        var execute = options.Has("execute");
        var staleAfter = TimeSpan.FromMinutes(
            options.Get("stale-after-minutes") is { } minutes ? int.Parse(minutes, CultureInfo.InvariantCulture) : 120
        );
        using var prLease = execute
            ? ReviewRunner.AcquireLease(database, ReviewRunner.PrLeaseIdentity(repo, pr))
            : null;
        var attempt = await ReadAsync(database, AttemptKey(repo, pr), ct);
        if (
            options.Has("resume")
            || options.Has("cleanup-only")
            || (attempt is not null && attempt["cleanupState"]?.GetValue<string>() != "released")
        )
        {
            // Recovery, or an attempt that still owns its slot, stays in the slot it recorded.
            var pinned =
                attempt?["slot"]?.GetValue<int>()
                ?? throw new InvalidOperationException("No retained attempt records this PR's slot; pass --slot.");
            return await ReviewRunner.RunInSlotAsync(
                options.With(("slot", pinned.ToString(CultureInfo.InvariantCulture))),
                config,
                client,
                sandbox,
                null,
                true,
                new JsonObject { ["mode"] = "pinned", ["slot"] = pinned },
                ct,
                delay
            );
        }
        var batchSlots = await LiveBatchSlotsAsync(database, workspace, repo, ct);
        var skipped = new JsonArray();
        foreach (var slot in CliOptions.ReviewSlots(options))
        {
            if (batchSlots.Contains(slot))
            {
                Skip(slot, "A queued batch entry holds this slot.");
                continue;
            }
            FileStream? lease;
            try
            {
                lease = execute
                    ? ReviewRunner.AcquireLease(database, ReviewRunner.SlotLeaseIdentity(workspace, repo, slot))
                    : null;
            }
            catch (IOException)
            {
                Skip(slot, "Another local run holds the slot lease.");
                continue;
            }
            using (lease)
            {
                var slotOptions = options.With(("slot", slot.ToString(CultureInfo.InvariantCulture)));
                var selection = new JsonObject
                {
                    ["mode"] = "auto",
                    ["slot"] = slot,
                    ["skipped"] = skipped.DeepClone(),
                };
                var (exit, held) = await TryRunAsync(slotOptions, lease, selection);
                if (exit is { } code)
                    return code;
                if (
                    await StaleOwnerAsync(database, repo, workspace, pr, slot, held!.Owner, staleAfter, ct) is { } stale
                )
                {
                    if (!execute)
                    {
                        selection["reclaim"] = stale;
                        Console.WriteLine(
                            new JsonObject
                            {
                                ["repo"] = repo.DisplayName,
                                ["pr"] = pr,
                                ["slot"] = slot,
                                ["workspace"] = workspace,
                                ["slotSelection"] = selection,
                                ["actions"] = new JsonArray(
                                    $"reclaim stale owner PR {stale} through cleanup-only",
                                    "then run this PR in the reclaimed slot"
                                ),
                            }.ToJsonString()
                        );
                        return 0;
                    }
                    try
                    {
                        // The slot lease stays held across the reclaim, so nothing local can take the slot.
                        await ReviewRunner.RunInSlotAsync(
                            slotOptions.With(
                                ("pr", stale),
                                ("cleanup-only", "true"),
                                ("resume", null),
                                ("head", null),
                                ("reasoning-effort", null)
                            ),
                            config,
                            client,
                            sandbox,
                            lease,
                            false,
                            null,
                            ct,
                            delay
                        );
                    }
                    catch (Exception failure) when (failure is not OperationCanceledException)
                    {
                        Skip(slot, $"Stale owner PR {stale} could not be reclaimed: {failure.Message}");
                        continue;
                    }
                    selection["reclaimed"] = stale;
                    (exit, held) = await TryRunAsync(slotOptions, lease, selection);
                    if (exit is { } reclaimed)
                        return reclaimed;
                }
                Skip(slot, held!.Message);
            }
        }
        Console.Error.WriteLine(
            new JsonObject
            {
                ["status"] = "no-slot",
                ["repository"] = repo.DisplayName,
                ["prId"] = pr,
                ["skipped"] = skipped,
            }.ToJsonString()
        );
        return ReviewRunner.NoSlotExitCode;

        void Skip(int slot, string reason) => skipped.Add(new JsonObject { ["slot"] = slot, ["reason"] = reason });

        async Task<(int? Exit, ReviewRunner.SlotUnavailableException? Held)> TryRunAsync(
            CliOptions slotOptions,
            FileStream? lease,
            JsonObject selection
        )
        {
            try
            {
                return (
                    await ReviewRunner.RunInSlotAsync(
                        slotOptions,
                        config,
                        client,
                        sandbox,
                        lease,
                        true,
                        selection,
                        ct,
                        delay
                    ),
                    null
                );
            }
            catch (ReviewRunner.SlotUnavailableException held)
            {
                return (null, held);
            }
        }
    }

    /// <summary>
    ///     The PR whose owner of <paramref name="slot"/> is stale and in a state cleanup-only accepts: a verified
    ///     review never released, or a failed original review. Unknown or in-flight stages are never stale.
    /// </summary>
    internal static async Task<string?> StaleOwnerAsync(
        string database,
        RepoIdentity repo,
        string workspace,
        string pr,
        int slot,
        JsonNode? owner,
        TimeSpan staleAfter,
        CancellationToken ct
    )
    {
        if (owner?["prId"]?.GetValue<string>() is not { } ownerPr || ownerPr == pr)
            return null;
        var attempt = await ReadAsync(database, AttemptKey(repo, ownerPr), ct);
        if (
            attempt is null
            || attempt["slot"]?.GetValue<int>() != slot
            || attempt["workspaceId"]?.GetValue<string>() != workspace
            || attempt["cleanupState"]?.GetValue<string>() != "owned"
        )
            return null;
        var stage = attempt["stage"]?.GetValue<string>();
        var cleanable =
            stage == "completed-verified"
            || (
                stage == "review-running"
                && attempt["reviewInputId"] is not null
                && attempt["confirmationInputId"] is null
                && attempt["confirmationIdempotencyKey"] is null
            );
        var stamps = new[] { "updatedAt", "finishedAt", "startedAt" }
            .Select(name =>
                attempt[name] is JsonValue value && value.TryGetValue<DateTimeOffset>(out var at)
                    ? at
                    : (DateTimeOffset?)null
            )
            .OfType<DateTimeOffset>()
            .ToArray();
        return cleanable && stamps.Length > 0 && DateTimeOffset.UtcNow - stamps.Max() >= staleAfter ? ownerPr : null;
    }

    private static async Task<HashSet<int>> LiveBatchSlotsAsync(
        string database,
        string workspace,
        RepoIdentity repo,
        CancellationToken ct
    )
    {
        var slots = new HashSet<int>();
        using var connection = Open(database);
        await connection.OpenAsync(ct);
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT value FROM cache WHERE key LIKE 'reviewd/batch-state/%'";
        using var reader = await query.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var state = JsonNode.Parse(reader.GetString(0));
            if (state?["scope"]?["workspace"]?.GetValue<string>() != workspace)
                continue;
            foreach (var entry in state["entries"]?.AsArray() ?? [])
            {
                if (
                    LiveBatchStatuses.Contains(entry?["status"]?.GetValue<string>())
                    && entry?["repo"]?.GetValue<string>() is { } entryRepo
                    && PrCommands.ParseRepo(entryRepo).NormalizedKey == repo.NormalizedKey
                    && entry["slot"]?.GetValue<int>() is { } slot
                )
                    slots.Add(slot);
            }
        }
        return slots;
    }

    private static string AttemptKey(RepoIdentity repo, string pr) =>
        $"reviewd/review-attempt/{repo.NormalizedKey}/{pr}";

    private static async Task<JsonNode?> ReadAsync(string database, string key, CancellationToken ct)
    {
        using var connection = Open(database);
        await connection.OpenAsync(ct);
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT value FROM cache WHERE key=@key";
        query.Parameters.AddWithValue("@key", key);
        return await query.ExecuteScalarAsync(ct) is string raw ? JsonNode.Parse(raw) : null;
    }

    private static SqliteConnection Open(string database) =>
        new(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly }.ToString());
}
