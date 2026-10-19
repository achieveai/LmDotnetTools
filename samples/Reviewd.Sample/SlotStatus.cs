using System.Text.Json.Nodes;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace Reviewd.Sample;

/// <summary>
///     <c>review slots</c>: a read-only view of each repository's slots, from the database alone. A slot is
///     <c>free</c> (no owner, or its owner released it), <c>queued</c> (a live batch entry will run in it),
///     <c>owned</c> (its owner's lease is current, or it was claimed before leases existed), or <c>expired</c> (its
///     owner's lease lapsed, so an automatic run reclaims it). Local run locks and the sandbox are not probed, so
///     prepare can still refuse a free slot whose native worktree is off the idle baseline.
/// </summary>
internal static class SlotStatus
{
    internal static async Task<int> RunAsync(
        CliOptions options,
        ReviewdConfig config,
        DateTimeOffset now,
        CancellationToken ct
    )
    {
        var rows = await ReadAsync(
            options.Required("db"),
            options.Required("workspace"),
            PrCommands.ParseRepos(options),
            CliOptions.ReviewSlots(options, config.LanesPerRepo),
            now,
            ct
        );
        foreach (var row in rows)
            Console.WriteLine(row.ToJsonString());
        return 0;
    }

    internal static async Task<List<JsonObject>> ReadAsync(
        string database,
        string workspace,
        IEnumerable<RepoIdentity> repos,
        int[] slots,
        DateTimeOffset now,
        CancellationToken ct
    )
    {
        var rows = new List<JsonObject>();
        foreach (var repo in repos)
        {
            var queued = await AutoSlot.LiveBatchSlotsAsync(database, workspace, repo, ct);
            foreach (var slot in slots)
            {
                var owner = await AutoSlot.ReadAsync(
                    database,
                    SlotRelease.OwnerKey(workspace, repo.NormalizedKey, slot),
                    ct
                );
                rows.Add(Describe(repo, slot, owner as JsonObject, queued.Contains(slot), now));
            }
        }
        return rows;
    }

    internal static JsonObject Describe(RepoIdentity repo, int slot, JsonObject? owner, bool queued, DateTimeOffset now)
    {
        var owned = owner?["state"]?.GetValue<string>() == "owned";
        var row = new JsonObject
        {
            ["repository"] = repo.DisplayName,
            ["slot"] = slot,
            ["state"] =
                owned ? (SlotRelease.IsLeaseExpired(owner!, now) ? "expired" : "owned")
                : queued ? "queued"
                : "free",
        };
        foreach (var field in owned ? new[] { "prId", "headSha", "startedAt", "leaseExpiresAt" } : [])
        {
            if (owner![field] is { } value)
                row[field] = value.DeepClone();
        }
        return row;
    }
}
