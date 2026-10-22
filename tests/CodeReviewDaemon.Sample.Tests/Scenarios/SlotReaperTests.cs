extern alias Reviewd;

using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.Misc.Storage;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Reviewd::Reviewd.Sample;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// The slot-never-blocks invariant: a dead/failed/SIGKILL'd review must never keep a slot owned.
/// Claims are leased; a live review heartbeats the lease; the reaper reclaims any owned slot whose
/// lease has lapsed via the same safe freeze→discard→release path the operator command uses.
/// </summary>
public sealed class SlotReaperTests
{
    [Fact]
    public void Lease_expiry_honours_state_and_undated_owners()
    {
        var now = DateTimeOffset.UtcNow;
        var past = now - TimeSpan.FromMinutes(1);
        var future = now + TimeSpan.FromMinutes(1);

        SlotRelease
            .IsLeaseExpired(new JsonObject { ["state"] = "owned", ["leaseExpiresAt"] = past }, now)
            .Should()
            .BeTrue();
        SlotRelease
            .IsLeaseExpired(new JsonObject { ["state"] = "owned", ["leaseExpiresAt"] = future }, now)
            .Should()
            .BeFalse("a live lease is not reclaimable");
        SlotRelease
            .IsLeaseExpired(new JsonObject { ["state"] = "released", ["leaseExpiresAt"] = past }, now)
            .Should()
            .BeFalse("a released slot is not owned");
        SlotRelease
            .IsLeaseExpired(new JsonObject { ["state"] = "owned" }, now)
            .Should()
            .BeFalse("an undated owner is ignored by a routine sweep");
        SlotRelease
            .IsLeaseExpired(new JsonObject { ["state"] = "owned" }, now, undatedExpired: true)
            .Should()
            .BeTrue("an opt-in sweep reclaims legacy owners with no lease");
    }

    [Fact]
    public async Task Claim_stamps_a_lease_and_heartbeat_extends_it()
    {
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-lease-{Guid.NewGuid():N}.db");
        try
        {
            using var connection = PrThreadBindings.Connection(database);
            var store = new SqliteKvStore(connection);
            await connection.OpenAsync();

            var key = SlotRelease.OwnerKey("workspace", "repo", 0);
            var claimedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(2);
            var token = await SlotRelease.ClaimAsync(
                connection,
                key,
                new JsonObject { ["prId"] = "17", ["sessionId"] = "session" },
                null,
                "reviewd/review-attempt/repo/17",
                CancellationToken.None,
                claimedAt
            );

            var owner = (await store.GetAsync<JsonObject>(key))!;
            owner["leaseExpiresAt"]!
                .GetValue<DateTimeOffset>()
                .Should()
                .BeCloseTo(claimedAt + SlotRelease.DefaultLeaseWindow, TimeSpan.FromSeconds(1));
            SlotRelease.IsLeaseExpired(owner, DateTimeOffset.UtcNow).Should().BeTrue("the claim is two hours old");

            var beat = DateTimeOffset.UtcNow;
            (await SlotRelease.HeartbeatAsync(connection, key, token, CancellationToken.None, beat)).Should().BeTrue();
            var refreshed = (await store.GetAsync<JsonObject>(key))!;
            SlotRelease
                .IsLeaseExpired(refreshed, DateTimeOffset.UtcNow)
                .Should()
                .BeFalse("the heartbeat pushed the lease back into the future");

            (await SlotRelease.HeartbeatAsync(connection, key, "wrong-token", CancellationToken.None))
                .Should()
                .BeFalse("a lost claim never turns a heartbeat into a failure");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }

    [Fact]
    public async Task Reaper_reclaims_an_expired_slot_and_frees_it_for_a_fresh_claim()
    {
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-reap-{Guid.NewGuid():N}.db");
        try
        {
            using var connection = PrThreadBindings.Connection(database);
            var store = new SqliteKvStore(connection);
            await connection.OpenAsync();

            var repo = PrCommands.ParseRepo("O365Exchange/Weve_DA/Nova");
            const int slot = 3;
            var ownerKey = SlotRelease.OwnerKey("workspace", repo.NormalizedKey, slot);

            // A review was admitted, then died — its claim is leased but the lease lapsed two hours ago.
            var receipt = new JsonObject
            {
                ["repository"] = repo.DisplayName,
                ["prId"] = "100",
                ["slot"] = slot,
                ["workspaceId"] = "workspace",
                ["sessionId"] = "dead-session",
                ["headSha"] = "abc123",
                ["threadId"] = "thread-x",
            };
            var token = await SlotRelease.ClaimAsync(
                connection,
                ownerKey,
                receipt,
                null,
                $"reviewd/review-attempt/{repo.NormalizedKey}/100",
                CancellationToken.None,
                DateTimeOffset.UtcNow - TimeSpan.FromHours(2)
            );
            var attempt = receipt.DeepClone().AsObject();
            attempt["claimToken"] = token;
            attempt["cleanupState"] = "owned";
            await store.SetAsync($"reviewd/review-attempt/{repo.NormalizedKey}/100", attempt, CancellationToken.None);

            // RED: today the lapsed-lease slot is still owned, so a fresh PR cannot claim it — the jam.
            var blocked = () =>
                SlotRelease.ClaimAsync(
                    connection,
                    ownerKey,
                    new JsonObject { ["prId"] = "200", ["sessionId"] = "live" },
                    null,
                    $"reviewd/review-attempt/{repo.NormalizedKey}/200",
                    CancellationToken.None
                );
            await blocked.Should().ThrowAsync<InvalidOperationException>();

            // The host freezes the dead conversation and reports it and its (empty) tree idle; the sandbox
            // confirms the discard.
            var handler = new FakeHttpMessageHandler()
                .OnJson(HttpMethod.Post, "/freeze", """{"frozen":true}""")
                .OnJson(HttpMethod.Get, "/run-state", """{"isInProgress":false,"currentRunId":null}""")
                .OnJson(HttpMethod.Get, "/subagents?recursive=true", EmptyTree);
            using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
            var client = new LmStreamingS2SClient(http, null, null, null);
            SlotReaper.SandboxExecutor sandbox = (_, args, _, _) =>
                Task.FromResult(
                    new JsonObject
                    {
                        ["claimToken"] = args["claimToken"]!.DeepClone(),
                        ["cleanBaselineVerified"] = true,
                    }
                );

            var results = await SlotReaper.SweepAsync(
                connection,
                client,
                sandbox,
                "workspace",
                DateTimeOffset.UtcNow,
                undatedExpired: false,
                execute: true,
                CancellationToken.None
            );

            // GREEN: the reaper freed the slot.
            results.Should().ContainSingle(r => r.Status == "reclaimed" && r.Pr == "100" && r.Slot == slot);
            (await store.GetAsync<JsonObject>(ownerKey))!["state"]!.GetValue<string>().Should().Be("released");

            // The freed slot now accepts a fresh different-PR claim — nothing blocks the slot.
            var next = await SlotRelease.ClaimAsync(
                connection,
                ownerKey,
                new JsonObject { ["prId"] = "200", ["sessionId"] = "live" },
                null,
                $"reviewd/review-attempt/{repo.NormalizedKey}/200",
                CancellationToken.None
            );
            next.Should().NotBeNullOrEmpty();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }

    [Fact]
    public async Task Reaper_never_freezes_a_conversation_the_host_reports_in_progress()
    {
        // A legacy undated owner (reclaimable only under --include-undated) whose conversation is in
        // fact still running: the host reports it in-progress. The reaper must leave it completely
        // untouched — never freeze it — so an in-flight review (e.g. the active session) is not
        // disturbed. A later sweep retries once it truly goes idle.
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-inprog-{Guid.NewGuid():N}.db");
        try
        {
            using var connection = PrThreadBindings.Connection(database);
            var store = new SqliteKvStore(connection);
            await connection.OpenAsync();

            var repo = PrCommands.ParseRepo("O365Exchange/Weve_DA/Nova");
            const int slot = 7;
            var ownerKey = SlotRelease.OwnerKey("workspace", repo.NormalizedKey, slot);
            var receipt = new JsonObject
            {
                ["repository"] = repo.DisplayName,
                ["prId"] = "100",
                ["slot"] = slot,
                ["workspaceId"] = "workspace",
                ["sessionId"] = "live-session",
                ["headSha"] = "abc123",
                ["threadId"] = "thread-busy",
            };
            // Claimed by the old binary: owned, no lease stamp. Strip the lease so it is an undated owner.
            _ = await SlotRelease.ClaimAsync(
                connection,
                ownerKey,
                receipt,
                null,
                $"reviewd/review-attempt/{repo.NormalizedKey}/100",
                CancellationToken.None
            );
            var undated = (await store.GetAsync<JsonObject>(ownerKey))!;
            undated.Remove("leaseExpiresAt");
            await store.SetAsync(ownerKey, undated, CancellationToken.None);
            var attempt = receipt.DeepClone().AsObject();
            attempt["claimToken"] = undated["claimToken"]!.DeepClone();
            attempt["cleanupState"] = "owned";
            await store.SetAsync($"reviewd/review-attempt/{repo.NormalizedKey}/100", attempt, CancellationToken.None);

            var handler = new FakeHttpMessageHandler()
                .OnJson(HttpMethod.Post, "/freeze", """{"frozen":true}""")
                .OnJson(HttpMethod.Get, "/run-state", """{"isInProgress":true,"currentRunId":"run-1"}""");
            using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
            var client = new LmStreamingS2SClient(http, null, null, null);
            SlotReaper.SandboxExecutor sandbox = (_, _, _, _) =>
                throw new InvalidOperationException("The reaper must not discard an in-progress conversation.");

            var results = await SlotReaper.SweepAsync(
                connection,
                client,
                sandbox,
                "workspace",
                DateTimeOffset.UtcNow,
                undatedExpired: true,
                execute: true,
                CancellationToken.None
            );

            results.Should().ContainSingle(r => r.Status == "skipped" && r.Pr == "100");
            handler.CountRequests("/freeze").Should().Be(0, "an in-progress conversation is never frozen");
            (await store.GetAsync<JsonObject>(ownerKey))!["state"]!.GetValue<string>().Should().Be("owned");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }

    [Fact]
    public async Task Reaper_leaves_a_slot_whose_lease_is_still_live()
    {
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-live-{Guid.NewGuid():N}.db");
        try
        {
            using var connection = PrThreadBindings.Connection(database);
            var store = new SqliteKvStore(connection);
            await connection.OpenAsync();

            var repo = PrCommands.ParseRepo("O365Exchange/Weve_DA/Nova");
            const int slot = 1;
            var ownerKey = SlotRelease.OwnerKey("workspace", repo.NormalizedKey, slot);
            var receipt = new JsonObject
            {
                ["repository"] = repo.DisplayName,
                ["prId"] = "100",
                ["slot"] = slot,
                ["workspaceId"] = "workspace",
                ["sessionId"] = "session",
                ["headSha"] = "abc123",
                ["threadId"] = "thread-live",
            };
            // Freshly claimed: the lease is well in the future.
            _ = await SlotRelease.ClaimAsync(
                connection,
                ownerKey,
                receipt,
                null,
                $"reviewd/review-attempt/{repo.NormalizedKey}/100",
                CancellationToken.None
            );

            // A host that would answer every call still must not be touched for a live-lease slot.
            var handler = new FakeHttpMessageHandler()
                .OnJson(HttpMethod.Post, "/freeze", """{"frozen":true}""")
                .OnJson(HttpMethod.Get, "/run-state", """{"isInProgress":false,"currentRunId":null}""");
            using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
            var client = new LmStreamingS2SClient(http, null, null, null);
            SlotReaper.SandboxExecutor sandbox = (_, _, _, _) =>
                throw new InvalidOperationException("The reaper must not discard a live-lease slot.");

            var results = await SlotReaper.SweepAsync(
                connection,
                client,
                sandbox,
                "workspace",
                DateTimeOffset.UtcNow,
                undatedExpired: false,
                execute: true,
                CancellationToken.None
            );

            results.Should().BeEmpty("no lease had lapsed");
            handler.Requests.Should().BeEmpty("a live-lease slot is never even frozen");
            (await store.GetAsync<JsonObject>(ownerKey))!["state"]!.GetValue<string>().Should().Be("owned");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }

    private const string EmptyTree = """{"schemaVersion":1,"nodes":[]}""";

    private static string Tree(params string[] statuses) =>
        """{"schemaVersion":1,"nodes":["""
        + string.Join(
            ",",
            statuses.Select(
                (status, i) =>
                    $$"""{"agentId":"agent-{{i + 1}}","threadId":"child-{{i + 1}}","parentThreadId":"thread-x","depth":1,"template":"reviewer","status":"{{status}}"}"""
            )
        )
        + "]}";

    // A review whose claim lapsed two hours ago and whose root conversation is idle: the only question
    // left for the reaper is whether anything below the root is still working.
    private static async Task<(string OwnerKey, SqliteKvStore Store)> SeedLapsedClaimAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        int slot,
        TimeSpan? claimedAgo = null
    )
    {
        var repo = PrCommands.ParseRepo("O365Exchange/Weve_DA/Nova");
        var store = new SqliteKvStore(connection);
        var ownerKey = SlotRelease.OwnerKey("workspace", repo.NormalizedKey, slot);
        var receipt = new JsonObject
        {
            ["repository"] = repo.DisplayName,
            ["prId"] = "100",
            ["slot"] = slot,
            ["workspaceId"] = "workspace",
            ["sessionId"] = "dead-session",
            ["headSha"] = "abc123",
            ["threadId"] = "thread-x",
        };
        var token = await SlotRelease.ClaimAsync(
            connection,
            ownerKey,
            receipt,
            null,
            $"reviewd/review-attempt/{repo.NormalizedKey}/100",
            CancellationToken.None,
            DateTimeOffset.UtcNow - (claimedAgo ?? TimeSpan.FromHours(2))
        );
        var attempt = receipt.DeepClone().AsObject();
        attempt["claimToken"] = token;
        attempt["cleanupState"] = "owned";
        await store.SetAsync($"reviewd/review-attempt/{repo.NormalizedKey}/100", attempt, CancellationToken.None);
        return (ownerKey, store);
    }

    private static async Task<(IReadOnlyList<SlotReaper.ReclaimResult> Results, int Discards)> SweepAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        FakeHttpMessageHandler handler,
        List<JsonObject>? calls = null,
        JsonArray? forced = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null
    )
    {
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var client = new LmStreamingS2SClient(http, null, null, null);
        var discards = 0;
        SlotReaper.SandboxExecutor sandbox = (_, args, _, _) =>
        {
            discards++;
            calls?.Add(args.DeepClone().AsObject());
            return Task.FromResult(
                new JsonObject
                {
                    ["claimToken"] = args["claimToken"]!.DeepClone(),
                    ["cleanBaselineVerified"] = true,
                    ["forced"] = forced?.DeepClone() ?? new JsonArray(),
                }
            );
        };
        var results = await SlotReaper.SweepAsync(
            connection,
            client,
            sandbox,
            "workspace",
            DateTimeOffset.UtcNow,
            undatedExpired: false,
            execute: true,
            CancellationToken.None,
            delay: delay ?? ((_, _) => Task.CompletedTask)
        );
        return (results, discards);
    }

    [Theory]
    [InlineData("running")]
    [InlineData("notstarted")]
    public async Task Reaper_never_discards_while_a_descendant_of_an_idle_root_is_still_working(string childStatus)
    {
        // The root went idle and the client lease lapsed, but a background child is still writing into the
        // prepared slot. Discarding now would destroy its work, so the slot is left wholly untouched.
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-child-{Guid.NewGuid():N}.db");
        try
        {
            using var connection = PrThreadBindings.Connection(database);
            await connection.OpenAsync();
            var (ownerKey, store) = await SeedLapsedClaimAsync(connection, 4);
            var handler = new FakeHttpMessageHandler()
                .OnJson(HttpMethod.Post, "/freeze", """{"frozen":true}""")
                .OnJson(HttpMethod.Get, "/run-state", """{"isInProgress":false,"currentRunId":null}""")
                .OnJson(HttpMethod.Get, "/subagents?recursive=true", Tree("completed", childStatus));

            var (results, discards) = await SweepAsync(connection, handler);

            results.Should().ContainSingle(r => r.Status == "skipped" && r.Detail!.Contains("agent-2"));
            discards.Should().Be(0, "a working descendant means the slot is still in use");
            handler.CountRequests("/freeze").Should().Be(0, "a live tree is never frozen");
            (await store.GetAsync<JsonObject>(ownerKey))!["state"]!.GetValue<string>().Should().Be("owned");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }

    [Fact]
    public async Task Reaper_skips_when_the_host_cannot_say_whether_descendants_are_quiet()
    {
        // hierarchy_busy means the tree is unknown, and unknown is never quiet: skip and retry next sweep.
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-busy-{Guid.NewGuid():N}.db");
        try
        {
            using var connection = PrThreadBindings.Connection(database);
            await connection.OpenAsync();
            var (ownerKey, store) = await SeedLapsedClaimAsync(connection, 5);
            var handler = new FakeHttpMessageHandler()
                .OnJson(HttpMethod.Post, "/freeze", """{"frozen":true}""")
                .OnJson(HttpMethod.Get, "/run-state", """{"isInProgress":false,"currentRunId":null}""")
                .OnJson(
                    HttpMethod.Get,
                    "/subagents?recursive=true",
                    """{"code":"hierarchy_busy"}""",
                    System.Net.HttpStatusCode.ServiceUnavailable
                );

            var (results, discards) = await SweepAsync(connection, handler);

            results.Should().ContainSingle(r => r.Status == "skipped");
            discards.Should().Be(0);
            handler.CountRequests("/freeze").Should().Be(0);
            (await store.GetAsync<JsonObject>(ownerKey))!["state"]!.GetValue<string>().Should().Be("owned");
            handler
                .CountRequests("/subagents?recursive=true")
                .Should()
                .Be(
                    SlotReaper.BusyRereads + 1,
                    "busy is re-read a bounded number of times, then left for the next sweep"
                );
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }

    [Fact]
    public async Task Reaper_rereads_a_busy_host_and_reclaims_once_it_answers()
    {
        // A shared host answers hierarchy_busy whenever any conversation is publishing metadata. That is unknown,
        // not active: re-read it instead of giving up the whole sweep.
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-busy-then-ok-{Guid.NewGuid():N}.db");
        try
        {
            using var connection = PrThreadBindings.Connection(database);
            await connection.OpenAsync();
            var (ownerKey, store) = await SeedLapsedClaimAsync(connection, 7);
            var handler = new FakeHttpMessageHandler()
                .OnJson(HttpMethod.Post, "/freeze", """{"frozen":true}""")
                .OnJson(HttpMethod.Get, "/run-state", """{"isInProgress":false,"currentRunId":null}""")
                .OnSequence(
                    HttpMethod.Get,
                    "/subagents?recursive=true",
                    (System.Net.HttpStatusCode.ServiceUnavailable, """{"code":"hierarchy_busy"}"""),
                    (System.Net.HttpStatusCode.ServiceUnavailable, """{"code":"hierarchy_busy"}"""),
                    (System.Net.HttpStatusCode.OK, Tree("completed"))
                );

            var (results, discards) = await SweepAsync(connection, handler);

            results.Should().ContainSingle(r => r.Status == "reclaimed");
            discards.Should().Be(1);
            (await store.GetAsync<JsonObject>(ownerKey))!["state"]!.GetValue<string>().Should().Be("released");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }

    [Theory]
    [InlineData("renewed")]
    [InlineData("released")]
    public async Task Reaper_leaves_a_slot_whose_owner_changed_while_it_waited(string change)
    {
        // A sweep can wait minutes on a busy host after choosing an owner. A lane that renewed its lease meanwhile is
        // live, and a released owner may already belong to another PR's claim: neither is frozen or reset.
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-owner-moved-{Guid.NewGuid():N}.db");
        try
        {
            using var connection = PrThreadBindings.Connection(database);
            await connection.OpenAsync();
            var (ownerKey, store) = await SeedLapsedClaimAsync(connection, 7);
            var handler = new FakeHttpMessageHandler()
                .OnJson(HttpMethod.Post, "/freeze", """{"frozen":true}""")
                .OnJson(HttpMethod.Get, "/run-state", """{"isInProgress":false,"currentRunId":null}""")
                .OnSequence(
                    HttpMethod.Get,
                    "/subagents?recursive=true",
                    (System.Net.HttpStatusCode.ServiceUnavailable, """{"code":"hierarchy_busy"}"""),
                    (System.Net.HttpStatusCode.OK, Tree("completed"))
                );

            var (results, discards) = await SweepAsync(
                connection,
                handler,
                delay: async (_, _) =>
                {
                    var owner = (await store.GetAsync<JsonObject>(ownerKey))!;
                    if (change == "renewed")
                        owner["leaseExpiresAt"] = DateTimeOffset.UtcNow + SlotRelease.DefaultLeaseWindow;
                    else
                        owner["state"] = "released";
                    await store.SetAsync(ownerKey, owner, CancellationToken.None);
                }
            );

            results.Should().ContainSingle(r => r.Status == "skipped" && r.Detail!.Contains("owner changed"));
            discards.Should().Be(0);
            handler.CountRequests("/freeze").Should().Be(0);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }

    [Fact]
    public async Task Reaper_rereads_the_root_after_waiting_out_a_busy_tree()
    {
        // A run can start while the tree read waits on a busy host; the root read from before the wait is stale.
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-root-reread-{Guid.NewGuid():N}.db");
        try
        {
            using var connection = PrThreadBindings.Connection(database);
            await connection.OpenAsync();
            var (ownerKey, store) = await SeedLapsedClaimAsync(connection, 7);
            var handler = new FakeHttpMessageHandler()
                .OnJson(HttpMethod.Post, "/freeze", """{"frozen":true}""")
                .OnSequence(
                    HttpMethod.Get,
                    "/run-state",
                    (System.Net.HttpStatusCode.OK, """{"isInProgress":false,"currentRunId":null}"""),
                    (System.Net.HttpStatusCode.OK, """{"isInProgress":true,"currentRunId":"run-2"}""")
                )
                .OnSequence(
                    HttpMethod.Get,
                    "/subagents?recursive=true",
                    (System.Net.HttpStatusCode.ServiceUnavailable, """{"code":"hierarchy_busy"}"""),
                    (System.Net.HttpStatusCode.OK, Tree("completed"))
                );

            var (results, discards) = await SweepAsync(connection, handler);

            results.Should().ContainSingle(r => r.Status == "skipped" && r.Detail!.Contains("in progress"));
            discards.Should().Be(0);
            handler.CountRequests("/freeze").Should().Be(0);
            (await store.GetAsync<JsonObject>(ownerKey))!["state"]!.GetValue<string>().Should().Be("owned");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }

    [Theory]
    [InlineData("prepare-pending", false, false, "failed-released", true, 2)]
    [InlineData("review-completed", true, false, "failed-released", true, 2)]
    // The runner saves verify-completed before the verify result: a crash in between still posted the review.
    [InlineData("verify-completed", true, false, "verify-completed", false, 1)]
    [InlineData("commit-pending", true, true, "commit-pending", false, 1)]
    public async Task Reaper_turns_a_reset_review_without_verified_publication_into_a_failed_admission(
        string stage,
        bool sent,
        bool verified,
        string expectedStage,
        bool pickOffersItAgain,
        int resetsAfter
    )
    {
        // Seen live: 12 open PRs whose reviews stopped before publication was verified were reset by the reaper and
        // then parked until a new push, and prepares that failed holding their slot were never retried. Kay9: review
        // them again. A review whose publication was verified posted, so it keeps its stage and is not redone.
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-reset-retry-{Guid.NewGuid():N}.db");
        try
        {
            using var connection = PrThreadBindings.Connection(database);
            await connection.OpenAsync();
            var (_, store) = await SeedLapsedClaimAsync(connection, 4);
            var attemptKey =
                $"reviewd/review-attempt/{PrCommands.ParseRepo("O365Exchange/Weve_DA/Nova").NormalizedKey}/100";
            var seeded = (await store.GetAsync<JsonObject>(attemptKey))!;
            seeded["stage"] = stage;
            seeded["resetCount"] = 1;
            if (sent)
            {
                seeded["reviewIdempotencyKey"] = "reviewd-key";
                seeded["reviewInputId"] = "input-1";
            }
            if (verified)
                seeded["verifiedPublication"] = new JsonObject { ["comments"] = 3 };
            await store.SetAsync(attemptKey, seeded, CancellationToken.None);
            var handler = new FakeHttpMessageHandler()
                .OnJson(HttpMethod.Post, "/freeze", """{"frozen":true}""")
                .OnJson(HttpMethod.Get, "/run-state", """{"isInProgress":false,"currentRunId":null}""")
                .OnJson(HttpMethod.Get, "/subagents?recursive=true", EmptyTree);

            var (results, _) = await SweepAsync(connection, handler);

            results.Should().ContainSingle(r => r.Status == "reclaimed");
            var attempt = (await store.GetAsync<JsonObject>(attemptKey))!;
            attempt["stage"]!.GetValue<string>().Should().Be(expectedStage);
            PrSelection.IsSettled(JsonSerializer.SerializeToElement(attempt)).Should().Be(pickOffersItAgain);
            attempt["resetCount"]!.GetValue<int>().Should().Be(resetsAfter);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }

    [Fact]
    public async Task Reaper_reclaims_once_every_descendant_has_settled()
    {
        // Finished, failed and stopped children are done writing, so they do not hold the slot.
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-settled-{Guid.NewGuid():N}.db");
        try
        {
            using var connection = PrThreadBindings.Connection(database);
            await connection.OpenAsync();
            var (ownerKey, store) = await SeedLapsedClaimAsync(connection, 6);
            var handler = new FakeHttpMessageHandler()
                .OnJson(HttpMethod.Post, "/freeze", """{"frozen":true}""")
                .OnJson(HttpMethod.Get, "/run-state", """{"isInProgress":false,"currentRunId":null}""")
                .OnJson(HttpMethod.Get, "/subagents?recursive=true", Tree("completed", "error", "stopped"));

            var (results, discards) = await SweepAsync(connection, handler);

            results.Should().ContainSingle(r => r.Status == "reclaimed");
            discards.Should().Be(1);
            (await store.GetAsync<JsonObject>(ownerKey))!["state"]!.GetValue<string>().Should().Be("released");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }

    [Fact]
    public async Task A_long_reaper_reset_keeps_the_slot_leased_so_a_second_reaper_leaves_it_alone()
    {
        // The reaper's own reset renews the lease while it runs, like every other step. Otherwise a second reaper (the
        // next sweep, or reclaim-slot) reads the same long-lapsed lease and resets the slot again under the first.
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-reset-lease-{Guid.NewGuid():N}.db");
        var previousInterval = ReviewRunner.LeaseRenewInterval;
        ReviewRunner.LeaseRenewInterval = TimeSpan.FromMilliseconds(20);
        try
        {
            using var connection = PrThreadBindings.Connection(database);
            await connection.OpenAsync();
            var (ownerKey, store) = await SeedLapsedClaimAsync(connection, 2);
            var handler = new FakeHttpMessageHandler()
                .OnJson(HttpMethod.Post, "/freeze", """{"frozen":true}""")
                .OnJson(HttpMethod.Get, "/run-state", """{"isInProgress":false,"currentRunId":null}""")
                .OnJson(HttpMethod.Get, "/subagents?recursive=true", Tree("completed"));
            using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
            var client = new LmStreamingS2SClient(http, null, null, null);
            IReadOnlyList<SlotReaper.ReclaimResult>? second = null;
            var resets = 0;
            SlotReaper.SandboxExecutor? sandbox = null;
            sandbox = async (phase, args, _, token) =>
            {
                if (phase is "step-activity" or "step-stop")
                    return [];
                if (++resets == 1)
                {
                    using var other = PrThreadBindings.Connection(database);
                    await other.OpenAsync(token);
                    var otherStore = new SqliteKvStore(other);
                    for (var deadline = DateTime.UtcNow.AddSeconds(5); DateTime.UtcNow < deadline; await Task.Delay(20))
                    {
                        var lease = (await otherStore.GetAsync<JsonObject>(ownerKey))!["leaseExpiresAt"]!;
                        if (lease.GetValue<DateTimeOffset>() > DateTimeOffset.UtcNow)
                            break;
                    }
                    second = await SlotReaper.SweepAsync(
                        other,
                        client,
                        sandbox!,
                        "workspace",
                        DateTimeOffset.UtcNow,
                        undatedExpired: false,
                        execute: true,
                        token,
                        (_, _) => Task.CompletedTask
                    );
                }
                return new JsonObject
                {
                    ["claimToken"] = args["claimToken"]!.DeepClone(),
                    ["cleanBaselineVerified"] = true,
                    ["forced"] = new JsonArray(),
                };
            };

            var first = await SlotReaper.SweepAsync(
                connection,
                client,
                sandbox,
                "workspace",
                DateTimeOffset.UtcNow,
                undatedExpired: false,
                execute: true,
                CancellationToken.None,
                (_, _) => Task.CompletedTask
            );

            first.Should().ContainSingle(r => r.Status == "reclaimed");
            second.Should().BeEmpty("the running reset kept the lease fresh");
            resets.Should().Be(1);
            (await store.GetAsync<JsonObject>(ownerKey))!["state"]!.GetValue<string>().Should().Be("released");
        }
        finally
        {
            ReviewRunner.LeaseRenewInterval = previousInterval;
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }

    [Fact]
    public async Task A_reset_that_renews_and_then_fails_leaves_the_lease_lapsed_for_the_next_sweep()
    {
        // Kay9: a slot is never held needlessly. A reset that renewed the lease while it ran and then failed puts the
        // lease back as it found it, so the next sweep retries at once instead of waiting out a lease nobody renews.
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-reset-fail-{Guid.NewGuid():N}.db");
        var previousInterval = ReviewRunner.LeaseRenewInterval;
        ReviewRunner.LeaseRenewInterval = TimeSpan.FromMilliseconds(20);
        try
        {
            using var connection = PrThreadBindings.Connection(database);
            await connection.OpenAsync();
            var (ownerKey, store) = await SeedLapsedClaimAsync(connection, 2);
            var lapsed = (await store.GetAsync<JsonObject>(ownerKey))!["leaseExpiresAt"]!.GetValue<DateTimeOffset>();
            var handler = new FakeHttpMessageHandler()
                .OnJson(HttpMethod.Post, "/freeze", """{"frozen":true}""")
                .OnJson(HttpMethod.Get, "/run-state", """{"isInProgress":false,"currentRunId":null}""")
                .OnJson(HttpMethod.Get, "/subagents?recursive=true", Tree("completed"));
            using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
            var client = new LmStreamingS2SClient(http, null, null, null);
            var resets = 0;
            SlotReaper.SandboxExecutor sandbox = async (phase, args, _, token) =>
            {
                if (phase is "step-activity" or "step-stop")
                    return [];
                if (++resets == 1)
                {
                    using var other = PrThreadBindings.Connection(database);
                    await other.OpenAsync(token);
                    var otherStore = new SqliteKvStore(other);
                    for (var deadline = DateTime.UtcNow.AddSeconds(5); DateTime.UtcNow < deadline; await Task.Delay(20))
                    {
                        var lease = (await otherStore.GetAsync<JsonObject>(ownerKey))!["leaseExpiresAt"]!;
                        if (lease.GetValue<DateTimeOffset>() > DateTimeOffset.UtcNow)
                            break;
                    }
                    throw new ReviewRunner.SettledSandboxException("cleanup", "Sandbox cleanup failed (exit 1).");
                }
                return new JsonObject
                {
                    ["claimToken"] = args["claimToken"]!.DeepClone(),
                    ["cleanBaselineVerified"] = true,
                    ["forced"] = new JsonArray(),
                };
            };
            Task<IReadOnlyList<SlotReaper.ReclaimResult>> SweepOnceAsync() =>
                SlotReaper.SweepAsync(
                    connection,
                    client,
                    sandbox,
                    "workspace",
                    DateTimeOffset.UtcNow,
                    undatedExpired: false,
                    execute: true,
                    CancellationToken.None,
                    (_, _) => Task.CompletedTask
                );

            var failed = await SweepOnceAsync();
            var lease = (await store.GetAsync<JsonObject>(ownerKey))!["leaseExpiresAt"]!.GetValue<DateTimeOffset>();
            var retried = await SweepOnceAsync();

            failed.Should().ContainSingle(r => r.Status == "skipped").Which.Detail.Should().Contain("exit 1");
            lease.Should().Be(lapsed, "the failed reset put back the lease it renewed");
            retried.Should().ContainSingle(r => r.Status == "reclaimed");
            resets.Should().Be(2);
        }
        finally
        {
            ReviewRunner.LeaseRenewInterval = previousInterval;
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }

    [Fact]
    public async Task A_failed_step_leaves_a_lease_a_live_lane_renewed_since()
    {
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-restore-cas-{Guid.NewGuid():N}.db");
        try
        {
            using var connection = PrThreadBindings.Connection(database);
            await connection.OpenAsync();
            var (ownerKey, store) = await SeedLapsedClaimAsync(connection, 2);
            var token = (await store.GetAsync<JsonObject>(ownerKey))!["claimToken"]!.GetValue<string>();
            var step = (await SlotRelease.RenewAsync(connection, ownerKey, token, CancellationToken.None))!.Value;
            var lane = DateTimeOffset.UtcNow.AddMinutes(10);
            (await SlotRelease.HeartbeatAsync(connection, ownerKey, token, CancellationToken.None, lane))
                .Should()
                .BeTrue();

            (await SlotRelease.RestoreLeaseAsync(connection, ownerKey, step.Before, step.After, CancellationToken.None))
                .Should()
                .BeFalse();
            (await store.GetAsync<JsonObject>(ownerKey))!["leaseExpiresAt"]!
                .GetValue<DateTimeOffset>()
                .Should()
                .Be(lane + SlotRelease.DefaultLeaseWindow);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }

    [Theory]
    // Kay9 (gap 1): a step whose watchdog is stuck would run, leased, until the 24-hour backstop. While no agent is
    // active, each renewal asks the sandbox's step-stop, which kills the step only if the watchdog heartbeat is 15
    // minutes stale. From then on the lease is not renewed, and the killed step puts back the lease it found.
    [InlineData("quiet", true)] // the heartbeat is stale at the third check: killed, lease put back
    [InlineData("quiet", false)] // the watchdog keeps beating: renewed, the step runs to its end
    [InlineData("active", true)] // an agent is active: never asked, renewed
    [InlineData("unknown", true)] // the agent read fails: counts as active
    [InlineData("hung", true)] // step-stop never answers: each check gives up in time, renewed, the step ends promptly
    public async Task A_step_whose_watchdog_is_stuck_is_stopped_only_while_no_agent_is_active(string agents, bool stale)
    {
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-step-stop-{Guid.NewGuid():N}.db");
        var previousInterval = ReviewRunner.LeaseRenewInterval;
        var previousTimeout = ReviewRunner.StepStopTimeout;
        ReviewRunner.LeaseRenewInterval = TimeSpan.FromMilliseconds(20);
        ReviewRunner.StepStopTimeout = TimeSpan.FromMilliseconds(50);
        try
        {
            using var connection = PrThreadBindings.Connection(database);
            await connection.OpenAsync();
            var (ownerKey, store) = await SeedLapsedClaimAsync(connection, 2);
            var owner = (await store.GetAsync<JsonObject>(ownerKey))!;
            var lapsed = owner["leaseExpiresAt"]!.GetValue<DateTimeOffset>();
            var reads = 0;
            var checks = 0;
            var killed = false;
            Task<JsonObject> Sandbox(string phase, JsonObject args, string operation, CancellationToken token) =>
                phase switch
                {
                    "step-activity" => Task.FromResult(new JsonObject()),
                    "step-stop" when agents == "hung" => HangAsync(token),
                    "step-stop" => Task.FromResult(new JsonObject { ["stopped"] = killed = ++checks >= 3 && stale }),
                    _ => StepAsync(),
                };
            async Task<JsonObject> HangAsync(CancellationToken token)
            {
                checks++;
                await Task.Delay(Timeout.Infinite, token);
                return new JsonObject { ["stopped"] = true };
            }
            async Task<JsonObject> StepAsync()
            {
                for (var deadline = DateTime.UtcNow.AddSeconds(10); DateTime.UtcNow < deadline; await Task.Delay(10))
                {
                    if (killed)
                        throw new ReviewRunner.SettledSandboxException("verify", "Sandbox verify failed (exit -9).");
                    if (reads >= 6)
                        return new JsonObject { ["ok"] = true };
                }
                throw new TimeoutException("The step never ended.");
            }
            var step = () =>
                ReviewRunner.RunLeasedStepAsync(
                    connection,
                    ownerKey,
                    owner["claimToken"]!.GetValue<string>(),
                    _ =>
                    {
                        reads++;
                        return agents == "unknown"
                            ? throw new HttpRequestException("host unreachable")
                            : Task.FromResult(agents == "active");
                    },
                    Sandbox,
                    "verify",
                    [],
                    Guid.NewGuid().ToString("N"),
                    CancellationToken.None
                );

            if (agents == "quiet" && stale)
                await step.Should().ThrowAsync<ReviewRunner.SettledSandboxException>();
            else
                (await step.Should().CompleteWithinAsync(TimeSpan.FromSeconds(5))).Subject["ok"]!
                    .GetValue<bool>()
                    .Should()
                    .BeTrue();
            var lease = (await store.GetAsync<JsonObject>(ownerKey))!["leaseExpiresAt"]!.GetValue<DateTimeOffset>();

            if (agents == "quiet" && stale)
            {
                checks.Should().Be(3, "the client stops asking, and renewing, once the step is killed");
                lease.Should().Be(lapsed, "the killed step put back the lease its renewals extended");
            }
            else
            {
                checks
                    .Should()
                    .Be(agents is "quiet" or "hung" ? reads : 0, "only a quiet tick asks the sandbox to stop the step");
                lease.Should().BeAfter(DateTimeOffset.UtcNow);
            }
        }
        finally
        {
            ReviewRunner.LeaseRenewInterval = previousInterval;
            ReviewRunner.StepStopTimeout = previousTimeout;
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }

    [Fact]
    public async Task Reaper_forces_a_long_lapsed_slot_through_a_bookkeeping_mismatch()
    {
        // Lapsed 90 minutes, nothing active in the host: whatever the sandbox's records say, the slot comes back,
        // and the log names what was overridden (seen live: our own half-released marker jammed a slot 8 hours).
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-force-{Guid.NewGuid():N}.db");
        try
        {
            using var connection = PrThreadBindings.Connection(database);
            await connection.OpenAsync();
            var (ownerKey, store) = await SeedLapsedClaimAsync(connection, 2);
            var handler = new FakeHttpMessageHandler()
                .OnJson(HttpMethod.Post, "/freeze", """{"frozen":true}""")
                .OnJson(HttpMethod.Get, "/run-state", """{"isInProgress":false,"currentRunId":null}""")
                .OnJson(HttpMethod.Get, "/subagents?recursive=true", Tree("completed"));
            var calls = new List<JsonObject>();

            var (results, _) = await SweepAsync(connection, handler, calls, ["owner marker already released"]);

            calls.Should().ContainSingle().Which["force"]!.GetValue<bool>().Should().BeTrue();
            results
                .Should()
                .ContainSingle(r => r.Status == "reclaimed")
                .Which.Detail.Should()
                .Be("forced: owner marker already released");
            (await store.GetAsync<JsonObject>(ownerKey))!["state"]!.GetValue<string>().Should().Be("released");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }

    [Fact]
    public async Task A_verified_review_stranded_by_a_head_move_frees_its_slot_without_being_redone()
    {
        // Seen on the ADO runner (MCQdbDEV #11441): the review posted and verified, the artifact commit was deferred
        // (verify-pending), and the PR head moved before it resumed. The attempt can never resume and its native
        // marker no longer matched, so a plain reclaim refused. Once the lease has been lapsed 30 minutes the reaper
        // forces the slot back, keeps the verified stage, and the new head is admitted as a fresh review.
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-stranded-{Guid.NewGuid():N}.db");
        try
        {
            using var connection = PrThreadBindings.Connection(database);
            await connection.OpenAsync();
            var (ownerKey, store) = await SeedLapsedClaimAsync(connection, 0);
            var attemptKey =
                $"reviewd/review-attempt/{PrCommands.ParseRepo("O365Exchange/Weve_DA/Nova").NormalizedKey}/100";
            var seeded = (await store.GetAsync<JsonObject>(attemptKey))!;
            seeded["stage"] = "verify-pending";
            seeded["reviewInputId"] = "input-1";
            seeded["confirmationInputId"] = "input-2";
            seeded["verifiedPublication"] = new JsonObject { ["comments"] = 3 };
            await store.SetAsync(attemptKey, seeded, CancellationToken.None);
            var handler = new FakeHttpMessageHandler()
                .OnJson(HttpMethod.Post, "/freeze", """{"frozen":true}""")
                .OnJson(HttpMethod.Get, "/run-state", """{"isInProgress":false,"currentRunId":null}""")
                .OnJson(HttpMethod.Get, "/subagents?recursive=true", Tree("completed"));
            var calls = new List<JsonObject>();

            var (results, _) = await SweepAsync(
                connection,
                handler,
                calls,
                ["owner marker held by claim other for PR 100"]
            );

            calls.Should().ContainSingle().Which["force"]!.GetValue<bool>().Should().BeTrue();
            results
                .Should()
                .ContainSingle(r => r.Status == "reclaimed")
                .Which.Detail.Should()
                .Be("forced: owner marker held by claim other for PR 100");
            (await store.GetAsync<JsonObject>(ownerKey))!["state"]!.GetValue<string>().Should().Be("released");
            var attempt = (await store.GetAsync<JsonObject>(attemptKey))!;
            attempt["stage"]!.GetValue<string>().Should().Be("verify-pending");
            attempt["verifiedPublication"].Should().NotBeNull();
            attempt["resetCount"].Should().BeNull();
            attempt["reclaim"]!["forced"]!
                .AsArray()
                .Select(o => o!.GetValue<string>())
                .Should()
                .Equal("owner marker held by claim other for PR 100");
            var released = JsonSerializer.SerializeToElement(attempt);
            PrSelection.IsReleasedForEarlierHead(released, "def456").Should().BeTrue("the new head is a fresh review");
            PrSelection.IsReleasedForEarlierHead(released, "abc123").Should().BeFalse("the posted head is not redone");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }

    [Theory]
    [InlineData(120, true, true)]
    [InlineData(40, true, false)] // the lease lapsed only 10 minutes ago
    [InlineData(120, false, false)] // another claim holds the slot now
    public async Task Reclaim_slot_forces_exactly_when_the_reaper_would(int claimedMinutesAgo, bool sameClaim, bool due)
    {
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-reclaim-force-{Guid.NewGuid():N}.db");
        try
        {
            using var connection = PrThreadBindings.Connection(database);
            await connection.OpenAsync();
            var (ownerKey, store) = await SeedLapsedClaimAsync(connection, 1, TimeSpan.FromMinutes(claimedMinutesAgo));
            var token = (await store.GetAsync<JsonObject>(ownerKey))!["claimToken"]!.GetValue<string>();

            (
                await SlotReaper.IsForceDueAsync(
                    connection,
                    "workspace",
                    PrCommands.ParseRepo("O365Exchange/Weve_DA/Nova"),
                    1,
                    sameClaim ? token : "other",
                    DateTimeOffset.UtcNow,
                    CancellationToken.None
                )
            )
                .Should()
                .Be(due);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }

    [Theory]
    [InlineData("renewed", "skipped")]
    [InlineData("unchanged", "reclaimed")]
    public async Task Reclaim_slot_leaves_a_slot_whose_owner_changed_while_it_waited(string change, string status)
    {
        // Like the sweep, reclaim-slot rechecks the owner before the freeze and the reset. A lane that renewed its lease
        // while the command waited on a busy host is live, so its slot is neither frozen nor reset.
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-reclaim-recheck-{Guid.NewGuid():N}.db");
        try
        {
            using var connection = PrThreadBindings.Connection(database);
            await connection.OpenAsync();
            var repo = PrCommands.ParseRepo("O365Exchange/Weve_DA/Nova");
            var (ownerKey, store) = await SeedLapsedClaimAsync(connection, 9);
            var token = (await store.GetAsync<JsonObject>(ownerKey))!["claimToken"]!.GetValue<string>();
            var attempt = (await store.GetAsync<JsonObject>($"reviewd/review-attempt/{repo.NormalizedKey}/100"))!;
            var handler = new FakeHttpMessageHandler()
                .OnJson(HttpMethod.Post, "/freeze", """{"frozen":true}""")
                .OnJson(HttpMethod.Get, "/run-state", """{"isInProgress":false,"currentRunId":null}""")
                .OnSequence(
                    HttpMethod.Get,
                    "/subagents?recursive=true",
                    (System.Net.HttpStatusCode.ServiceUnavailable, """{"code":"hierarchy_busy"}"""),
                    (System.Net.HttpStatusCode.OK, Tree("completed"))
                );
            using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
            var discards = 0;
            SlotReaper.SandboxExecutor sandbox = (_, args, _, _) =>
            {
                discards++;
                return Task.FromResult(
                    new JsonObject
                    {
                        ["claimToken"] = args["claimToken"]!.DeepClone(),
                        ["cleanBaselineVerified"] = true,
                    }
                );
            };
            var recheck = await SlotReaper.ReclaimSlotRecheckAsync(
                connection,
                "workspace",
                repo,
                9,
                token,
                CancellationToken.None
            );

            var result = await SlotReaper.ReclaimOneAsync(
                connection,
                new LmStreamingS2SClient(http, null, null, null),
                sandbox,
                repo,
                "100",
                9,
                "workspace",
                attempt,
                token,
                execute: true,
                CancellationToken.None,
                force: true,
                delay: async (_, _) =>
                {
                    if (change != "renewed")
                        return;
                    var owner = (await store.GetAsync<JsonObject>(ownerKey))!;
                    owner["leaseExpiresAt"] = DateTimeOffset.UtcNow + SlotRelease.DefaultLeaseWindow;
                    await store.SetAsync(ownerKey, owner, CancellationToken.None);
                },
                recheck: recheck
            );

            result.Status.Should().Be(status);
            discards.Should().Be(status == "reclaimed" ? 1 : 0);
            handler.CountRequests("/freeze").Should().Be(status == "reclaimed" ? 1 : 0);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }

    [Fact]
    public async Task Reaper_does_not_force_a_slot_whose_lease_lapsed_only_minutes_ago()
    {
        // Claimed 40 minutes ago, so the 30-minute lease lapsed 10 minutes ago: reclaim normally, no force.
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-noforce-{Guid.NewGuid():N}.db");
        try
        {
            using var connection = PrThreadBindings.Connection(database);
            await connection.OpenAsync();
            await SeedLapsedClaimAsync(connection, 3, TimeSpan.FromMinutes(40));
            var handler = new FakeHttpMessageHandler()
                .OnJson(HttpMethod.Post, "/freeze", """{"frozen":true}""")
                .OnJson(HttpMethod.Get, "/run-state", """{"isInProgress":false,"currentRunId":null}""")
                .OnJson(HttpMethod.Get, "/subagents?recursive=true", Tree("completed"));
            var calls = new List<JsonObject>();

            var (results, _) = await SweepAsync(connection, handler, calls);

            calls.Should().ContainSingle().Which["force"]!.GetValue<bool>().Should().BeFalse();
            results.Should().ContainSingle(r => r.Status == "reclaimed" && r.Detail == null);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }

    [Fact]
    public async Task Release_that_rolls_back_leaves_the_callers_attempt_unreleased()
    {
        // The runner's failure path persists the attempt object it passed in. If the release transaction
        // rolls back after the owner row was rewritten, that object must not read as released while the
        // owner row is still held by its token.
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-rollback-{Guid.NewGuid():N}.db");
        try
        {
            using var connection = PrThreadBindings.Connection(database);
            await connection.OpenAsync();
            var (ownerKey, store) = await SeedLapsedClaimAsync(connection, 8);
            var attemptKey =
                $"reviewd/review-attempt/{PrCommands.ParseRepo("O365Exchange/Weve_DA/Nova").NormalizedKey}/100";
            var attempt = (await store.GetAsync<JsonObject>(attemptKey))!;
            var token = attempt["claimToken"]!.GetValue<string>();
            using (var trigger = connection.CreateCommand())
            {
                // Fails the attempt write, which runs after the owner row update inside the same transaction.
                trigger.CommandText = $"""
                    CREATE TRIGGER fail_attempt_insert BEFORE INSERT ON cache WHEN NEW.key = '{attemptKey}'
                    BEGIN SELECT RAISE(ABORT, 'attempt write failed'); END;
                    CREATE TRIGGER fail_attempt_update BEFORE UPDATE ON cache WHEN NEW.key = '{attemptKey}'
                    BEGIN SELECT RAISE(ABORT, 'attempt write failed'); END;
                    """;
                await trigger.ExecuteNonQueryAsync();
            }

            var release = () =>
                SlotRelease.ReleaseAsync(connection, ownerKey, token, attemptKey, attempt, CancellationToken.None);

            await release.Should().ThrowAsync<Microsoft.Data.Sqlite.SqliteException>();
            attempt["cleanupState"]!.GetValue<string>().Should().Be("owned", "the release did not commit");
            (await store.GetAsync<JsonObject>(ownerKey))!["state"]!.GetValue<string>().Should().Be("owned");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }
}
