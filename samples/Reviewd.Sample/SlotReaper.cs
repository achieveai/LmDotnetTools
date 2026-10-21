using System.Text.Json;
using System.Text.Json.Nodes;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Persistence.Models;
using Microsoft.Data.Sqlite;

namespace Reviewd.Sample;

/// <summary>
/// Guarantees the "a slot is never blocked forever" invariant. Every slot claim is leased
/// (<see cref="SlotRelease.DefaultLeaseWindow"/>) and a running review extends its lease each phase;
/// a dead, failed, or SIGKILL'd review stops extending it, so the lease lapses. This reaper sweeps
/// the owner rows, and for any owned slot whose lease has lapsed it runs the SAME safe reclaim the
/// operator <c>review reclaim-slot</c> uses — freeze the owning conversation, confirm it has
/// quiesced (a still-live review will not, so it is left alone and unfrozen), discard the prepared
/// native slot, then CAS-release the SQLite owner. The reclaim core lives here so the manual command
/// and the automatic sweep share one implementation and cannot drift. The host client and the sandbox
/// executor are injected so the whole sweep is testable without a real host or sandbox.
/// </summary>
internal static class SlotReaper
{
    /// <summary>Runs one sandbox phase (<c>phase</c>, <c>args</c>, <c>operation</c>, <c>ct</c>) — the same
    /// shape <see cref="ReviewRunner"/> injects, so tests supply a fake and production binds the live session.</summary>
    internal delegate Task<JsonObject> SandboxExecutor(
        string phase,
        JsonObject args,
        string operation,
        CancellationToken ct
    );

    /// <summary>One slot's reclaim outcome, surfaced for the command's JSON and the sweep's summary.</summary>
    internal sealed record ReclaimResult(
        string Repo,
        string Pr,
        int Slot,
        string? ThreadId,
        string Freeze,
        string Status,
        string? Operation = null,
        string? Detail = null
    );

    /// <summary>
    /// Reads and validates the retained attempt backing a slot. Returns a terminal <c>Status</c> with a
    /// null attempt when there is nothing to reclaim (missing attempt, no claim token, slot/workspace
    /// mismatch, or already released), so both callers short-circuit identically.
    /// </summary>
    internal static async Task<(JsonObject? Attempt, string? ClaimToken, ReclaimResult? Terminal)> PrepareAsync(
        SqliteConnection connection,
        RepoIdentity repo,
        string pr,
        int slot,
        string workspace,
        CancellationToken ct
    )
    {
        var attemptKey = $"reviewd/review-attempt/{repo.NormalizedKey}/{pr}";
        var attempt = await ReadAsync(connection, attemptKey, ct);
        ReclaimResult Terminal(string status) => new(repo.DisplayName, pr, slot, null, "n/a", status);
        if (attempt is null)
            return (null, null, Terminal("no-attempt"));
        if (attempt["claimToken"]?.GetValue<string>() is not { Length: > 0 } token)
            return (null, null, Terminal("no-claim-token"));
        if (attempt["slot"]?.GetValue<int>() != slot || attempt["workspaceId"]?.GetValue<string>() != workspace)
            return (null, null, Terminal("slot-workspace-mismatch"));
        if (attempt["cleanupState"]?.GetValue<string>() == "released")
            return (null, null, Terminal("already-released"));
        return (attempt, token, null);
    }

    /// <summary>
    /// Freeze → confirm quiescence → discard-reset the native slot → CAS-release the SQLite owner, for a
    /// single validated attempt. A conversation the host does not know is treated as already stopped. A
    /// conversation that will not quiesce within the deadline is still live: it is unfrozen and the method
    /// throws, so the caller skips it rather than discarding a running review. With <paramref name="execute"/>
    /// false only quiescence is inspected (the live conversation is restored) and nothing is reset.
    /// </summary>
    internal static async Task<ReclaimResult> ReclaimOneAsync(
        SqliteConnection connection,
        LmStreamingS2SClient client,
        SandboxExecutor sandbox,
        RepoIdentity repo,
        string pr,
        int slot,
        string workspace,
        JsonObject attempt,
        string claimToken,
        bool execute,
        CancellationToken ct,
        bool force = false,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<CancellationToken, Task<bool?>>? recheck = null
    )
    {
        delay ??= Task.Delay;
        var attemptKey = $"reviewd/review-attempt/{repo.NormalizedKey}/{pr}";
        var threadId = attempt["threadId"]?.GetValue<string>();
        var head =
            attempt["headSha"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Attempt has no head sha; refusing to reclaim.");

        // 0) Read-only liveness pre-check. Freezing is the disturbance; passive GETs are not. If the root
        //    still has a run, or any descendant has not settled, it is a live review (for example a legacy
        //    undated owner swept under --include-undated, the active session, or a background child still
        //    writing after its root went idle). Leave it wholly untouched — never freeze it — so in-flight
        //    work is not interrupted. A later sweep retries once the whole tree goes quiet.
        if (threadId is { Length: > 0 } && await FindActivityAsync(client, threadId, delay, ct) is { } liveActivity)
            return new ReclaimResult(
                repo.DisplayName,
                pr,
                slot,
                threadId,
                "not-frozen",
                "skipped",
                Detail: $"{liveActivity}; left untouched (not frozen)."
            );
        if (!await StillOursAsync())
            return Moved("not-frozen");

        // 1) Freeze the conversation we own. Present == false means the host does not know it — not
        //    running there, which for a reclaim is the same end state as frozen.
        string freezeState;
        var frozenActive = false;
        if (threadId is { Length: > 0 })
        {
            var frozen = await client.SetFrozenAsync(threadId, frozen: true, ct);
            if (frozen.Refusal is { } refusal)
                throw new InvalidOperationException($"Host refused to freeze {threadId}: {refusal}.");
            freezeState =
                !frozen.Present ? "absent-on-host"
                : frozen.Frozen ? "frozen"
                : "freeze-acknowledged";
            frozenActive = frozen.Present;
        }
        else
        {
            freezeState = "no-thread";
        }

        // The sweep chose this owner minutes ago and each activity read can wait out a busy host, so the owner row is
        // re-read before the conversation is frozen and again before the slot is reset. A lane that renewed or
        // re-claimed the slot meanwhile, or another PR that claimed it, is left alone, and force is recomputed from
        // the row as it is now.
        async Task<bool> StillOursAsync()
        {
            if (recheck is null)
                return true;
            if (await recheck(ct) is not { } forceNow)
                return false;
            force = forceNow;
            return true;
        }

        ReclaimResult Moved(string freeze) =>
            new(
                repo.DisplayName,
                pr,
                slot,
                threadId,
                freeze,
                "skipped",
                Detail: "The slot owner changed while the reaper waited (renewed, re-claimed or released); left untouched."
            );

        async Task RestoreIfFrozenAsync()
        {
            if (frozenActive && threadId is { Length: > 0 })
            {
                try
                {
                    _ = await client.SetFrozenAsync(threadId, frozen: false, CancellationToken.None);
                }
                catch
                {
                    // Best effort only; the abort/throw already carries the real outcome.
                }
            }
        }

        try
        {
            // 2) Confirm the frozen conversation and every descendant are quiet. Freezing stops only the
            //    root, so a running child keeps the tree busy until it finishes. If it will not quiesce
            //    within the deadline it was live, not dead — abort (the catch unfreezes it so it continues).
            if (frozenActive)
            {
                var deadline = DateTimeOffset.UtcNow + QuiescenceDeadline;
                while (await FindActivityAsync(client, threadId!, delay, ct) is { } activity)
                {
                    if (DateTimeOffset.UtcNow >= deadline)
                        throw new InvalidOperationException(
                            $"Conversation {threadId} did not quiesce within {QuiescenceDeadline.TotalSeconds:0}s of "
                                + $"freezing ({activity}); slot not reclaimed."
                        );
                    await Task.Delay(QuiescencePoll, ct);
                }
            }

            if (!execute)
            {
                await RestoreIfFrozenAsync();
                return new ReclaimResult(repo.DisplayName, pr, slot, threadId, freezeState, "would-reclaim");
            }

            if (!await StillOursAsync())
            {
                await RestoreIfFrozenAsync();
                return Moved(freezeState);
            }

            // 3) Discard-reset the native slot (release the owner marker, discard the prepared slot/pool run). The reset
            //    renews the lease while it runs, so another reaper reading the same lapsed lease leaves the slot alone.
            var ownerKey = SlotRelease.OwnerKey(workspace, repo.NormalizedKey, slot);
            var operation = Guid.NewGuid().ToString("N");
            var args = new JsonObject
            {
                ["org"] = repo.OrgOrOwner,
                ["project"] = repo.Project,
                ["repo"] = repo.RepoName,
                ["pr"] = pr,
                ["slot"] = slot,
                ["head"] = head,
                ["claimToken"] = claimToken,
                ["discard"] = true,
                ["force"] = force,
            };
            var result = await ReviewRunner.RunLeasedStepAsync(
                connection,
                ownerKey,
                claimToken,
                threadId is { Length: > 0 }
                    ? async token => await FindActivityAsync(client, threadId, delay, token) is not null
                    : null,
                (phase, stepArgs, stepOperation, token) => sandbox(phase, stepArgs, stepOperation, token),
                "cleanup",
                args,
                operation,
                ct
            );
            if (
                result["claimToken"]?.GetValue<string>() != claimToken
                || result["cleanBaselineVerified"]?.GetValue<bool>() != true
            )
                throw new InvalidOperationException(
                    "Discard-reset postconditions were not verified; slot remains claimed."
                );

            // 4) Release the SQLite owner on the exact claim token, stamping the attempt released.
            attempt["reclaim"] = new JsonObject { ["freeze"] = freezeState, ["operation"] = operation };
            // A review whose publication was never verified becomes a failed admission once its slot is reset: the next
            // pick admits the PR again in its own conversation, which checks what already posted (Kay9: review it again
            // rather than skip it until a new push; seen live: 12 open PRs parked that way, and prepares that failed
            // holding their slot). A verified publication keeps its stage; only its artifact commit is missing. The
            // runner saves verify-completed before it stores the verify result, so that stage counts as verified too.
            if (
                attempt["verifiedPublication"] is null
                && attempt["stage"]?.GetValue<string>()
                    is not ("verify-completed" or "commit-pending" or "commit-completed" or "completed-verified")
            )
            {
                attempt["stageBeforeReclaim"] = attempt["stage"]?.DeepClone();
                attempt["stage"] = "failed-released";
                attempt["resetCount"] = (attempt["resetCount"]?.GetValue<int>() ?? 0) + 1;
            }
            await SlotRelease.ReleaseAsync(connection, ownerKey, claimToken, attemptKey, attempt, ct);

            var forced = result["forced"] is JsonArray { Count: > 0 } overrides
                ? "forced: " + string.Join("; ", overrides.Select(o => o!.GetValue<string>()))
                : null;
            // Deliberately leave the host agent frozen — it has no slot to resume into, and host-side
            // eviction reclaims the in-memory instance.
            return new ReclaimResult(repo.DisplayName, pr, slot, threadId, freezeState, "reclaimed", operation, forced);
        }
        catch
        {
            await RestoreIfFrozenAsync();
            throw;
        }
    }

    // A live lane renews its lease every step, so a lease lapsed this long means no lane has watched the slot for an
    // hour: only our own bookkeeping still holds it. From here a reclaim forces through any disagreement between the
    // sandbox's records and ours. It still never resets while an agent on the conversation is active.
    internal static readonly TimeSpan ForceAfterLapse = TimeSpan.FromMinutes(30);

    internal static bool IsForceDue(JsonObject owner, DateTimeOffset now) =>
        owner["leaseExpiresAt"]?.GetValue<DateTimeOffset>() is not { } expiry || now - expiry >= ForceAfterLapse;

    // How long a frozen conversation's tree gets to settle before the reclaim gives up on it as live.
    private static readonly TimeSpan QuiescenceDeadline = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan QuiescencePoll = TimeSpan.FromSeconds(1);

    // The host answers hierarchy_busy while any conversation's metadata publication is pending, which on a host shared
    // by 22 batches is most of the time (measured: 19 of 30 reads, with quiet gaps between). Busy means unknown, never
    // quiet: re-read every 5 seconds for up to two minutes to catch a gap, then leave it to the next sweep.
    internal const int BusyRereads = 24;
    internal static readonly TimeSpan BusyReread = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Describes why <paramref name="threadId"/>'s tree is not quiet, or returns null when it is: the root
    /// has no run in progress or pending, and every descendant has settled by the same rule the review
    /// wait uses (<see cref="ReviewRunner.IsSettledNode"/>). A busy hierarchy means the descendants are
    /// unknown, which is never quiet. A thread the host does not know at all has nothing running.
    /// </summary>
    internal static async Task<string?> FindActivityAsync(
        LmStreamingS2SClient client,
        string threadId,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken ct
    )
    {
        var root = await client.GetRunStateAsync(threadId, ct);
        if (root.IsInProgress || root.CurrentRunId is not null)
            return "Conversation is in progress";
        ReviewSubAgentTreeSnapshot? tree = null;
        var busyReads = 0;
        while (tree is null)
        {
            try
            {
                tree = await client.GetSubAgentTreeAsync(threadId, ct);
            }
            catch (LmStreamingS2SClient.HierarchyBusyException) when (busyReads < BusyRereads)
            {
                busyReads++;
                await delay(BusyReread, ct);
            }
            catch (LmStreamingS2SClient.HierarchyBusyException)
            {
                return "Host hierarchy is busy, so descendants are unknown";
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }
        }
        // A run can start while the tree is busy, so a quiet verdict never rests on a root read older than the tree.
        if (busyReads > 0)
        {
            root = await client.GetRunStateAsync(threadId, ct);
            if (root.IsInProgress || root.CurrentRunId is not null)
                return "Conversation is in progress";
        }
        var now = DateTimeOffset.UtcNow;
        var active = tree.Nodes.Where(n => !ReviewRunner.IsSettledNode(n, now)).ToList();
        return active.Count == 0
            ? null
            : $"{active.Count} descendant(s) still active ({string.Join(", ", active.Select(n => $"{n.AgentId}={n.Status}"))})";
    }

    /// <summary>
    /// Sweeps every slot owner in <paramref name="workspace"/> and reclaims the ones whose lease has
    /// lapsed. A still-live review (its lease fresh, or it refuses to quiesce) is skipped, never
    /// discarded. Per-owner failures are caught and recorded so one wedged slot never stops the sweep.
    /// Owners in other workspaces are ignored — only the live session's own workspace can discard its
    /// native markers.
    /// </summary>
    internal static async Task<IReadOnlyList<ReclaimResult>> SweepAsync(
        SqliteConnection connection,
        LmStreamingS2SClient client,
        SandboxExecutor sandbox,
        string workspace,
        DateTimeOffset now,
        bool undatedExpired,
        bool execute,
        CancellationToken ct,
        Func<TimeSpan, CancellationToken, Task>? delay = null
    )
    {
        var owners = new List<(RepoIdentity Repo, string Pr, int Slot, bool Force)>();
        using (var scan = connection.CreateCommand())
        {
            scan.CommandText = "SELECT value FROM cache WHERE key LIKE @prefix";
            scan.Parameters.AddWithValue("@prefix", SlotRelease.OwnerKeyPrefix + "%");
            using var reader = await scan.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (JsonNode.Parse(reader.GetString(0)) is not JsonObject owner)
                    continue;
                if (owner["workspaceId"]?.GetValue<string>() != workspace)
                    continue;
                if (!SlotRelease.IsLeaseExpired(owner, now, undatedExpired))
                    continue;
                if (
                    owner["repository"]?.GetValue<string>() is not { Length: > 0 } repository
                    || owner["prId"]?.GetValue<string>() is not { Length: > 0 } pr
                    || owner["slot"]?.GetValue<int>() is not { } slot
                )
                    continue;
                owners.Add((PrCommands.ParseRepo(repository), pr, slot, IsForceDue(owner, now)));
            }
        }

        var results = new List<ReclaimResult>();
        foreach (var (repo, pr, slot, force) in owners)
        {
            try
            {
                var (attempt, token, terminal) = await PrepareAsync(connection, repo, pr, slot, workspace, ct);
                if (terminal is not null)
                {
                    results.Add(terminal);
                    continue;
                }
                results.Add(
                    await ReclaimOneAsync(
                        connection,
                        client,
                        sandbox,
                        repo,
                        pr,
                        slot,
                        workspace,
                        attempt!,
                        token!,
                        execute,
                        ct,
                        force,
                        delay,
                        async cancel =>
                        {
                            var owner = await ReadAsync(
                                connection,
                                SlotRelease.OwnerKey(workspace, repo.NormalizedKey, slot),
                                cancel
                            );
                            var stillLapsed =
                                owner is not null
                                && owner["claimToken"]?.GetValue<string>() == token
                                && SlotRelease.IsLeaseExpired(owner, DateTimeOffset.UtcNow, undatedExpired);
                            return stillLapsed ? IsForceDue(owner!, DateTimeOffset.UtcNow) : null;
                        }
                    )
                );
            }
            catch (Exception ex)
            {
                // A slot that refuses to quiesce (still live) or any other reclaim error is recorded and
                // skipped so the sweep continues; the next sweep retries it while its lease stays lapsed.
                results.Add(new ReclaimResult(repo.DisplayName, pr, slot, null, "n/a", "skipped", Detail: ex.Message));
            }
        }
        return results;
    }

    /// <summary>
    /// <c>review reap-slots</c>: reclaim every slot in a workspace whose lease has lapsed. Dead/failed
    /// reviews are freed automatically; <c>--include-undated</c> also reclaims legacy owners claimed
    /// before leasing existed (the 3 jammed repos). Dry run (omit <c>--execute</c>) only inspects.
    /// </summary>
    public static async Task<int> RunAsync(CliOptions options, ReviewdConfig config, CancellationToken ct)
    {
        var database = options.Required("db");
        var workspace = options.Required("workspace");
        var session = options.Required("session");
        var execute = options.Has("execute");
        var undatedExpired = options.Has("include-undated");

        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Mode = execute ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly,
            }.ToString()
        );
        await connection.OpenAsync(ct);

        var client = config.CreateS2SClient();
        using var sandboxClient = config.CreateSandboxClient(TimeSpan.FromMinutes(10));
        SandboxExecutor sandbox = (phase, args, operation, token) =>
            ReviewRunner.ExecuteSandboxAsync(sandboxClient, session, phase, args, operation, token);

        var results = await SweepAsync(
            connection,
            client,
            sandbox,
            workspace,
            DateTimeOffset.UtcNow,
            undatedExpired,
            execute,
            ct
        );
        Console.WriteLine(
            JsonSerializer.Serialize(
                new
                {
                    workspace,
                    execute,
                    includeUndated = undatedExpired,
                    reclaimed = results.Count(r => r.Status == "reclaimed"),
                    skipped = results.Count(r => r.Status == "skipped"),
                    results,
                }
            )
        );
        return 0;
    }

    private static async Task<JsonObject?> ReadAsync(SqliteConnection connection, string key, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM cache WHERE key=@key";
        _ = command.Parameters.AddWithValue("@key", key);
        return await command.ExecuteScalarAsync(ct) is string raw ? JsonNode.Parse(raw)?.AsObject() : null;
    }
}
