using System.Globalization;
using System.Text.Json.Nodes;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Persistence.Models;
using Microsoft.Data.Sqlite;

namespace Reviewd.Sample;

/// <summary>
///     <c>review run</c> without <c>--slot</c>. Each allowed slot is tried once, in order: a free slot is used, an
///     owner whose lease has lapsed is reclaimed by the reaper's own reclaim and its slot used, and a held slot is
///     skipped with its reason. Nothing is claimed for a skipped slot. A PR that already owns a slot keeps it.
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
        Func<CancellationToken, Task>? delay,
        Func<DateTimeOffset>? now
    )
    {
        var repo = PrCommands.ParseRepo(options.Required("repo"));
        var pr = options.Required("pr");
        var database = options.Required("db");
        var workspace = options.Required("workspace");
        var execute = options.Has("execute");
        now ??= () => DateTimeOffset.UtcNow;
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
                delay,
                now
            );
        }
        var batchSlots = await LiveBatchSlotsAsync(database, workspace, repo, ct);
        var skipped = new JsonArray();
        foreach (var slot in CliOptions.ReviewSlots(options, config.LanesPerRepo))
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
                if (await LapsedOwnerAsync(database, repo, workspace, pr, slot, now(), ct) is { } stale)
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
                                    $"reclaim PR {stale}'s lapsed claim as reap-slots does (freeze, confirm quiesced, discard, release)",
                                    "then run this PR in the reclaimed slot"
                                ),
                            }.ToJsonString()
                        );
                        return 0;
                    }
                    // The slot lease stays held across the reclaim, so nothing local can take the slot.
                    if (await ReclaimAsync(stale, slot) is { } refusal)
                    {
                        Skip(slot, $"Lapsed owner PR {stale} was not reclaimed: {refusal}");
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
                        delay,
                        now
                    ),
                    null
                );
            }
            catch (ReviewRunner.SlotUnavailableException held)
            {
                return (null, held);
            }
        }

        // Null when the slot was reclaimed, else why not (a live review refuses to quiesce, for example).
        async Task<string?> ReclaimAsync(string ownerPr, int slot)
        {
            try
            {
                await using var connection = new SqliteConnection(
                    new SqliteConnectionStringBuilder
                    {
                        DataSource = database,
                        Mode = SqliteOpenMode.ReadWrite,
                    }.ToString()
                );
                await connection.OpenAsync(ct);
                var (ownerAttempt, token, terminal) = await SlotReaper.PrepareAsync(
                    connection,
                    repo,
                    ownerPr,
                    slot,
                    workspace,
                    ct
                );
                if (terminal is not null)
                    return terminal.Status;
                var result = await SlotReaper.ReclaimOneAsync(
                    connection,
                    client,
                    (phase, args, operation, cancel) => sandbox(phase, args, operation, cancel),
                    repo,
                    ownerPr,
                    slot,
                    workspace,
                    ownerAttempt!,
                    token!,
                    execute: true,
                    ct
                );
                return result.Status == "reclaimed" ? null : result.Detail ?? result.Status;
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                return failure.Message;
            }
        }
    }

    /// <summary>
    ///     The other PR whose claim on <paramref name="slot"/> has a lapsed lease, the same test the reaper uses.
    ///     An owner claimed before leasing existed is never lapsed here; <c>review reap-slots --include-undated</c>
    ///     reclaims those.
    /// </summary>
    internal static async Task<string?> LapsedOwnerAsync(
        string database,
        RepoIdentity repo,
        string workspace,
        string pr,
        int slot,
        DateTimeOffset now,
        CancellationToken ct
    ) =>
        await ReadAsync(database, SlotRelease.OwnerKey(workspace, repo.NormalizedKey, slot), ct) is JsonObject owner
        && owner["prId"]?.GetValue<string>() is { } ownerPr
        && ownerPr != pr
        && SlotRelease.IsLeaseExpired(owner, now)
            ? ownerPr
            : null;

    internal static async Task<HashSet<int>> LiveBatchSlotsAsync(
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

    internal static async Task<JsonNode?> ReadAsync(string database, string key, CancellationToken ct)
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
