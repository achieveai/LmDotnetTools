extern alias Reviewd;

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

            // The host freezes the dead conversation and reports it idle; the sandbox confirms the discard.
            var handler = new FakeHttpMessageHandler()
                .OnJson(HttpMethod.Post, "/freeze", """{"frozen":true}""")
                .OnJson(HttpMethod.Get, "/run-state", """{"isInProgress":false,"currentRunId":null}""");
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
}
