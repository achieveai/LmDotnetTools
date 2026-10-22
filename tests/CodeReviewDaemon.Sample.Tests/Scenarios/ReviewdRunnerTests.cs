extern alias Reviewd;

using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.Misc.Storage;
using AchieveAi.LmDotnetTools.Sandbox;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Reviewd::Reviewd.Sample;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

public sealed class ReviewdRunnerTests
{
    [Fact]
    public void Preparation_recovery_requires_a_new_session_and_no_submission_identifiers()
    {
        var attempt = JsonNode.Parse("""{"stage":"prepare-failed-before-reset","sessionId":"old"}""")!;
        ReviewRunner.CanRetryPreparation(attempt, "new").Should().BeTrue();
        ReviewRunner.CanRetryPreparation(attempt, "old").Should().BeFalse();
        foreach (
            var field in new[]
            {
                "threadId",
                "reviewInputId",
                "confirmationInputId",
                "reviewIdempotencyKey",
                "confirmationIdempotencyKey",
                "initialSend",
                "confirmationSend",
            }
        )
        {
            attempt[field] = "retained";
            ReviewRunner.CanRetryPreparation(attempt, "new").Should().BeFalse();
            attempt.AsObject().Remove(field);
        }
        attempt.AsObject().Remove("sessionId");
        ReviewRunner.CanRetryPreparation(attempt, "new").Should().BeFalse();
    }

    [Fact]
    public void Slot_release_binds_the_exact_attempt_and_never_permits_same_PR_or_session()
    {
        const string attempt =
            """{"repository":"org/project/Nova","prId":"17","workspaceId":"workspace","slot":1,"sessionId":"old"}""";
        var proof = JsonNode.Parse(attempt)!.AsObject();
        proof["schemaVersion"] = 1;
        proof["attemptDigest"] = SlotRelease.Digest(attempt);
        proof["classification"] = "failed-before-review-admission";
        proof["noAdmission"] = true;
        proof["threadQuiescent"] = true;
        proof["nativeQuiescent"] = true;
        proof["evidence"] = new JsonArray(new JsonObject());
        SlotRelease.Allows(proof, attempt, "18", "new").Should().BeTrue();
        SlotRelease.Allows(proof, attempt, "17", "new").Should().BeFalse();
        SlotRelease.Allows(proof, attempt, "18", "old").Should().BeFalse();
        SlotRelease.Allows(proof, attempt + " ", "18", "new").Should().BeFalse();
        proof["noAdmission"] = false;
        SlotRelease.Allows(proof, attempt, "18", "new").Should().BeFalse();
        var admitted = JsonNode.Parse(attempt)!.AsObject();
        admitted["stage"] = "review-completed";
        admitted["reviewInputId"] = "original-input";
        var retired = admitted.DeepClone().AsObject();
        retired["schemaVersion"] = 1;
        retired["attemptDigest"] = SlotRelease.Digest(admitted.ToJsonString());
        retired["classification"] = "operator-retired-after-reset";
        retired["recoverySessionId"] = "old";
        retired["writersRetired"] = true;
        retired["producersStopped"] = true;
        retired["threadQuiescent"] = true;
        retired["nativeQuiescent"] = true;
        retired["resetReceiptPath"] = "reset.json";
        retired["archiveManifestPath"] = "archive.json";
        retired["evidence"] = new JsonArray(new JsonObject());
        SlotRelease.Allows(retired, admitted.ToJsonString(), "18", "old").Should().BeTrue();
        SlotRelease.Allows(retired, admitted.ToJsonString(), "17", "old").Should().BeFalse();
        SlotRelease.Allows(retired, admitted.ToJsonString(), "18", "different").Should().BeFalse();
        retired["slot"] = 2;
        SlotRelease.Allows(retired, admitted.ToJsonString(), "18", "old").Should().BeFalse();
        retired["slot"] = 1;
        admitted["stage"] = "completed-verified";
        retired["attemptDigest"] = SlotRelease.Digest(admitted.ToJsonString());
        SlotRelease.Allows(retired, admitted.ToJsonString(), "18", "old").Should().BeFalse();
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("admitted")]
    [InlineData("send-pending")]
    [InlineData("changed-claim")]
    [InlineData("native-owned")]
    [InlineData("operation-running")]
    [InlineData("missing-operation")]
    [InlineData("dirty-baseline")]
    [InlineData("writers-active")]
    [InlineData("stale-proof")]
    [InlineData("unbound-receipt")]
    [InlineData("existing-certificate")]
    public async Task Never_admitted_reconciliation_atomically_releases_only_a_proven_exact_owner(string scenario)
    {
        var directory = Path.Combine(Path.GetTempPath(), "reviewd-release-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "state.db");
        var repo = PrCommands.ParseRepo("org/project/WeveNova");
        var attemptKey = "reviewd/review-attempt/" + repo.NormalizedKey + "/17";
        var ownerKey = SlotRelease.OwnerKey("workspace", repo.NormalizedKey, 3);
        try
        {
            using var connection = PrThreadBindings.Connection(database);
            var store = new SqliteKvStore(connection);
            var attempt = new JsonObject
            {
                ["repository"] = repo.DisplayName,
                ["workspaceId"] = "workspace",
                ["sessionId"] = "session",
                ["prId"] = "17",
                ["slot"] = 3,
                ["stage"] = "prepare-pending",
                ["claimToken"] = "claim",
                ["prepareOperationId"] = "prepare",
                ["cleanupOperationId"] = "cleanup",
                ["cleanupState"] = "pending",
            };
            if (scenario == "admitted")
                attempt["reviewInputId"] = "input";
            if (scenario == "send-pending")
                attempt["initialSend"] = "pending";
            var original = attempt.ToJsonString();
            var owner = attempt.DeepClone().AsObject();
            owner["state"] = "owned";
            if (scenario == "changed-claim")
                owner["claimToken"] = "new-owner";
            await store.SetAsync(attemptKey, attempt);
            await store.SetAsync(ownerKey, owner);
            var native = attempt.DeepClone().AsObject();
            native["schemaVersion"] = 1;
            native["attemptDigest"] = SlotRelease.Digest(original);
            native["nativeOwnerState"] = scenario == "native-owned" ? "owned" : "released";
            native["preparedRunAbsent"] = true;
            native["poolRunAbsent"] = true;
            native["writersQuiescent"] = scenario != "writers-active";
            native["producersStopped"] = true;
            native["cleanBaselineVerified"] = scenario != "dirty-baseline";
            native["baselineSha"] = new string('a', 40);
            native["sourceHeadSha"] = new string('a', 40);
            native["checkedAt"] =
                scenario == "stale-proof" ? DateTimeOffset.UtcNow.AddMinutes(-6) : DateTimeOffset.UtcNow;
            native["operations"] = new JsonArray(
                new JsonObject
                {
                    ["operationId"] = "prepare",
                    ["status"] = scenario == "operation-running" ? "running" : "failed",
                    ["exitCode"] = 1,
                },
                new JsonObject
                {
                    ["operationId"] = scenario == "missing-operation" ? "unrelated" : "cleanup",
                    ["status"] = "failed",
                    ["exitCode"] = 1,
                }
            );
            var nativePath = Path.Combine(directory, "native.json");
            await File.WriteAllTextAsync(nativePath, native.ToJsonString());
            var proof = attempt.DeepClone().AsObject();
            proof["schemaVersion"] = 1;
            proof["attemptDigest"] = SlotRelease.Digest(original);
            proof["classification"] = "preparation-settled-native-idle";
            proof["noAdmission"] = true;
            proof["preparationSettled"] = true;
            proof["threadQuiescent"] = true;
            proof["nativeQuiescent"] = true;
            proof["operatorReason"] = "Never admitted; native baseline independently inspected.";
            proof["nativeIdleReceiptPath"] = nativePath;
            proof["evidence"] = new JsonArray(
                new JsonObject
                {
                    ["path"] = nativePath,
                    ["sha256"] = scenario == "unbound-receipt" ? "changed" : SlotRelease.Digest(native.ToJsonString()),
                }
            );
            var proofPath = Path.Combine(directory, "proof.json");
            await File.WriteAllTextAsync(proofPath, proof.ToJsonString());
            if (scenario == "existing-certificate")
                await store.SetAsync(SlotRelease.Key(original), new JsonObject { ["retained"] = true });
            var options = CliOptions.Parse([
                "review",
                "reconcile-slot",
                "--repo",
                repo.DisplayName,
                "--pr",
                "17",
                "--db",
                database,
                "--evidence",
                proofPath,
                "--execute",
            ]);
            var run = () => SlotRelease.RunAsync(options, CancellationToken.None);
            if (scenario == "valid")
            {
                (await run()).Should().Be(0);
                (await store.GetAsync<JsonObject>(ownerKey))!["state"]!.GetValue<string>().Should().Be("released");
                (await store.GetAsync<JsonObject>(SlotRelease.Key(original))).Should().NotBeNull();
                SlotRelease.Allows(proof, original, "18", "session").Should().BeTrue();
                SlotRelease.Allows(proof, original, "17", "session").Should().BeFalse();
                SlotRelease.Allows(proof, original, "18", "other-session").Should().BeFalse();
                var next = attempt.DeepClone().AsObject();
                next["prId"] = "18";
                await SlotRelease.ClaimAsync(
                    connection,
                    ownerKey,
                    next,
                    null,
                    "reviewd/review-attempt/" + repo.NormalizedKey + "/18",
                    CancellationToken.None
                );
            }
            else
            {
                await run.Should().ThrowAsync<Exception>();
                (await store.GetAsync<JsonObject>(ownerKey))!.ToJsonString().Should().Be(owner.ToJsonString());
                var certificate = await store.GetAsync<JsonObject>(SlotRelease.Key(original));
                if (scenario == "existing-certificate")
                    certificate!["retained"]!.GetValue<bool>().Should().BeTrue();
                else
                    certificate.Should().BeNull();
            }
            using var read = connection.CreateCommand();
            read.CommandText = "SELECT value FROM cache WHERE key=@key";
            read.Parameters.AddWithValue("@key", attemptKey);
            (await read.ExecuteScalarAsync()).Should().Be(original);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Reset_retirement_reconciliation_releases_the_owner_and_allows_a_fresh_claim()
    {
        // Regression: an admitted review that was reset+archived used to keep its SQLite owner at
        // state="owned" (certificate only), so ClaimAsync refused every fresh PR and the slot was
        // permanently stuck. reconcile-slot must now release that owner so the slot is reusable.
        var directory = Path.Combine(Path.GetTempPath(), "reviewd-retire-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "state.db");
        var repo = PrCommands.ParseRepo("org/project/WeveNova");
        var attemptKey = "reviewd/review-attempt/" + repo.NormalizedKey + "/17";
        var ownerKey = SlotRelease.OwnerKey("workspace", repo.NormalizedKey, 3);
        try
        {
            using var connection = PrThreadBindings.Connection(database);
            var store = new SqliteKvStore(connection);
            var attempt = new JsonObject
            {
                ["repository"] = repo.DisplayName,
                ["workspaceId"] = "workspace",
                ["sessionId"] = "session",
                ["prId"] = "17",
                ["slot"] = 3,
                ["stage"] = "review-running",
                ["claimToken"] = "claim",
                ["reviewInputId"] = "input",
                ["confirmationInputId"] = "confirm",
            };
            var original = attempt.ToJsonString();
            var owner = attempt.DeepClone().AsObject();
            owner["state"] = "owned";
            await store.SetAsync(attemptKey, attempt);
            await store.SetAsync(ownerKey, owner);

            var digest = SlotRelease.Digest(original);
            var now = DateTimeOffset.UtcNow;
            var reset = new JsonObject
            {
                ["attemptDigest"] = digest,
                ["workspaceId"] = "workspace",
                ["sessionId"] = "recovery",
                ["repository"] = repo.DisplayName,
                ["slot"] = 3,
                ["operationId"] = "reset-op",
                ["status"] = "succeeded",
                ["exitCode"] = 0,
                ["dryRun"] = false,
                ["cleanBaselineVerified"] = true,
                ["writersRetiredAt"] = now.AddMinutes(-3),
                ["startedAt"] = now.AddMinutes(-2),
                ["finishedAt"] = now.AddMinutes(-1),
            };
            var archive = new JsonObject
            {
                ["attemptDigest"] = digest,
                ["files"] = new JsonArray(new JsonObject { ["path"] = "f", ["sha256"] = "h" }),
                ["verified"] = true,
                ["finishedAt"] = now.AddMinutes(-3),
            };
            var resetPath = Path.Combine(directory, "reset.json");
            var archivePath = Path.Combine(directory, "archive.json");
            await File.WriteAllTextAsync(resetPath, reset.ToJsonString());
            await File.WriteAllTextAsync(archivePath, archive.ToJsonString());

            var proof = attempt.DeepClone().AsObject();
            proof["schemaVersion"] = 1;
            proof["attemptDigest"] = digest;
            proof["classification"] = "operator-retired-after-reset";
            proof["recoverySessionId"] = "recovery";
            proof["writersRetired"] = true;
            proof["producersStopped"] = true;
            proof["threadQuiescent"] = true;
            proof["nativeQuiescent"] = true;
            proof["operatorReason"] = "Admitted review reset and archived; owner reclaimed for reuse.";
            proof["resetReceiptPath"] = resetPath;
            proof["archiveManifestPath"] = archivePath;
            proof["evidence"] = new JsonArray(
                new JsonObject { ["path"] = resetPath, ["sha256"] = SlotRelease.Digest(reset.ToJsonString()) },
                new JsonObject { ["path"] = archivePath, ["sha256"] = SlotRelease.Digest(archive.ToJsonString()) }
            );
            var proofPath = Path.Combine(directory, "proof.json");
            await File.WriteAllTextAsync(proofPath, proof.ToJsonString());

            var options = CliOptions.Parse([
                "review",
                "reconcile-slot",
                "--repo",
                repo.DisplayName,
                "--pr",
                "17",
                "--db",
                database,
                "--evidence",
                proofPath,
                "--execute",
            ]);
            (await SlotRelease.RunAsync(options, CancellationToken.None)).Should().Be(0);

            // Owner released, certificate recorded, retained attempt byte-unchanged.
            (await store.GetAsync<JsonObject>(ownerKey))!["state"]!
                .GetValue<string>()
                .Should()
                .Be("released");
            (await store.GetAsync<JsonObject>(SlotRelease.Key(original))).Should().NotBeNull();
            using var read = connection.CreateCommand();
            read.CommandText = "SELECT value FROM cache WHERE key=@key";
            read.Parameters.AddWithValue("@key", attemptKey);
            (await read.ExecuteScalarAsync()).Should().Be(original);

            // The released slot now accepts a fresh different-PR claim.
            var next = attempt.DeepClone().AsObject();
            next["prId"] = "18";
            await SlotRelease.ClaimAsync(
                connection,
                ownerKey,
                next,
                null,
                "reviewd/review-attempt/" + repo.NormalizedKey + "/18",
                CancellationToken.None
            );
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(503, "hierarchy_busy", 2, true)]
    // Minutes of busy under a full batch are normal and must not abort a live review.
    [InlineData(503, "hierarchy_busy", 120, true)]
    [InlineData(503, "hierarchy_busy", ReviewRunner.MaxConsecutiveBusyReads + 8, false)]
    [InlineData(503, "other", 1, false)]
    [InlineData(500, "hierarchy_busy", 1, false)]
    public async Task Hierarchy_busy_retries_are_bounded_and_require_two_new_idle_snapshots(
        int code,
        string error,
        int busyCount,
        bool succeeds
    )
    {
        var reads = 0;
        var delays = 0;
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/run-state", """{"threadId":"thread","isInProgress":false,"currentRunId":null}""")
            .On(
                request => request.RequestUri!.AbsolutePath.EndsWith("/subagents", StringComparison.Ordinal),
                _ =>
                {
                    reads++;
                    return reads > 1 && reads <= busyCount + 1
                        ? new HttpResponseMessage((HttpStatusCode)code)
                        {
                            Content = new StringContent($$"""{"code":"{{error}}"}"""),
                        }
                        : Json("""{"schemaVersion":1,"nodes":[]}""");
                }
            );
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var client = new LmStreamingS2SClient(http, null, null, null);
        var run = () =>
            ReviewRunner.WaitAsync(
                client,
                "thread",
                null,
                _ =>
                {
                    delays++;
                    return Task.CompletedTask;
                },
                CancellationToken.None
            );
        if (succeeds)
        {
            await run();
            reads.Should().Be(busyCount + 3);
            delays.Should().Be(busyCount + 2);
        }
        else
        {
            await run.Should().ThrowAsync<HttpRequestException>();
            reads.Should().Be(error == "hierarchy_busy" && code == 503 ? ReviewRunner.MaxConsecutiveBusyReads + 2 : 2);
        }
        handler.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
    }

    [Fact]
    public async Task Successful_hierarchy_reads_reset_the_consecutive_busy_budget()
    {
        var reads = 0;
        var handler = new FakeHttpMessageHandler()
            .On(
                request => request.RequestUri!.AbsolutePath.EndsWith("/run-state", StringComparison.Ordinal),
                _ =>
                    Json(
                        reads < 40
                            ? """{"threadId":"thread","isInProgress":true,"currentRunId":"run"}"""
                            : """{"threadId":"thread","isInProgress":false,"currentRunId":null}"""
                    )
            )
            .On(
                request => request.RequestUri!.AbsolutePath.EndsWith("/subagents", StringComparison.Ordinal),
                _ =>
                {
                    reads++;
                    return reads <= 40 && reads % 2 == 0
                        ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                        {
                            Content = new StringContent("""{"code":"hierarchy_busy"}"""),
                        }
                        : Json("""{"schemaVersion":1,"nodes":[]}""");
                }
            );
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var client = new LmStreamingS2SClient(http, null, null, null);
        await ReviewRunner.WaitAsync(client, "thread", null, _ => Task.CompletedTask, CancellationToken.None);
        reads.Should().Be(42);
        handler.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
    }

    // Seen live: a review parked on AskUserQuestion took the confirmation as its answer. The host ran it, but
    // never folded the input into a run, so the input read NotStarted on an idle thread for hours and the
    // batch, its slot and its whole store waited forever. The wait reports it lost, a type the runner catches
    // to go on to confirmation and verify. One clock minute passes per poll.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_input_that_never_runs_on_an_idle_thread_is_reported_lost(bool stays)
    {
        var reads = 0;
        var handler = new FakeHttpMessageHandler()
            .On(
                request => request.RequestUri!.AbsolutePath.EndsWith("/status", StringComparison.Ordinal),
                _ =>
                    Json(
                        stays || reads < 5
                            ? """{"threadId":"thread","runId":null,"status":"NotStarted","response":null}"""
                            : """{"threadId":"thread","runId":"run","status":"Completed","response":null}"""
                    )
            )
            .OnJson(HttpMethod.Get, "/run-state", """{"threadId":"thread","isInProgress":false,"currentRunId":null}""")
            .On(
                request => request.RequestUri!.AbsolutePath.EndsWith("/subagents", StringComparison.Ordinal),
                _ =>
                {
                    reads++;
                    return Json("""{"schemaVersion":1,"nodes":[]}""");
                }
            );
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var client = new LmStreamingS2SClient(http, null, null, null);
        var now = DateTimeOffset.Parse("2026-10-19T08:00:00Z", CultureInfo.InvariantCulture);
        var run = () =>
            ReviewRunner.WaitAsync(
                client,
                "thread",
                "input",
                _ =>
                {
                    if (reads > 100)
                        throw new TimeoutException("Still waiting.");
                    now = now.AddMinutes(1);
                    return Task.CompletedTask;
                },
                CancellationToken.None,
                () => now
            );
        if (stays)
        {
            await run.Should().ThrowAsync<ReviewRunner.LostInputException>().WithMessage("Input input never started*");
            reads.Should().Be((int)ReviewRunner.NeverStartedGrace.TotalMinutes + 1);
        }
        else
        {
            (await run())!.Status.Should().Be("Completed");
        }
        handler.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
    }

    // Seen live: a grader stamped "error" beside a completed publisher. A finished child that did not succeed
    // is not a failed review, so the wait settles instead of throwing. A running child keeps the wait however
    // old its activity time looks: that time is not refreshed per turn (one read four hours "idle" while it
    // was still writing messages).
    [Theory]
    [InlineData("error", 0, true)]
    [InlineData("stopped", 0, true)]
    [InlineData("running", 240, false)]
    [InlineData("running", 0, false)]
    [InlineData("mystery", 2, true)]
    [InlineData("mystery", 0, false)]
    public async Task Terminal_descendant_stamps_settle_the_wait_but_running_ones_keep_it(
        string state,
        int idleMinutes,
        bool settles
    )
    {
        var activity = DateTimeOffset.UtcNow.AddMinutes(-idleMinutes).ToString("O");
        var failure = state == "error" ? ",\"failureCode\":\"model_error\"" : "";
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/run-state", """{"threadId":"thread","isInProgress":false,"currentRunId":null}""")
            .OnJson(
                HttpMethod.Get,
                "/subagents?",
                $$"""{"schemaVersion":1,"nodes":[{"agentId":"publisher","threadId":"publisher-thread","parentThreadId":"thread","depth":1,"template":"reviewer","status":"completed"},{"agentId":"left","threadId":"left-thread","parentThreadId":"thread","depth":1,"template":"reviewer","status":"{{state}}","lastActivityUtc":"{{activity}}"{{failure}}}]}"""
            );
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var client = new LmStreamingS2SClient(http, null, null, null);
        var delays = 0;
        var run = () =>
            ReviewRunner.WaitAsync(
                client,
                "thread",
                null,
                _ => ++delays > 20 ? throw new TimeoutException("Descendant still active.") : Task.CompletedTask,
                CancellationToken.None
            );
        if (settles)
        {
            await run();
            delays.Should().Be(1);
        }
        else
        {
            await run.Should().ThrowAsync<TimeoutException>();
        }
    }

    // Seen live on PR 221: the confirmation read NotStarted for four hours on an idle conversation, and the
    // batch never reached the next PR. One clock minute passes per poll.
    [Theory]
    [InlineData(false, "completed", true)]
    [InlineData(true, "completed", false)]
    [InlineData(false, "running", false)]
    public async Task An_input_that_never_starts_on_a_settled_idle_conversation_fails_after_the_grace(
        bool rootInProgress,
        string childStatus,
        bool fails
    )
    {
        var runState = rootInProgress
            ? """{"threadId":"thread","isInProgress":true,"currentRunId":"run"}"""
            : """{"threadId":"thread","isInProgress":false,"currentRunId":null}""";
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/status?", """{"status":"NotStarted","runId":null,"response":null}""")
            .OnJson(HttpMethod.Get, "/run-state", runState)
            .OnJson(
                HttpMethod.Get,
                "/subagents?",
                $$"""{"schemaVersion":1,"nodes":[{"agentId":"child","threadId":"child-thread","parentThreadId":"thread","depth":1,"template":"reviewer","status":"{{childStatus}}"}]}"""
            );
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var client = new LmStreamingS2SClient(http, null, null, null);
        var now = DateTimeOffset.Parse("2026-10-19T08:00:00Z", CultureInfo.InvariantCulture);
        var delays = 0;
        var run = () =>
            ReviewRunner.WaitAsync(
                client,
                "thread",
                "input-1",
                _ =>
                {
                    if (++delays > 30)
                        throw new TimeoutException("Still waiting.");
                    now = now.AddMinutes(1);
                    return Task.CompletedTask;
                },
                CancellationToken.None,
                () => now
            );

        if (fails)
        {
            (await run.Should().ThrowAsync<InvalidOperationException>())
                .Which.Message.Should()
                .Contain("never started");
            delays.Should().Be((int)ReviewRunner.NeverStartedGrace.TotalMinutes);
        }
        else
        {
            await run.Should().ThrowAsync<TimeoutException>();
        }
    }

    [Fact]
    public async Task An_input_that_starts_inside_the_grace_completes_normally()
    {
        var reads = 0;
        var handler = new FakeHttpMessageHandler()
            .On(
                request => request.RequestUri!.AbsolutePath.EndsWith("/status", StringComparison.Ordinal),
                _ =>
                    ++reads < (int)ReviewRunner.NeverStartedGrace.TotalMinutes
                        ? Json("""{"status":"NotStarted","runId":null,"response":null}""")
                        : Json("""{"status":"Completed","runId":"run","response":{"text":"done"}}""")
            )
            .OnJson(HttpMethod.Get, "/run-state", """{"threadId":"thread","isInProgress":false,"currentRunId":null}""")
            .OnJson(HttpMethod.Get, "/subagents?", """{"schemaVersion":1,"nodes":[]}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var client = new LmStreamingS2SClient(http, null, null, null);
        var now = DateTimeOffset.Parse("2026-10-19T08:00:00Z", CultureInfo.InvariantCulture);

        var status = await ReviewRunner.WaitAsync(
            client,
            "thread",
            "input-1",
            _ =>
            {
                now = now.AddMinutes(1);
                return Task.CompletedTask;
            },
            CancellationToken.None,
            () => now
        );

        status!.ResponseText.Should().Be("done");
    }

    [Fact]
    public async Task A_busy_root_between_two_idle_stretches_restarts_the_grace()
    {
        // Two idle NotStarted stretches of 7 polls each, split by a busy root, then completion.
        // Together they pass the 10-minute grace; each alone does not.
        var polls = 0;
        var handler = new FakeHttpMessageHandler()
            .On(
                request => request.RequestUri!.AbsolutePath.EndsWith("/status", StringComparison.Ordinal),
                _ =>
                    ++polls < 22
                        ? Json("""{"status":"NotStarted","runId":null,"response":null}""")
                        : Json("""{"status":"Completed","runId":"run","response":{"text":"done"}}""")
            )
            .On(
                request => request.RequestUri!.AbsolutePath.EndsWith("/run-state", StringComparison.Ordinal),
                _ =>
                    polls is >= 9 and <= 13
                        ? Json("""{"threadId":"thread","isInProgress":true,"currentRunId":"other"}""")
                        : Json("""{"threadId":"thread","isInProgress":false,"currentRunId":null}""")
            )
            .OnJson(HttpMethod.Get, "/subagents?", """{"schemaVersion":1,"nodes":[]}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var client = new LmStreamingS2SClient(http, null, null, null);
        var now = DateTimeOffset.Parse("2026-10-19T08:00:00Z", CultureInfo.InvariantCulture);

        var status = await ReviewRunner.WaitAsync(
            client,
            "thread",
            "input-1",
            _ =>
            {
                now = now.AddMinutes(1);
                return Task.CompletedTask;
            },
            CancellationToken.None,
            () => now
        );

        status!.ResponseText.Should().Be("done");
        polls.Should().Be(23, "completion is confirmed by a second idle read");
    }

    [Fact]
    public async Task A_busy_hierarchy_between_two_idle_stretches_restarts_the_grace()
    {
        // Seven idle NotStarted polls, two hierarchy_busy polls, then idle again until completion. Counted as
        // one stretch, the second idle run would trip the 10-minute grace on its second poll.
        var polls = 0;
        var handler = new FakeHttpMessageHandler()
            .On(
                request => request.RequestUri!.AbsolutePath.EndsWith("/status", StringComparison.Ordinal),
                _ =>
                    ++polls < 16
                        ? Json("""{"status":"NotStarted","runId":null,"response":null}""")
                        : Json("""{"status":"Completed","runId":"run","response":{"text":"done"}}""")
            )
            .OnJson(HttpMethod.Get, "/run-state", """{"threadId":"thread","isInProgress":false,"currentRunId":null}""")
            .On(
                request => request.RequestUri!.AbsolutePath.EndsWith("/subagents", StringComparison.Ordinal),
                _ =>
                    polls is 8 or 9
                        ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                        {
                            Content = new StringContent("""{"code":"hierarchy_busy"}"""),
                        }
                        : Json("""{"schemaVersion":1,"nodes":[]}""")
            );
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var client = new LmStreamingS2SClient(http, null, null, null);
        var now = DateTimeOffset.Parse("2026-10-19T08:00:00Z", CultureInfo.InvariantCulture);

        var status = await ReviewRunner.WaitAsync(
            client,
            "thread",
            "input-1",
            _ =>
            {
                now = now.AddMinutes(1);
                return Task.CompletedTask;
            },
            CancellationToken.None,
            () => now
        );

        status!.ResponseText.Should().Be("done");
        polls.Should().Be(17, "completion is confirmed by a second idle read");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("/app/CodeReviewDaemon.Sample")]
    [InlineData("dotnet /app/CodeReviewDaemon.Sample.dll")]
    public void Producer_scan_skips_exited_process_and_still_detects_later_competitor(string? competitor)
    {
        if (!OperatingSystem.IsLinux())
            return;
        var reads = new List<string>();
        var scan = () =>
            ReviewBatch.EnsureNoDaemon(
                () => ["/proc/non-process", "/proc/11", "/proc/12"],
                path =>
                {
                    reads.Add(path);
                    if (path == "/proc/11/cmdline")
                        throw new IOException("No such process", 3);
                    return CommandLine(competitor ?? "dotnet /app/Reviewd.Sample.dll");
                }
            );
        if (competitor is not null)
            scan.Should().Throw<InvalidOperationException>().WithMessage("Competing daemon producer detected.");
        else
            scan.Should().NotThrow();
        reads.Should().Equal("/proc/11/cmdline", "/proc/12/cmdline");
    }

    // Kay9: only a process running the daemon competes, never a tool that names its folder or file. Seen live: a
    // grep over the daemon's source folder stopped every running batch.
    [Theory]
    [InlineData("grep -rn Lease samples/CodeReviewDaemon.Sample", false)]
    [InlineData("ls samples/CodeReviewDaemon.Sample", false)]
    [InlineData("git -C /repo log -- samples/CodeReviewDaemon.Sample", false)]
    [InlineData("code /x/CodeReviewDaemon.Sample.dll", false)]
    [InlineData("dotnet test tests/CodeReviewDaemon.Sample.Tests/CodeReviewDaemon.Sample.Tests.csproj", false)]
    [InlineData("dotnet /x/Reviewd.Sample.dll /x/CodeReviewDaemon.Sample.dll", false)]
    [InlineData("dotnet /x/CodeReviewDaemon.Sample.dll", true)]
    [InlineData("dotnet exec --depsfile a.json /x/CodeReviewDaemon.Sample.dll", true)]
    [InlineData("/usr/bin/dotnet exec --runtimeconfig b.dll --depsfile a.json /x/CodeReviewDaemon.Sample.dll", true)]
    [InlineData("/x/CodeReviewDaemon.Sample --serve", true)]
    public void Producer_scan_counts_only_a_process_running_the_daemon(string commandLine, bool competes)
    {
        if (!OperatingSystem.IsLinux())
            return;
        var scan = () => ReviewBatch.EnsureNoDaemon(() => ["/proc/12"], _ => CommandLine(commandLine));
        if (competes)
            scan.Should().Throw<InvalidOperationException>().WithMessage("Competing daemon producer detected.");
        else
            scan.Should().NotThrow();
    }

    // /proc/<pid>/cmdline: each argument ends with a NUL.
    private static byte[] CommandLine(string spaced) =>
        System.Text.Encoding.UTF8.GetBytes(string.Concat(spaced.Split(' ').Select(argument => argument + "\0")));

    [Theory]
    [InlineData("Reviewd.Sample", false)]
    [InlineData("CodeReviewDaemon.Sample", true)]
    [InlineData("codereviewdaemon.sample", true)]
    [InlineData("CodeReviewDaemon.Sample.Tests", false)]
    public void Windows_producer_scan_detects_the_daemon_apphost_by_process_name(string name, bool competing)
    {
        var scan = () => ReviewBatch.EnsureNoWindowsDaemon(() => ["svchost", name]);
        if (competing)
            scan.Should().Throw<InvalidOperationException>().WithMessage("Competing daemon producer detected.");
        else
            scan.Should().NotThrow();
    }

    [Theory]
    [InlineData("file")]
    [InlineData("directory")]
    [InlineData("io")]
    [InlineData("eperm")]
    [InlineData("eacces")]
    [InlineData("default-io")]
    [InlineData("windows-path")]
    [InlineData("access")]
    [InlineData("enumeration")]
    public void Producer_scan_only_skips_missing_process_reads_and_fails_closed_otherwise(string failure)
    {
        if (!OperatingSystem.IsLinux())
            return;
        Exception error = failure switch
        {
            "file" => new FileNotFoundException(),
            "directory" => new DirectoryNotFoundException(),
            "eperm" => new IOException("Operation not permitted", 1),
            "eacces" => new IOException("Permission denied", 13),
            "default-io" => new IOException("Generic I/O failure"),
            "windows-path" => new IOException("Path not found", unchecked((int)0x80070003)),
            "access" => new UnauthorizedAccessException(),
            "enumeration" => new IOException("Enumeration failed", 3),
            _ => new IOException("I/O failure", 5),
        };
        var reads = 0;
        var scan = () =>
            ReviewBatch.EnsureNoDaemon(
                () => failure == "enumeration" ? throw error : new[] { "/proc/11", "/proc/12" },
                _ =>
                {
                    if (++reads == 1)
                        throw error;
                    return [];
                }
            );
        if (failure is "file" or "directory")
        {
            scan.Should().NotThrow();
            reads.Should().Be(2);
        }
        else
        {
            scan.Should().Throw<Exception>().Which.Should().BeSameAs(error);
            reads.Should().Be(failure == "enumeration" ? 0 : 1);
        }
    }

    [Fact]
    public async Task Batch_requests_are_write_once_across_connections_and_do_not_replay()
    {
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-request-{Guid.NewGuid():N}.db");
        try
        {
            using (var connection = PrThreadBindings.Connection(database))
                _ = new SqliteKvStore(connection);
            const string request = "{\"repositories\":{},\"lanes\":{}}";
            var digest = SlotRelease.Digest(request);
            (await ReviewBatch.ClaimAsync(database, digest, request, CancellationToken.None)).Should().BeTrue();
            (await ReviewBatch.ClaimAsync(database, digest, request, CancellationToken.None)).Should().BeFalse();
            (
                await ReviewBatch.ClaimAsync(
                    database,
                    SlotRelease.Digest(request + " "),
                    request + " ",
                    CancellationToken.None
                )
            )
                .Should()
                .BeTrue();
            (await ReviewBatch.ClaimAsync(database, digest, request, CancellationToken.None)).Should().BeFalse();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("xhigh")]
    public async Task Batch_effort_is_optional_forwarded_and_bound_to_execution_scope(string? effort)
    {
        using var batch = new BatchFixture(0) { ReasoningEffort = effort };
        await batch.ExecuteAsync();
        batch.RunEfforts.Should().Equal(effort, effort, effort);
        var scope = batch.Read(batch.StateKey)!["scope"]!.AsObject();
        scope.ContainsKey("reasoningEffort").Should().Be(effort is not null);
        var recordedEffort = scope["reasoningEffort"]?.GetValue<string>();
        recordedEffort.Should().Be(effort);
        batch.Restart();
        await batch.ExecuteAsync();
        batch.Runs.Should().HaveCount(3, "same scope skips verified work");
        batch.ReasoningEffort = effort is null ? "xhigh" : "high";
        var changed = () => batch.ExecuteAsync();
        await changed.Should().ThrowAsync<InvalidOperationException>().WithMessage("*scope changed*");
        batch.Runs.Should().HaveCount(3);
    }

    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("xhigh")]
    [InlineData("extreme")]
    [InlineData("")]
    [InlineData("XHIGH")]
    public void Review_cli_accepts_only_explicit_effort_vocabulary(string effort)
    {
        var parse = () =>
            CliOptions.Parse([
                "review",
                "run",
                "--repo",
                "org/project/Nova",
                "--pr",
                "17",
                "--slot",
                "2",
                "--db",
                "fixture",
                "--session",
                "session",
                "--workspace",
                "workspace",
                "--provider",
                "test",
                "--reasoning-effort",
                effort,
            ]);
        if (effort is "low" or "medium" or "high" or "xhigh")
            parse().Get("reasoning-effort").Should().Be(effort);
        else
            parse.Should().Throw<ArgumentException>().WithMessage("*reasoning-effort must*");
    }

    [Fact]
    public async Task Batch_requeues_failures_without_releasing_admitted_owners_or_stopping_siblings()
    {
        using var batch = new BatchFixture(0, 1);
        batch.PutAttempt(
            "1",
            new JsonObject
            {
                ["slot"] = 0,
                ["stage"] = "review-running",
                ["reviewInputId"] = "original",
                ["mergeSha"] = new string('b', 40),
            }
        );
        var checks = 0;
        batch.Eligibility = pr =>
        {
            var live = BatchFixture.Live(pr);
            if (pr == "1" && ++checks == 1)
                live.Remove("mergeSha");
            return live;
        };
        await batch.ExecuteAsync();
        batch.Runs.Should().Contain("1:resume").And.Contain("2:fresh").And.Contain("3:fresh");
        batch.Runs.IndexOf("1:resume").Should().BeLessThan(batch.Runs.IndexOf("3:fresh"));
        batch.Entries().Should().OnlyContain(entry => entry["status"]!.GetValue<string>() == "verified");
    }

    [Fact]
    public async Task Batch_reviews_the_new_head_of_a_pr_this_db_already_reviewed()
    {
        using var batch = new BatchFixture(0);
        batch.PutAttempt(
            "1",
            new JsonObject
            {
                ["slot"] = 0,
                ["stage"] = "completed-verified",
                ["cleanupState"] = "released",
                ["claimToken"] = "earlier-claim",
                ["reviewedHeadSha"] = new string('9', 40),
                ["mergeSha"] = new string('9', 40),
                ["artifactCommit"] = new JsonObject { ["status"] = "committed", ["commit"] = new string('c', 40) },
            }
        );
        await batch.ExecuteAsync();
        batch.Runs.Should().Equal("1:fresh", "2:fresh", "3:fresh");
        batch.Entries().Should().OnlyContain(entry => entry["status"]!.GetValue<string>() == "verified");
    }

    [Fact]
    public async Task Batch_waits_for_a_slot_another_pr_still_owns_instead_of_burning_the_lane()
    {
        using var batch = new BatchFixture(0);
        batch.Put(
            SlotRelease.OwnerKey("workspace", PrCommands.ParseRepo("org/project/Nova").NormalizedKey, 0),
            new JsonObject
            {
                ["prId"] = "99",
                ["state"] = "owned",
                ["claimToken"] = "earlier-batch",
            }
        );
        await batch.ExecuteAsync();
        batch.Runs.Should().BeEmpty();
        batch.Entries().Should().OnlyContain(entry => entry["status"]!.GetValue<string>() == "queued");
    }

    [Fact]
    public async Task Batch_keeps_a_lane_running_past_many_prs_another_run_already_reviewed()
    {
        using var batch = new BatchFixture(0);
        batch.PutSelection(7);
        batch.OnRunAsync = () =>
            batch.Runs[^1] != "7:fresh"
                ? throw new ReviewRunner.ReviewNotAdmittedException(
                    "PR head was already reviewed by another run; skipped."
                )
                : Task.CompletedTask;
        await batch.ExecuteAsync();
        batch.Runs.Should().HaveCount(7).And.EndWith("7:fresh");
        batch.Entries().Last()["status"]!.GetValue<string>().Should().Be("verified");
    }

    [Fact]
    public async Task Batch_skips_a_held_pr_and_keeps_its_lane_running()
    {
        using var batch = new BatchFixture(0);
        batch.OnRunAsync = () =>
            batch.Runs[^1] == "1:fresh"
                ? throw new InvalidOperationException("PR is not eligible for review.")
                : Task.CompletedTask;
        await batch.ExecuteAsync();
        batch.Runs.Should().Equal("1:fresh", "2:fresh", "3:fresh");
        batch
            .Entries()
            .Select(entry => entry["status"]!.GetValue<string>())
            .Should()
            .Equal("held", "verified", "verified");
    }

    [Theory]
    [InlineData(true, 3, "quarantined")]
    [InlineData(false, 5, "held")]
    public async Task Batch_quarantines_a_pr_after_three_identical_failures_on_one_head(
        bool identical,
        int expectedRuns,
        string expectedStatus
    )
    {
        // Seen live: one PR failed setup the same way 88 times, spending a lane each pick. The same failure three
        // times on one head, each a selection apart, will not clear by retrying; a different failure still gets every
        // retry.
        using var batch = new BatchFixture(0);
        batch.PutAttempt(
            "1",
            new JsonObject
            {
                ["slot"] = 0,
                ["stage"] = "failed-released",
                ["cleanupState"] = "released",
                ["claimToken"] = "failed-claim",
            }
        );
        var failures = 0;
        batch.OnRunAsync = () =>
        {
            if (batch.Runs[^1] != "1:fresh")
                return Task.CompletedTask;
            batch.AdvanceSeconds((int)ReviewBatch.QuarantineSpacing.TotalSeconds);
            throw new InvalidOperationException(
                identical || ++failures % 2 == 0
                    ? $"Sandbox prepare failed (exit 1, operation {Guid.NewGuid():N}); Setup stopped with exit 2."
                    : $"Sandbox verify failed (exit 1, operation {Guid.NewGuid():N}); receipt missing."
            );
        };

        await batch.ExecuteAsync();

        batch.Runs.Count(run => run == "1:fresh").Should().Be(expectedRuns);
        batch.Entries()[0]["status"]!.GetValue<string>().Should().Be(expectedStatus);
        batch.Entries().Skip(1).Should().OnlyContain(entry => entry["status"]!.GetValue<string>() == "verified");
    }

    [Fact]
    public async Task Batch_counts_identical_failures_inside_the_spacing_once()
    {
        // A short outage fails every retry of one selection the same way. That says nothing about the PR, so it is one
        // failure, not a quarantine.
        using var batch = new BatchFixture(0);
        batch.PutAttempt(
            "1",
            new JsonObject
            {
                ["slot"] = 0,
                ["stage"] = "failed-released",
                ["cleanupState"] = "released",
                ["claimToken"] = "failed-claim",
            }
        );
        batch.OnRunAsync = () =>
            batch.Runs[^1] == "1:fresh"
                ? throw new InvalidOperationException(
                    $"Sandbox prepare failed (exit 1, operation {Guid.NewGuid():N}); gateway timed out."
                )
                : Task.CompletedTask;

        await batch.ExecuteAsync();

        batch.Runs.Count(run => run == "1:fresh").Should().Be(5);
        batch.Entries()[0]["status"]!.GetValue<string>().Should().Be("held");
        batch.Read(ReviewBatch.FailureKey(BatchFixture.Repo, "1"))!["count"]!.GetValue<int>().Should().Be(1);
    }

    [Theory]
    [InlineData("resume")]
    [InlineData("slot-not-idle")]
    public async Task Batch_does_not_count_failures_that_say_nothing_about_the_pr(string kind)
    {
        // Seen live: a review whose input was lost re-verified on every resume, five times; only a fresh admission can
        // replace it, so quarantining it would skip a PR that would pass. A held slot fails every PR the same way.
        using var batch = new BatchFixture(0);
        batch.PutAttempt(
            "1",
            kind == "resume"
                ? new JsonObject
                {
                    ["slot"] = 0,
                    ["stage"] = "review-running",
                    ["cleanupState"] = "owned",
                    ["claimToken"] = "lost-input",
                    ["reviewInputId"] = "input-never-run",
                }
                : new JsonObject
                {
                    ["slot"] = 0,
                    ["stage"] = "failed-released",
                    ["cleanupState"] = "released",
                    ["claimToken"] = "failed-claim",
                }
        );
        batch.OnRunAsync = () =>
        {
            if (!batch.Runs[^1].StartsWith("1:", StringComparison.Ordinal))
                return Task.CompletedTask;
            batch.AdvanceSeconds((int)ReviewBatch.QuarantineSpacing.TotalSeconds);
            throw kind == "resume"
                ? new InvalidOperationException($"Sandbox verify failed (exit 1, operation {Guid.NewGuid():N}).")
                : new ReviewRunner.SlotUnavailableException("Native slot is not idle: orphaned owner marker.");
        };

        await batch.ExecuteAsync();

        batch.Runs.Should().Contain(kind == "resume" ? "1:resume" : "1:fresh");
        batch.Entries()[0]["status"]!.GetValue<string>().Should().Be("held");
        batch.Read(ReviewBatch.FailureKey(BatchFixture.Repo, "1")).Should().BeNull();
    }

    [Fact]
    public async Task Batch_counts_a_failure_that_still_holds_its_slot()
    {
        // Seen live: a prepare that timed out kept its slot each time, so it was held, freed by the reaper, picked
        // again and timed out again. Those failures count too; the pre-launch check skips the head once the slot is free.
        using var batch = new BatchFixture(0);
        batch.OnRunAsync = () =>
        {
            if (batch.Runs[^1] != "1:fresh")
                return Task.CompletedTask;
            batch.PutAttempt(
                "1",
                new JsonObject
                {
                    ["slot"] = 0,
                    ["stage"] = "prepare-pending",
                    ["cleanupState"] = "owned",
                    ["claimToken"] = "timed-out",
                }
            );
            throw new InvalidOperationException(
                "The sandbox gateway's execution timeout elapsed before the command completed."
            );
        };

        await batch.ExecuteAsync();

        batch.Entries()[0]["status"]!.GetValue<string>().Should().Be("held", "its slot is still owned");
        batch.Read(ReviewBatch.FailureKey(BatchFixture.Repo, "1"))!["count"]!.GetValue<int>().Should().Be(1);
    }

    [Theory]
    [InlineData('a', false)]
    [InlineData('c', true)]
    public async Task Batch_skips_a_quarantined_head_without_spending_a_lane(char failedHead, bool runs)
    {
        // The selection's head is 'a'. A quarantine recorded for head 'a' skips it; one for an older head does not.
        using var batch = new BatchFixture(0);
        batch.Put(
            ReviewBatch.FailureKey(BatchFixture.Repo, "1"),
            new JsonObject
            {
                ["head"] = new string(failedHead, 40),
                ["signature"] = "Sandbox prepare failed",
                ["count"] = ReviewBatch.QuarantineAfter,
                ["reason"] = "Sandbox prepare failed",
            }
        );

        await batch.ExecuteAsync();

        batch.Runs.Contains("1:fresh").Should().Be(runs);
        batch.Entries()[0]["status"]!.GetValue<string>().Should().Be(runs ? "verified" : "quarantined");
        // A verified review settles the PR, so its old failure history goes with it.
        (batch.Read(ReviewBatch.FailureKey(BatchFixture.Repo, "1")) is null)
            .Should()
            .Be(runs);
    }

    [Fact]
    public void Failure_signature_ignores_operation_ids_and_numbers()
    {
        ReviewBatch
            .FailureSignature(
                "Sandbox prepare failed (exit 1, operation 7ba6394395f64c58aa9636e5111d7697) at 2026-10-20T15:10:51"
            )
            .Should()
            .Be(
                ReviewBatch.FailureSignature(
                    "Sandbox prepare failed (exit 1, operation 5ad6c1a398e14dd19c4c8196527796cb) at 2026-10-21T01:02:03"
                )
            );
        ReviewBatch
            .FailureSignature("Sandbox prepare failed (exit 1)")
            .Should()
            .NotBe(ReviewBatch.FailureSignature("Sandbox verify failed (exit 1)"));
        // The output tail is cut at a fixed length, so where it starts shifts between tries; its last line does not.
        const string tail = "\n--- sandbox output tail ---\n";
        ReviewBatch
            .FailureSignature(
                "Sandbox prepare failed (exit 1)." + tail + "…N: checking 9.8s\nReviewError: Setup stopped"
            )
            .Should()
            .Be(
                ReviewBatch.FailureSignature(
                    "Sandbox prepare failed (exit 1)." + tail + "…: checking 10.2s\nReviewError: Setup stopped"
                )
            );
        ReviewBatch
            .FailureSignature("Sandbox prepare failed (exit 1)." + tail + "FileNotFoundError: receipts.json")
            .Should()
            .NotBe(
                ReviewBatch.FailureSignature("Sandbox prepare failed (exit 1)." + tail + "ReviewError: Setup stopped")
            );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Batch_retries_a_host_timeout_before_the_claim_instead_of_holding_the_pr(bool reviewedEarlierHead)
    {
        // The conversation lookup runs before the claim. A host timeout there leaves the attempt row as it was
        // and says nothing about the PR, yet it held nine fresh PRs for good. The row may already hold an
        // earlier head's released review, which must not turn the timeout into a hold either.
        using var batch = new BatchFixture(0);
        if (reviewedEarlierHead)
            batch.PutAttempt(
                "1",
                new JsonObject
                {
                    ["slot"] = 0,
                    ["stage"] = "completed-verified",
                    ["cleanupState"] = "released",
                    ["claimToken"] = "earlier-claim",
                    ["reviewedHeadSha"] = new string('9', 40),
                    ["mergeSha"] = new string('9', 40),
                    ["artifactCommit"] = new JsonObject { ["status"] = "committed", ["commit"] = new string('c', 40) },
                }
            );
        batch.OnRunAsync = () =>
            batch.Runs.Count == 1
                ? throw new TaskCanceledException(
                    "The request was canceled due to the configured HttpClient.Timeout of 30 seconds elapsing.",
                    new TimeoutException()
                )
                : Task.CompletedTask;
        await batch.ExecuteAsync();
        batch.Runs.Should().Equal("1:fresh", "2:fresh", "3:fresh", "1:fresh");
        batch.Entries().Should().OnlyContain(entry => entry["status"]!.GetValue<string>() == "verified");
    }

    [Fact]
    public async Task Batch_retries_a_pr_whose_earlier_conversation_is_still_running_instead_of_holding_it()
    {
        // A busy hierarchy read, or a child still running in an earlier conversation, defers the review before
        // the claim. That passes, yet one transient busy read held the PR for the rest of the selection.
        using var batch = new BatchFixture(0);
        batch.OnRunAsync = () =>
            batch.Runs.Count == 1
                ? throw new ReviewRunner.ReviewDeferredException(
                    "An earlier conversation for this PR is still running; retry deferred."
                )
                : Task.CompletedTask;
        await batch.ExecuteAsync();
        batch.Runs.Should().Equal("1:fresh", "2:fresh", "3:fresh", "1:fresh");
        batch.Entries().Should().OnlyContain(entry => entry["status"]!.GetValue<string>() == "verified");
    }

    [Fact]
    public async Task Batch_shutdown_drains_and_same_request_restart_skips_verified_work()
    {
        using var batch = new BatchFixture(0);
        batch.OnRun = () => batch.Cancellation.Cancel();
        await batch.ExecuteAsync();
        batch.Runs.Should().Equal("1:fresh");
        batch.Restart();
        batch.Eligibility = pr =>
        {
            batch.Cancellation.Cancel();
            return BatchFixture.Live(pr);
        };
        await batch.ExecuteAsync();
        batch.Runs.Should().Equal("1:fresh");
        batch.Restart();
        await batch.ExecuteAsync();
        batch.Runs.Should().Equal("1:fresh", "2:fresh", "3:fresh");
        batch.Entries().Should().OnlyContain(entry => entry["status"]!.GetValue<string>() == "verified");
    }

    [Fact]
    public async Task Batch_proc_exit_guard_allows_following_queued_admission()
    {
        if (!OperatingSystem.IsLinux())
            return;
        using var batch = new BatchFixture(0);
        var checks = 0;
        batch.CheckProducer = () =>
        {
            checks++;
            ReviewBatch.EnsureNoDaemon(
                () => ["/proc/11", "/proc/12"],
                path => path == "/proc/11/cmdline" ? throw new IOException("No such process", 3) : []
            );
        };
        await batch.ExecuteAsync();
        checks.Should().Be(4);
        batch.Runs.Should().Equal("1:fresh", "2:fresh", "3:fresh");
        batch.Entries().Should().OnlyContain(entry => entry["status"]!.GetValue<string>() == "verified");
    }

    [Fact]
    public async Task Batch_producer_watcher_failure_drains_admitted_run_without_launching_queued_work()
    {
        using var batch = new BatchFixture(0);
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checks = 0;
        batch.CheckProducer = () =>
        {
            if (++checks < 3)
                return;
            throw new UnauthorizedAccessException("Producer inspection unavailable");
        };
        batch.WatchDelay = token => admitted.Task.WaitAsync(token);
        batch.OnRunAsync = async () =>
        {
            using var registration = batch.SchedulingToken.Register(() => canceled.TrySetResult());
            admitted.SetResult();
            await canceled.Task;
        };
        await batch.ExecuteAsync();
        batch.Runs.Should().Equal("1:fresh");
        batch
            .Entries()
            .Select(entry => entry["status"]!.GetValue<string>())
            .Should()
            .Equal("verified", "queued", "queued");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Batch_restart_holds_unknown_preparation_only_in_its_own_lane(bool retainedAttempt)
    {
        using var batch = new BatchFixture(0, 1);
        batch.Cancellation.Cancel();
        await batch.ExecuteAsync();
        batch.Restart();
        var state = batch.Read(batch.StateKey)!.AsObject();
        state["entries"]![0]!["status"] = "launching";
        batch.Put(batch.StateKey, state);
        if (retainedAttempt)
            batch.PutAttempt("1", new JsonObject { ["slot"] = 0, ["stage"] = "prepare-pending" });
        var execute = () => batch.ExecuteAsync();
        await execute();
        batch.Runs.Should().Equal("2:fresh");
        batch.LiveReads.Should().Be(1);
        batch.Entries()[0]["status"]!.GetValue<string>().Should().Be("held");
        if (retainedAttempt)
        {
            var ownerKey = "reviewd/review-attempt/" + PrCommands.ParseRepo("org/project/Nova").NormalizedKey + "/1";
            var owner = batch.Read(ownerKey)!.AsObject();
            owner["repository"] = "org/project/Nova";
            owner["prId"] = "1";
            owner["workspaceId"] = "workspace";
            owner["sessionId"] = "session";
            batch.PutAttempt("1", owner);
            var original = owner.ToJsonString();
            var retirement = owner.DeepClone().AsObject();
            retirement["schemaVersion"] = 1;
            retirement["attemptDigest"] = SlotRelease.Digest(original);
            retirement["classification"] = "operator-retired-after-reset";
            retirement["recoverySessionId"] = "session";
            retirement["writersRetired"] = true;
            retirement["producersStopped"] = true;
            retirement["threadQuiescent"] = true;
            retirement["nativeQuiescent"] = true;
            retirement["resetReceiptPath"] = "reset.json";
            retirement["archiveManifestPath"] = "archive.json";
            retirement["evidence"] = new JsonArray(new JsonObject());
            batch.Put(SlotRelease.Key(original), retirement);
            batch.Restart();
            await batch.ExecuteAsync();
            batch.Runs.Should().BeEquivalentTo(["2:fresh", "3:fresh"]);
            batch.Entries()[0]["status"]!.GetValue<string>().Should().Be("retired");
            batch.Read(ownerKey)!.ToJsonString().Should().Be(original);
            batch
                .Read("reviewd/review-run/" + PrCommands.ParseRepo("org/project/Nova").NormalizedKey + "/1")
                .Should()
                .BeNull();
            retirement["attemptDigest"] = "wrong";
            batch.Put(SlotRelease.Key(original), retirement);
            batch.Restart();
            await execute
                .Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("*Retired batch owner lacks*certificate*");
            batch.Runs.Should().BeEquivalentTo(["2:fresh", "3:fresh"]);
        }
    }

    [Theory]
    [InlineData("fresh", "age-excluded", 0)]
    [InlineData("failed-released", "age-excluded", 0)]
    [InlineData("admitted", "verified", 3)]
    [InlineData("prepared", "held", 0)]
    [InlineData("review-send-pending", "held", 0)]
    public async Task Batch_creation_age_excludes_only_fresh_admission_and_preserves_retained_owners(
        string scenario,
        string status,
        int runs
    )
    {
        using var batch = new BatchFixture(0) { MaxAgeHours = "72" };
        batch.SetManifestAge(JsonValue.Create(72));
        if (scenario != "fresh")
            batch.PutAttempt(
                "1",
                new JsonObject
                {
                    ["slot"] = 0,
                    ["stage"] = scenario == "admitted" ? "review-running" : scenario,
                    ["cleanupState"] = scenario == "failed-released" ? "released" : "owned",
                    ["claimToken"] = "token",
                    ["reviewInputId"] = scenario == "admitted" ? "original" : null,
                }
            );
        batch.Eligibility = pr =>
        {
            var live = BatchFixture.Live(pr);
            live["createdAt"] = batch.Now.AddHours(pr == "1" ? -73 : -1).ToString("O");
            return live;
        };
        await batch.ExecuteAsync();
        batch.Entries().First()["status"]!.GetValue<string>().Should().Be(status);
        batch.Runs.Should().HaveCount(runs == 0 && (scenario is "fresh" or "failed-released") ? 2 : runs);
        if (scenario is "prepared" or "review-send-pending")
            batch.Read("reviewd/review-attempt/azure-devops/org/project/nova/1")!["cleanupState"]!
                .GetValue<string>()
                .Should()
                .Be("owned");
        if (scenario is "fresh" or "failed-released")
            batch.Runs.Should().NotContain("1:fresh");
        batch.RunAges.Should().NotContain(age => age != "72");
    }

    [Fact]
    public async Task Batch_resumes_an_owner_whose_verification_did_not_finish()
    {
        using var batch = new BatchFixture(0);
        batch.PutAttempt(
            "1",
            new JsonObject
            {
                ["slot"] = 0,
                ["stage"] = "verify-pending",
                ["reviewInputId"] = "original",
                ["confirmationIdempotencyKey"] = "confirm",
                ["confirmationInputId"] = "confirmation",
                ["mergeSha"] = new string('b', 40),
            }
        );
        await batch.ExecuteAsync();
        batch.Runs.Should().Contain("1:resume");
        batch.Entries().Should().OnlyContain(entry => entry["status"]!.GetValue<string>() == "verified");
    }

    [Theory]
    [InlineData("eligibility")]
    [InlineData("resume")]
    public async Task Batch_backs_off_gateway_capacity_without_holding_the_pr(string refusedAt)
    {
        using var batch = new BatchFixture(0);
        if (refusedAt == "resume")
            batch.PutAttempt(
                "1",
                new JsonObject
                {
                    ["slot"] = 0,
                    ["stage"] = "review-running",
                    ["reviewInputId"] = "original",
                    ["mergeSha"] = new string('b', 40),
                }
            );
        var refusals = 0;
        void Refuse(string pr)
        {
            if (pr == "1" && ++refusals <= 7)
                throw new SandboxException(SandboxErrorKind.Unavailable, "operation_concurrency_limit", 503);
        }
        batch.Eligibility = pr =>
        {
            if (refusedAt == "eligibility")
                Refuse(pr);
            return BatchFixture.Live(pr);
        };
        if (refusedAt == "resume")
            batch.OnRun = () => Refuse(batch.Runs[^1].Split(':')[0]);
        await batch.ExecuteAsync();
        refusals.Should().Be(8);
        batch.Runs.Should().Contain(refusedAt == "resume" ? "1:resume" : "1:fresh");
        batch.Entries().Should().OnlyContain(entry => entry["status"]!.GetValue<string>() == "verified");
        batch.Entries().First()["failures"]!.GetValue<int>().Should().Be(0);
    }

    [Theory]
    [InlineData("failed-released", "1:fresh")]
    [InlineData("review-running", null)]
    public async Task Batch_defers_only_a_resumed_owner_whose_prepared_merge_changed(string stage, string? run)
    {
        using var batch = new BatchFixture(0);
        batch.PutAttempt(
            "1",
            new JsonObject
            {
                ["slot"] = 0,
                ["stage"] = stage,
                ["cleanupState"] = stage == "failed-released" ? "released" : "owned",
                ["claimToken"] = "token",
                ["reviewInputId"] = stage == "review-running" ? "original" : null,
                ["mergeSha"] = new string('9', 40),
            }
        );
        await batch.ExecuteAsync();
        if (run is null)
        {
            batch.Runs.Should().NotContain(value => value.StartsWith("1:", StringComparison.Ordinal));
            batch.Entries().First()["reason"]!.GetValue<string>().Should().Contain("prepared merge");
        }
        else
        {
            batch.Runs.Should().Contain(run);
            batch.Entries().First()["status"]!.GetValue<string>().Should().Be("verified");
        }
    }

    [Theory]
    [InlineData('d', "1:fresh")]
    [InlineData('a', null)]
    public async Task Batch_admits_a_new_head_fresh_after_its_slot_was_released_mid_review(
        char attemptHead,
        string? run
    )
    {
        using var batch = new BatchFixture(0);
        // The reaper released the slot at review-completed and left the stage; its prepared merge is now stale.
        batch.PutAttempt(
            "1",
            new JsonObject
            {
                ["slot"] = 0,
                ["stage"] = "review-completed",
                ["cleanupState"] = "released",
                ["claimToken"] = "token",
                ["reviewInputId"] = "original",
                ["headSha"] = new string(attemptHead, 40),
                ["mergeSha"] = new string('9', 40),
            }
        );
        await batch.ExecuteAsync();
        if (run is null)
        {
            // The same head may already have posted, so it only resumes, and its changed merge defers it.
            batch.Runs.Should().NotContain(value => value.StartsWith("1:", StringComparison.Ordinal));
            batch.Entries().First()["reason"]!.GetValue<string>().Should().Contain("prepared merge");
        }
        else
        {
            batch.Runs.Should().Contain(run);
            batch.Entries().First()["status"]!.GetValue<string>().Should().Be("verified");
        }
    }

    [Fact]
    public async Task Batch_creation_age_is_rolling_forwarded_and_bound_to_immutable_scope()
    {
        using var batch = new BatchFixture(0) { MaxAgeHours = "72" };
        batch.SetManifestAge(JsonValue.Create(72));
        var created = batch.Now.AddHours(-72).ToString("O");
        batch.Eligibility = pr =>
        {
            var live = BatchFixture.Live(pr);
            live["createdAt"] = created;
            return live;
        };
        batch.OnRun = () => batch.AdvanceSeconds(1);
        await batch.ExecuteAsync();
        batch.Runs.Should().Equal("1:fresh");
        batch.RunAges.Should().Equal("72");
        batch
            .Entries()
            .Select(e => e["status"]!.GetValue<string>())
            .Should()
            .Equal("verified", "age-excluded", "age-excluded");
        batch.Read(batch.StateKey)!["scope"]!["maxAgeHours"]!.GetValue<int>().Should().Be(72);
        batch.MaxAgeHours = "24";
        var changed = () => batch.ExecuteAsync();
        await changed.Should().ThrowAsync<InvalidOperationException>().WithMessage("*exactly match*");
        batch.Runs.Should().HaveCount(1);
    }

    [Theory]
    [InlineData(null, "72")]
    [InlineData("72", null)]
    [InlineData("24", "72")]
    [InlineData("\"72\"", "72")]
    [InlineData("72.0", "72")]
    [InlineData("0", "72")]
    public async Task Batch_creation_age_requires_matching_positive_integer_manifest_policy(
        string? manifest,
        string? option
    )
    {
        using var batch = new BatchFixture(0) { MaxAgeHours = option };
        if (manifest is not null)
            batch.SetManifestAge(JsonNode.Parse(manifest));
        var run = () => batch.ExecuteAsync();
        await run.Should().ThrowAsync<InvalidOperationException>().WithMessage("*exactly match*");
        batch.Runs.Should().BeEmpty();
        batch.Read(batch.StateKey).Should().BeNull();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+72")]
    [InlineData("1.5")]
    [InlineData("2147483648")]
    public void Review_cli_creation_age_requires_a_positive_integer(string value)
    {
        var parse = () => CliOptions.Parse(["pr", "pick", "--repo", "org/project/Nova", "--max-age-hours", value]);
        parse.Should().Throw<ArgumentException>().WithMessage("*positive integer*");
        CliOptions
            .Parse(["pr", "pick", "--repo", "org/project/Nova", "--max-age-hours", "072"])
            .Get("max-age-hours")
            .Should()
            .Be("72");
    }

    private sealed class BatchFixture : IDisposable
    {
        public const string Repo = "org/project/Nova";
        private readonly ReviewdConfig _config = new(new ConfigurationBuilder().Build());
        private readonly string _database = Path.Combine(Path.GetTempPath(), $"reviewd-batch-{Guid.NewGuid():N}.db");
        private string _selection;
        private long _seconds = 1_700_000_000;
        public CancellationTokenSource Cancellation { get; private set; } = new();
        public List<string> Runs { get; } = [];
        public int LiveReads { get; private set; }
        public CancellationToken SchedulingToken { get; private set; }
        public string? ReasoningEffort { get; set; }
        public string? MaxAgeHours { get; set; }
        public List<string?> RunAges { get; } = [];
        public DateTimeOffset Now => DateTimeOffset.FromUnixTimeSeconds(_seconds);

        public void AdvanceSeconds(int seconds) => _seconds += seconds;

        public void SetManifestAge(JsonNode? value)
        {
            var selection = JsonNode.Parse(_selection)!.AsObject();
            selection["maxAgeHours"] = value;
            _selection = selection.ToJsonString();
        }

        public List<string?> RunEfforts { get; } = [];
        public Func<string, JsonObject> Eligibility { get; set; } = Live;
        public Action? OnRun { get; set; }
        public Func<Task>? OnRunAsync { get; set; }
        public Action CheckProducer { get; set; } = () => { };
        public Func<CancellationToken, Task> WatchDelay { get; set; } =
            token => Task.Delay(Timeout.InfiniteTimeSpan, token);
        public string StateKey => "reviewd/batch-state/" + SlotRelease.Digest(_selection);

        public BatchFixture(params int[] slots)
        {
            using (var connection = PrThreadBindings.Connection(_database))
                _ = new SqliteKvStore(connection);
            var rows = new JsonArray();
            foreach (var pr in new[] { "1", "2", "3" })
                rows.Add(
                    new JsonObject
                    {
                        ["repository"] = Repo,
                        ["prId"] = pr,
                        ["headSha"] = new string('a', 40),
                    }
                );
            _selection = new JsonObject
            {
                ["repositories"] = new JsonObject { [Repo] = rows },
                ["lanes"] = new JsonObject
                {
                    [Repo] = new JsonArray([.. slots.Select(slot => (JsonNode)JsonValue.Create(slot))]),
                },
            }.ToJsonString();
        }

        public static JsonObject Live(string pr) =>
            new()
            {
                ["prId"] = pr,
                ["status"] = "active",
                ["isDraft"] = false,
                ["headSha"] = new string('a', 40),
                ["mergeSha"] = new string('b', 40),
            };

        public Task ExecuteAsync() =>
            ReviewBatch.ExecuteAsync(
                CliOptions.Parse([
                    "review",
                    "batch",
                    "--db",
                    _database,
                    "--workspace",
                    "workspace",
                    "--session",
                    "session",
                    "--provider",
                    "test",
                    "--execute",
                    .. ReasoningEffort is null ? [] : new string[] { "--reasoning-effort", ReasoningEffort },
                    .. MaxAgeHours is null ? [] : new string[] { "--max-age-hours", MaxAgeHours },
                ]),
                _config,
                _selection,
                (_, pr, _, _, token) =>
                {
                    SchedulingToken = token;
                    LiveReads++;
                    return Task.FromResult(Eligibility(pr));
                },
                async options =>
                {
                    var pr = options.Required("pr");
                    lock (Runs)
                    {
                        Runs.Add(pr + (options.Has("resume") ? ":resume" : ":fresh"));
                        RunEfforts.Add(options.Get("reasoning-effort"));
                        RunAges.Add(options.Get("max-age-hours"));
                    }
                    await Task.Yield();
                    OnRun?.Invoke();
                    if (OnRunAsync is not null)
                        await OnRunAsync();
                    var receipt = new JsonObject
                    {
                        ["slot"] = int.Parse(options.Required("slot")),
                        ["stage"] = "completed-verified",
                        ["cleanupState"] = "released",
                        ["claimToken"] = "fixture-claim",
                        ["reviewedHeadSha"] = new string('a', 40),
                        ["artifactCommit"] = new JsonObject
                        {
                            ["status"] = "committed",
                            ["commit"] = new string('c', 40),
                        },
                    };
                    PutAttempt(pr, receipt);
                    Put("reviewd/review-run/" + PrCommands.ParseRepo(Repo).NormalizedKey + "/" + pr, receipt);
                    return 0;
                },
                CheckProducer,
                async (duration, token) =>
                {
                    if (duration == TimeSpan.FromSeconds(5))
                        await WatchDelay(token);
                    else
                    {
                        token.ThrowIfCancellationRequested();
                        _seconds += (long)duration.TotalSeconds;
                        await Task.Yield();
                    }
                },
                () => DateTimeOffset.FromUnixTimeSeconds(_seconds),
                Cancellation.Token
            );

        public void Restart()
        {
            Cancellation.Dispose();
            Cancellation = new();
            OnRun = null;
            Eligibility = Live;
        }

        public void PutSelection(int count)
        {
            var selection = JsonNode.Parse(_selection)!.AsObject();
            selection["repositories"]![Repo] = new JsonArray([
                .. Enumerable
                    .Range(1, count)
                    .Select(pr =>
                        (JsonNode)
                            new JsonObject
                            {
                                ["repository"] = Repo,
                                ["prId"] = pr.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                ["headSha"] = new string('a', 40),
                            }
                    ),
            ]);
            _selection = selection.ToJsonString();
        }

        public void PutAttempt(string pr, JsonNode value) =>
            Put("reviewd/review-attempt/" + PrCommands.ParseRepo(Repo).NormalizedKey + "/" + pr, value);

        public void Put(string key, JsonNode value)
        {
            using var connection = PrThreadBindings.Connection(_database);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO cache(key,value) VALUES(@key,@value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
            command.Parameters.AddWithValue("@key", key);
            command.Parameters.AddWithValue("@value", value.ToJsonString());
            command.ExecuteNonQuery();
        }

        public JsonNode? Read(string key)
        {
            using var connection = PrThreadBindings.Connection(_database);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM cache WHERE key=@key";
            command.Parameters.AddWithValue("@key", key);
            return command.ExecuteScalar() is string raw ? JsonNode.Parse(raw) : null;
        }

        public JsonObject[] Entries() => [.. Read(StateKey)!["entries"]!.AsArray().Select(entry => entry!.AsObject())];

        public void Dispose()
        {
            Cancellation.Dispose();
            _config.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(_database);
        }
    }

    [Fact]
    public async Task Parallel_leases_allow_distinct_PRs_and_slots_but_reject_duplicates_and_fence_durable_claims()
    {
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-claims-{Guid.NewGuid():N}.db");
        try
        {
            using var connection = PrThreadBindings.Connection(database);
            _ = new SqliteKvStore(connection);
            await connection.OpenAsync();
            var owner = new JsonObject { ["prId"] = "17", ["sessionId"] = "session" };
            var key = SlotRelease.OwnerKey("workspace", "repo", 0);
            var first = await SlotRelease.ClaimAsync(
                connection,
                key,
                owner,
                null,
                "attempt/17",
                CancellationToken.None
            );
            var duplicate = () =>
                SlotRelease.ClaimAsync(connection, key, owner, null, "attempt/17", CancellationToken.None);
            await duplicate.Should().ThrowAsync<InvalidOperationException>();
            (await SlotRelease.ClaimAsync(connection, key, owner, first, "attempt/17", CancellationToken.None))
                .Should()
                .Be(first);
            await SlotRelease.ReleaseAsync(connection, key, first, "attempt/17", owner, CancellationToken.None);
            var next = await SlotRelease.ClaimAsync(
                connection,
                key,
                new JsonObject { ["prId"] = "18", ["sessionId"] = "session" },
                null,
                "attempt/18",
                CancellationToken.None
            );
            next.Should().NotBe(first);
            var stale = () => SlotRelease.AssertCurrentAsync(connection, key, first, CancellationToken.None);
            await stale.Should().ThrowAsync<InvalidOperationException>();
            var staleRelease = () =>
                SlotRelease.ReleaseAsync(connection, key, first, "attempt/17", owner, CancellationToken.None);
            await staleRelease.Should().ThrowAsync<InvalidOperationException>();
            await SlotRelease.AssertCurrentAsync(connection, key, next, CancellationToken.None);
            using (var failHistory = connection.CreateCommand())
            {
                failHistory.CommandText =
                    "CREATE TRIGGER fail_history BEFORE INSERT ON cache WHEN NEW.key LIKE 'reviewd/legacy-cleaned/%' BEGIN SELECT RAISE(ABORT, 'history persistence failed'); END";
                await failHistory.ExecuteNonQueryAsync();
            }
            var finalAttempt = new JsonObject { ["prId"] = "18", ["stage"] = "failed-released" };
            var releaseWithHistory = () =>
                SlotRelease.ReleaseAsync(
                    connection,
                    key,
                    next,
                    "attempt/18",
                    finalAttempt,
                    CancellationToken.None,
                    ["exact historical receipt"]
                );
            await releaseWithHistory.Should().ThrowAsync<Microsoft.Data.Sqlite.SqliteException>();
            await SlotRelease.AssertCurrentAsync(connection, key, next, CancellationToken.None);
            using (var repairHistory = connection.CreateCommand())
            {
                repairHistory.CommandText = "DROP TRIGGER fail_history";
                await repairHistory.ExecuteNonQueryAsync();
            }
            await releaseWithHistory();
            using var read = connection.CreateCommand();
            read.CommandText = "SELECT value FROM cache WHERE key='attempt/18'";
            JsonNode.Parse((string)(await read.ExecuteScalarAsync())!)!["stage"]!
                .GetValue<string>()
                .Should()
                .Be("failed-released");
            read.CommandText = "SELECT value FROM cache WHERE key=@key";
            read.Parameters.AddWithValue(
                "@key",
                "reviewd/legacy-cleaned/" + SlotRelease.Digest("exact historical receipt")
            );
            JsonNode.Parse((string)(await read.ExecuteScalarAsync())!)!["cleanupState"]!
                .GetValue<string>()
                .Should()
                .Be("released");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }

    [Fact]
    public async Task Slot_claim_and_its_attempt_commit_together_or_not_at_all()
    {
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-claim-attempt-{Guid.NewGuid():N}.db");
        try
        {
            using var connection = PrThreadBindings.Connection(database);
            _ = new SqliteKvStore(connection);
            await connection.OpenAsync();
            var key = SlotRelease.OwnerKey("workspace", "repo", 0);
            using (var failAttempt = connection.CreateCommand())
            {
                failAttempt.CommandText =
                    "CREATE TRIGGER fail_attempt BEFORE INSERT ON cache WHEN NEW.key = 'attempt/17' BEGIN SELECT RAISE(ABORT, 'attempt write failed'); END";
                await failAttempt.ExecuteNonQueryAsync();
            }
            var claim = () =>
                SlotRelease.ClaimAsync(
                    connection,
                    key,
                    new JsonObject
                    {
                        ["prId"] = "17",
                        ["sessionId"] = "session",
                        ["stage"] = "selected",
                    },
                    null,
                    "attempt/17",
                    CancellationToken.None
                );
            await claim.Should().ThrowAsync<Microsoft.Data.Sqlite.SqliteException>();
            (await ReadRawAsync(connection, key)).Should().BeNull("a claim whose attempt was not saved must roll back");
            using (var repair = connection.CreateCommand())
            {
                repair.CommandText = "DROP TRIGGER fail_attempt";
                await repair.ExecuteNonQueryAsync();
            }
            var token = await claim();
            var attempt = JsonNode.Parse((await ReadRawAsync(connection, "attempt/17"))!)!;
            attempt["claimToken"]!.GetValue<string>().Should().Be(token);
            attempt["cleanupState"]!.GetValue<string>().Should().Be("owned");
            attempt["stage"]!.GetValue<string>().Should().Be("selected");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reconcile_releases_the_exact_certified_claim_so_another_PR_can_take_the_slot(bool newerOwner)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"reviewd-reconcile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "reviewd.db");
        var repo = PrCommands.ParseRepo("org/project/Nova");
        var attemptKey = $"reviewd/review-attempt/{repo.NormalizedKey}/17";
        var ownerKey = SlotRelease.OwnerKey("workspace", repo.NormalizedKey, 2);
        try
        {
            using var connection = PrThreadBindings.Connection(database);
            var store = new SqliteKvStore(connection);
            await connection.OpenAsync();
            // A run that stopped right after claiming: its attempt is still at "selected".
            await SlotRelease.ClaimAsync(
                connection,
                ownerKey,
                new JsonObject
                {
                    ["repository"] = repo.DisplayName,
                    ["prId"] = "17",
                    ["slot"] = 2,
                    ["workspaceId"] = "workspace",
                    ["sessionId"] = "session",
                    ["stage"] = "selected",
                },
                null,
                attemptKey,
                CancellationToken.None
            );
            if (newerOwner)
            {
                var newer = (await store.GetAsync<JsonObject>(ownerKey))!;
                newer["claimToken"] = "newer-claim";
                await store.SetAsync(ownerKey, newer);
            }
            var attempt = (await ReadRawAsync(connection, attemptKey))!;
            var ownerBefore = await ReadRawAsync(connection, ownerKey);
            var evidencePath = Path.Combine(directory, "evidence.txt");
            await File.WriteAllTextAsync(evidencePath, "Runner stopped before prepare was dispatched.");
            var proof = JsonNode.Parse(attempt)!.AsObject();
            proof["schemaVersion"] = 1;
            proof["attemptDigest"] = SlotRelease.Digest(attempt);
            proof["classification"] = "preparation-settled-before-review";
            proof["noAdmission"] = true;
            proof["preparationSettled"] = true;
            proof["threadQuiescent"] = true;
            proof["nativeQuiescent"] = true;
            proof["operatorReason"] = "Runner stopped before prepare.";
            proof["evidence"] = new JsonArray(
                new JsonObject
                {
                    ["path"] = evidencePath,
                    ["sha256"] = Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(evidencePath))
                    ),
                }
            );
            var proofPath = Path.Combine(directory, "proof.json");
            await File.WriteAllTextAsync(proofPath, proof.ToJsonString());
            var reconcile = () =>
                SlotRelease.RunAsync(
                    CliOptions.Parse([
                        "review",
                        "reconcile-slot",
                        "--repo",
                        repo.DisplayName,
                        "--pr",
                        "17",
                        "--db",
                        database,
                        "--evidence",
                        proofPath,
                        "--execute",
                    ]),
                    CancellationToken.None
                );
            if (newerOwner)
            {
                await reconcile.Should().ThrowAsync<InvalidOperationException>().WithMessage("*different claim*");
                (await ReadRawAsync(connection, ownerKey)).Should().Be(ownerBefore);
                (await ReadRawAsync(connection, SlotRelease.Key(attempt))).Should().BeNull();
            }
            else
            {
                (await reconcile()).Should().Be(0);
                JsonNode.Parse((await ReadRawAsync(connection, ownerKey))!)!["state"]!
                    .GetValue<string>()
                    .Should()
                    .Be("released");
                (await ReadRawAsync(connection, SlotRelease.Key(attempt))).Should().NotBeNull();
                var next = await SlotRelease.ClaimAsync(
                    connection,
                    ownerKey,
                    new JsonObject { ["prId"] = "18", ["sessionId"] = "session" },
                    null,
                    $"reviewd/review-attempt/{repo.NormalizedKey}/18",
                    CancellationToken.None
                );
                next.Should().NotBeNullOrEmpty();
            }
            (await ReadRawAsync(connection, attemptKey)).Should().Be(attempt, "the original attempt is retained as is");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(0, 'd', true)]
    [InlineData(1, 'd', true)]
    [InlineData(1, 'a', false)]
    public async Task Batch_runs_a_new_head_fresh_after_a_released_review_in_any_slot(
        int retainedSlot,
        char reviewedHead,
        bool runs
    )
    {
        using var batch = new BatchFixture(0, 1);
        // PR 1 is assigned slot 0. Its earlier review finished and released whichever slot it used.
        var released = new JsonObject
        {
            ["slot"] = retainedSlot,
            ["stage"] = "completed-verified",
            ["cleanupState"] = "released",
            ["claimToken"] = "earlier-claim",
            ["reviewedHeadSha"] = new string(reviewedHead, 40),
            ["artifactCommit"] = new JsonObject { ["status"] = "committed", ["commit"] = new string('c', 40) },
        };
        batch.PutAttempt("1", released);
        batch.Put("reviewd/review-run/" + PrCommands.ParseRepo("org/project/Nova").NormalizedKey + "/1", released);
        await batch.ExecuteAsync();
        if (runs)
            batch.Runs.Should().Contain("1:fresh");
        else
            batch.Runs.Should().NotContain(run => run.StartsWith("1:", StringComparison.Ordinal));
    }

    private static async Task<string?> ReadRawAsync(Microsoft.Data.Sqlite.SqliteConnection connection, string key)
    {
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT value FROM cache WHERE key=@key";
        read.Parameters.AddWithValue("@key", key);
        return await read.ExecuteScalarAsync() as string;
    }

    [Fact]
    public void Parallel_leases_allow_distinct_PRs_and_slots_but_reject_duplicates()
    {
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-lease-{Guid.NewGuid():N}");
        using var first = ReviewRunner.AcquireLease(database, "pr/repo/17");
        using var second = ReviewRunner.AcquireLease(database, "pr/repo/18");
        using var slot = ReviewRunner.AcquireLease(database, "slot/workspace/repo/0");
        using var otherSlot = ReviewRunner.AcquireLease(database, "slot/workspace/other-repo/0");
        var duplicate = () => ReviewRunner.AcquireLease(database, "pr/repo/17");
        duplicate.Should().Throw<IOException>();
        var duplicateSlot = () => ReviewRunner.AcquireLease(database, "slot/workspace/repo/0");
        duplicateSlot.Should().Throw<IOException>();
    }

    [Fact]
    public void Workspace_identity_rejects_a_nested_workspace_with_the_same_leaf()
    {
        ReviewRunner.MatchesWorkspace("app-prefix/nova-reviews", "nova-reviews").Should().BeTrue();
        ReviewRunner.MatchesWorkspace("app-prefix/archive/nova-reviews", "nova-reviews").Should().BeFalse();
        ReviewRunner.MatchesWorkspace(null, "nova-reviews").Should().BeFalse();
    }

    [Theory]
    [InlineData("success")]
    [InlineData("completed-input")]
    [InlineData("running-input")]
    [InlineData("unknown-input")]
    [InlineData("confirmation")]
    [InlineData("confirmation-pending")]
    [InlineData("successful-receipt")]
    [InlineData("wrong-claim")]
    [InlineData("wrong-session")]
    [InlineData("cleanup-failure")]
    [InlineData("terminal-descendants")]
    [InlineData("running-descendant")]
    [InlineData("unknown-descendant")]
    public async Task Failed_owner_cleanup_is_exact_get_only_and_preserves_history_without_inventing_success(
        string scenario
    )
    {
        var succeeds = scenario is "success" or "terminal-descendants";
        static string Child(string id, string state, string? failure = null) =>
            $$"""{"agentId":"{{id}}","threadId":"{{id}}-thread","parentThreadId":"original","depth":1,"template":"reviewer","status":"{{state}}"{{(
                failure is null ? "" : $$""","failureCode":"{{failure}}" """
            )}}}""";
        var descendants = scenario switch
        {
            // A failed review leaves failed children behind; once nothing is running they are settled.
            "terminal-descendants" => Child("errored", "error", "model_error") + "," + Child("stopped", "stopped"),
            "running-descendant" => Child("errored", "error", "model_error") + "," + Child("busy", "running"),
            "unknown-descendant" => Child("errored", "error", "model_error") + "," + Child("odd", "mystery"),
            _ => "",
        };
        var waits = 0;
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-failed-cleanup-{Guid.NewGuid():N}.db");
        var repo = PrCommands.ParseRepo("org/project/Nova");
        var attemptKey = $"reviewd/review-attempt/{repo.NormalizedKey}/17";
        var receiptKey = $"reviewd/review-run/{repo.NormalizedKey}/17";
        var ownerKey = SlotRelease.OwnerKey("workspace", repo.NormalizedKey, 2);
        var status = scenario switch
        {
            "completed-input" => "Completed",
            "running-input" => "InProgress",
            "unknown-input" => "Unknown",
            _ => "Errored",
        };
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/status?", $$"""{"status":"{{status}}","runId":"old-run","response":null}""")
            .OnJson(
                HttpMethod.Get,
                "/capabilities",
                """{"messageIdempotency":true,"spawnSuppression":true,"rootReasoningEffort":true,"conversationWorkingDirectory":true}"""
            )
            .OnJson(HttpMethod.Get, "/run-state", """{"isInProgress":false,"currentRunId":null}""")
            .OnJson(HttpMethod.Get, "/subagents?", $$"""{"schemaVersion":1,"nodes":[{{descendants}}]}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var client = new LmStreamingS2SClient(http, null, null, null);
        using var config = new ReviewdConfig(new ConfigurationBuilder().Build());
        var phases = new List<string>();
        try
        {
            using var connection = PrThreadBindings.Connection(database);
            var store = new SqliteKvStore(connection);
            var attempt = new JsonObject
            {
                ["repository"] = repo.DisplayName,
                ["prId"] = "17",
                ["slot"] = 2,
                ["workspaceId"] = "workspace",
                ["sessionId"] = "session",
                ["providerId"] = "gemini-3.8-flash",
                ["reasoningEffort"] = "high",
                ["headSha"] = new string('a', 40),
                ["stage"] = "review-running",
                ["cleanupState"] = "owned",
                ["claimToken"] = "exact-token",
                ["threadId"] = "original",
                ["reviewInputId"] = "original-input",
                ["prepared"] = new JsonObject { ["mergeSha"] = new string('b', 40) },
            };
            if (scenario == "confirmation")
                attempt["confirmationInputId"] = "confirmation-input";
            if (scenario == "confirmation-pending")
                attempt["confirmationIdempotencyKey"] = "pending-key";
            if (scenario == "wrong-session")
                attempt["sessionId"] = "different-session";
            var original = attempt.ToJsonString();
            await store.SetAsync(attemptKey, attempt);
            await PrThreadBindings.SetAsync(database, repo, "17", "original", CancellationToken.None);
            var claim = attempt.DeepClone().AsObject();
            claim["state"] = "owned";
            if (scenario == "wrong-claim")
                claim["claimToken"] = "other-token";
            await store.SetAsync(ownerKey, claim);
            if (scenario == "successful-receipt")
                await store.SetAsync(receiptKey, new JsonObject { ["stage"] = "completed-verified" });
            var options = CliOptions.Parse([
                "review",
                "run",
                "--repo",
                repo.DisplayName,
                "--pr",
                "17",
                "--slot",
                "2",
                "--db",
                database,
                "--session",
                "session",
                "--workspace",
                "workspace",
                "--provider",
                "gpt-6-luna",
                "--cleanup-only",
                "--execute",
                "--head",
                new string('f', 40),
            ]);
            Task<JsonObject> Sandbox(string phase, JsonObject args, string operation, CancellationToken token)
            {
                phases.Add(phase);
                phase.Should().Be("cleanup");
                args["head"]!.GetValue<string>().Should().Be(new string('a', 40));
                if (scenario == "cleanup-failure")
                    throw new InvalidOperationException("Native reset refused");
                return Task.FromResult(
                    new JsonObject
                    {
                        ["claimToken"] = "exact-token",
                        ["cleanBaselineVerified"] = true,
                        ["evidencePreserved"] = true,
                    }
                );
            }
            var run = () =>
                ReviewRunner.RunAsync(
                    options,
                    config,
                    client,
                    Sandbox,
                    CancellationToken.None,
                    // An active descendant never settles; bound the wait so the test sees it keep waiting.
                    _ => ++waits > 20 ? throw new TimeoutException("Descendant still active.") : Task.CompletedTask
                );
            if (succeeds)
                await run();
            else if (scenario is "running-descendant" or "unknown-descendant")
                await run.Should().ThrowAsync<TimeoutException>();
            else
                await run.Should().ThrowAsync<InvalidOperationException>();
            handler.Requests.Should().NotContain(r => r.Method != HttpMethod.Get);
            var after = (await store.GetAsync<JsonObject>(attemptKey))!;
            var owner = (await store.GetAsync<JsonObject>(ownerKey))!;
            if (succeeds)
            {
                phases.Should().Equal("cleanup");
                after["stage"]!.GetValue<string>().Should().Be("failed-released");
                after["cleanupState"]!.GetValue<string>().Should().Be("released");
                owner["state"]!.GetValue<string>().Should().Be("released");
                after["providerId"]!.GetValue<string>().Should().Be("gemini-3.8-flash");
                after["reasoningEffort"]!.GetValue<string>().Should().Be("high");
                (await store.GetAsync<JsonObject>("reviewd/attempt-history/" + SlotRelease.Digest(original)))!
                    .ToJsonString()
                    .Should()
                    .Be(original);
            }
            else if (scenario == "cleanup-failure")
            {
                phases.Should().Equal("cleanup");
                after["stage"]!.GetValue<string>().Should().Be("review-running");
                after["cleanupState"]!.GetValue<string>().Should().Be("pending");
                owner["state"]!.GetValue<string>().Should().Be("owned");
            }
            else
            {
                phases.Should().BeEmpty();
                after.ToJsonString().Should().Be(original);
                if (scenario is "running-descendant" or "unknown-descendant")
                {
                    // The wait runs before the claim is re-taken, so it renews the retained lease itself;
                    // otherwise the reaper reclaims a review that is still finishing.
                    owner["leaseExpiresAt"].Should().NotBeNull();
                    owner.Remove("leaseExpiresAt");
                }
                owner.ToJsonString().Should().Be(claim.ToJsonString());
            }
            if (scenario != "successful-receipt")
                (await store.GetAsync<JsonObject>(receiptKey)).Should().BeNull();
            (await PrThreadBindings.GetAsync(database, repo, "17", CancellationToken.None)).Should().Be("original");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }

    [Fact]
    public async Task A_long_owner_cleanup_keeps_its_lease_and_its_agent_mark_so_nothing_stops_it()
    {
        // Seen on the ADO store: a slot reset on a Windows bind mount took over 10 minutes. Owner cleanup renewed no
        // lease, so past the 30-minute lease plus the reaper's 30-minute force wait the reaper could reset the slot
        // under it. And while an agent on the conversation wakes, the step's watchdog must not stop it (Kay9).
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-cleanup-lease-{Guid.NewGuid():N}.db");
        var repo = PrCommands.ParseRepo("org/project/Nova");
        var attemptKey = $"reviewd/review-attempt/{repo.NormalizedKey}/17";
        var ownerKey = SlotRelease.OwnerKey("workspace", repo.NormalizedKey, 2);
        var agentBusy = false;
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/status?", """{"status":"Errored","runId":"old-run","response":null}""")
            .OnJson(
                HttpMethod.Get,
                "/capabilities",
                """{"messageIdempotency":true,"spawnSuppression":true,"rootReasoningEffort":true,"conversationWorkingDirectory":true}"""
            )
            .On(
                request => request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/run-state"),
                _ =>
                    Json(
                        agentBusy
                            ? """{"isInProgress":true,"currentRunId":"woken"}"""
                            : """{"isInProgress":false,"currentRunId":null}"""
                    )
            )
            .OnJson(HttpMethod.Get, "/subagents?", """{"schemaVersion":1,"nodes":[]}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var client = new LmStreamingS2SClient(http, null, null, null);
        using var config = new ReviewdConfig(new ConfigurationBuilder().Build());
        var marks = new System.Collections.Concurrent.ConcurrentQueue<string>();
        IReadOnlyList<SlotReaper.ReclaimResult>? swept = null;
        var reaperPhases = new List<string>();
        var previousInterval = ReviewRunner.LeaseRenewInterval;
        ReviewRunner.LeaseRenewInterval = TimeSpan.FromMilliseconds(20);
        try
        {
            using var connection = PrThreadBindings.Connection(database);
            var store = new SqliteKvStore(connection);
            var attempt = new JsonObject
            {
                ["repository"] = repo.DisplayName,
                ["prId"] = "17",
                ["slot"] = 2,
                ["workspaceId"] = "workspace",
                ["sessionId"] = "session",
                ["providerId"] = "gemini-3.8-flash",
                ["reasoningEffort"] = "high",
                ["headSha"] = new string('a', 40),
                ["stage"] = "review-running",
                ["cleanupState"] = "owned",
                ["claimToken"] = "exact-token",
                ["threadId"] = "original",
                ["reviewInputId"] = "original-input",
                ["prepared"] = new JsonObject { ["mergeSha"] = new string('b', 40) },
            };
            await store.SetAsync(attemptKey, attempt);
            await PrThreadBindings.SetAsync(database, repo, "17", "original", CancellationToken.None);
            var claim = attempt.DeepClone().AsObject();
            claim["state"] = "owned";
            await store.SetAsync(ownerKey, claim);
            var options = CliOptions.Parse([
                "review",
                "run",
                "--repo",
                repo.DisplayName,
                "--pr",
                "17",
                "--slot",
                "2",
                "--db",
                database,
                "--session",
                "session",
                "--workspace",
                "workspace",
                "--provider",
                "gpt-6-luna",
                "--cleanup-only",
                "--execute",
                "--head",
                new string('f', 40),
            ]);
            async Task<JsonObject> Sandbox(string phase, JsonObject args, string operation, CancellationToken token)
            {
                if (phase == "step-activity")
                {
                    marks.Enqueue(args["operation"]!.GetValue<string>());
                    return [];
                }
                // The watchdog is alive, so a quiet tick's step-stop finds nothing to stop.
                if (phase == "step-stop")
                    return new JsonObject { ["stopped"] = false };
                phase.Should().Be("cleanup");
                // The cleanup has now run for over an hour: the lease it took when it started has lapsed past the
                // reaper's force wait.
                var owner = (await store.GetAsync<JsonObject>(ownerKey))!;
                owner["leaseExpiresAt"] = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(61);
                await store.SetAsync(ownerKey, owner);
                await WaitForAsync(async () =>
                    (await store.GetAsync<JsonObject>(ownerKey))!["leaseExpiresAt"]!.GetValue<DateTimeOffset>()
                    > DateTimeOffset.UtcNow
                );
                using (var sweep = PrThreadBindings.Connection(database))
                {
                    await sweep.OpenAsync(token);
                    swept = await SlotReaper.SweepAsync(
                        sweep,
                        client,
                        (reaperPhase, _, _, _) =>
                        {
                            reaperPhases.Add(reaperPhase);
                            return Task.FromResult(new JsonObject());
                        },
                        "workspace",
                        DateTimeOffset.UtcNow,
                        undatedExpired: false,
                        execute: true,
                        token
                    );
                }
                agentBusy = true;
                await WaitForAsync(() => Task.FromResult(marks.Contains(operation)));
                agentBusy = false;
                return new JsonObject
                {
                    ["claimToken"] = "exact-token",
                    ["cleanBaselineVerified"] = true,
                    ["evidencePreserved"] = true,
                };
            }

            await ReviewRunner.RunAsync(
                options,
                config,
                client,
                Sandbox,
                CancellationToken.None,
                _ => Task.CompletedTask
            );

            swept.Should().BeEmpty("the running cleanup kept its lease, so the reaper did not even consider the slot");
            reaperPhases.Should().BeEmpty();
            marks.Should().NotBeEmpty("an agent woke while the cleanup ran, so the step was marked active");
            (await store.GetAsync<JsonObject>(attemptKey))!["cleanupState"]!.GetValue<string>().Should().Be("released");
        }
        finally
        {
            ReviewRunner.LeaseRenewInterval = previousInterval;
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
        }

        static async Task WaitForAsync(Func<Task<bool>> condition)
        {
            for (var deadline = DateTime.UtcNow.AddSeconds(5); DateTime.UtcNow < deadline; await Task.Delay(20))
            {
                if (await condition())
                    return;
            }
        }
    }

    [Fact]
    public async Task Runner_renews_the_slot_lease_while_a_long_sandbox_step_runs()
    {
        // A large PR's prepare outlasts the 30-minute lease. Without renewal the reaper reclaims the slot
        // under the live command.
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-lease-{Guid.NewGuid():N}.db");
        var repo = PrCommands.ParseRepo("org/project/Nova");
        var ownerKey = SlotRelease.OwnerKey("workspace", repo.NormalizedKey, 2);
        var handler = new FakeHttpMessageHandler()
            .OnJson(
                HttpMethod.Get,
                "/capabilities",
                """{"messageIdempotency":true,"spawnSuppression":true,"rootReasoningEffort":true,"conversationWorkingDirectory":true}"""
            )
            .On(
                request =>
                    request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/conversations",
                _ => Json("""{"threadId":"thread"}""")
            )
            .OnJson(HttpMethod.Get, "/api/conversations?", "[]")
            .OnJson(HttpMethod.Put, "/metadata", "{}");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var client = new LmStreamingS2SClient(http, null, null, null);
        using var config = new ReviewdConfig(new ConfigurationBuilder().Build());
        var previousInterval = ReviewRunner.LeaseRenewInterval;
        ReviewRunner.LeaseRenewInterval = TimeSpan.FromMilliseconds(20);
        try
        {
            using var connection = PrThreadBindings.Connection(database);
            var store = new SqliteKvStore(connection);
            async Task<JsonObject> Sandbox(string phase, JsonObject args, string operation, CancellationToken token)
            {
                if (phase == "eligibility")
                    return new JsonObject
                    {
                        ["prId"] = "17",
                        ["status"] = "active",
                        ["isDraft"] = false,
                        ["headSha"] = new string('a', 40),
                        ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"),
                    };
                phase.Should().Be("prepare");
                var started = (await store.GetAsync<JsonObject>(ownerKey))![
                    "leaseExpiresAt"
                ]!.GetValue<DateTimeOffset>();
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(20, token);
                    var lease = (await store.GetAsync<JsonObject>(ownerKey))![
                        "leaseExpiresAt"
                    ]!.GetValue<DateTimeOffset>();
                    if (lease > started)
                        throw new InvalidOperationException("lease renewed during prepare");
                }
                throw new InvalidOperationException("lease never renewed during prepare");
            }
            await store.SetAsync(
                $"reviewd/pr-snapshot/{repo.NormalizedKey}",
                new JsonObject
                {
                    ["17"] = new JsonObject
                    {
                        ["Lifecycle"] = "Open",
                        ["IsDraft"] = false,
                        ["HeadSha"] = new string('a', 40),
                    },
                }
            );
            var options = CliOptions.Parse([
                "review",
                "run",
                "--repo",
                repo.DisplayName,
                "--pr",
                "17",
                "--slot",
                "2",
                "--db",
                database,
                "--session",
                "session",
                "--workspace",
                "workspace",
                "--provider",
                "test",
                "--execute",
            ]);
            var run = () =>
                ReviewRunner.RunAsync(
                    options,
                    config,
                    client,
                    Sandbox,
                    CancellationToken.None,
                    _ => Task.CompletedTask
                );
            (await run.Should().ThrowAsync<InvalidOperationException>())
                .Which.Message.Should()
                .Be("lease renewed during prepare");
        }
        finally
        {
            ReviewRunner.LeaseRenewInterval = previousInterval;
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }

    [Theory]
    [InlineData("old")]
    [InlineData("setup-expired")]
    [InlineData("recent")]
    [InlineData("future")]
    [InlineData("missing")]
    [InlineData("no-offset")]
    [InlineData("wrong-head")]
    public async Task Runner_creation_age_checks_live_before_claim_and_initial_send_without_releasing_prepared_owner(
        string scenario
    )
    {
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-age-{Guid.NewGuid():N}.db");
        var repo = PrCommands.ParseRepo("org/project/Nova");
        var now = DateTimeOffset.Parse("2026-10-07T22:00:00Z");
        var clock = now;
        var attemptKey = $"reviewd/review-attempt/{repo.NormalizedKey}/17";
        var ownerKey = SlotRelease.OwnerKey("workspace", repo.NormalizedKey, 2);
        var handler = new FakeHttpMessageHandler()
            .OnJson(
                HttpMethod.Get,
                "/capabilities",
                """{"messageIdempotency":true,"spawnSuppression":true,"rootReasoningEffort":true,"conversationWorkingDirectory":true}"""
            )
            .On(
                request =>
                    request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/conversations",
                _ => Json("""{"threadId":"thread"}""")
            )
            // No prior conversation exists for this PR: the durable host-title lookup finds none.
            .OnJson(HttpMethod.Get, "/api/conversations?", "[]")
            .OnJson(HttpMethod.Put, "/metadata", "{}")
            .OnJson(HttpMethod.Post, "/messages", """{"inputId":"original","idempotencyKeyHonored":true}""")
            .OnJson(HttpMethod.Get, "/status?", """{"status":"Errored","runId":"run","response":null}""")
            .OnJson(HttpMethod.Get, "/run-state", """{"isInProgress":false,"currentRunId":null}""")
            .OnJson(HttpMethod.Get, "/subagents?", """{"schemaVersion":1,"nodes":[]}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var client = new LmStreamingS2SClient(http, null, null, null);
        using var config = new ReviewdConfig(new ConfigurationBuilder().Build());
        var reads = 0;
        var phases = new List<string>();
        Task<JsonObject> Sandbox(string phase, JsonObject args, string operation, CancellationToken token)
        {
            phases.Add(phase);
            if (phase == "eligibility")
            {
                reads++;
                var created = scenario switch
                {
                    "old" => now.AddHours(-73).ToString("O"),
                    "future" => now.AddSeconds(1).ToString("O"),
                    "missing" => null,
                    "no-offset" => "2026-10-07T21:00:00",
                    _ => now.AddHours(-72).ToString("O"),
                };
                return Task.FromResult(
                    new JsonObject
                    {
                        ["prId"] = "17",
                        ["status"] = "active",
                        ["isDraft"] = false,
                        ["headSha"] = new string(scenario == "wrong-head" ? 'f' : 'a', 40),
                        ["createdAt"] = created,
                    }
                );
            }
            if (phase == "prepare")
            {
                if (scenario == "setup-expired")
                    clock = now.AddSeconds(1);
                return Task.FromResult(
                    new JsonObject
                    {
                        ["mergeSha"] = new string('b', 40),
                        ["contextPath"] = "context",
                        ["reviewDirectory"] = "reviews",
                        ["baselineComments"] = new JsonArray(),
                    }
                );
            }
            return Task.FromResult(new JsonObject());
        }
        try
        {
            using var connection = PrThreadBindings.Connection(database);
            var store = new SqliteKvStore(connection);
            await store.SetAsync(
                $"reviewd/pr-snapshot/{repo.NormalizedKey}",
                new JsonObject
                {
                    ["17"] = new JsonObject
                    {
                        ["Lifecycle"] = "Open",
                        ["IsDraft"] = false,
                        ["HeadSha"] = new string('a', 40),
                    },
                }
            );
            var options = CliOptions.Parse([
                "review",
                "run",
                "--repo",
                repo.DisplayName,
                "--pr",
                "17",
                "--slot",
                "2",
                "--db",
                database,
                "--session",
                "session",
                "--workspace",
                "workspace",
                "--provider",
                "test",
                "--max-age-hours",
                "72",
                "--execute",
            ]);
            var run = () =>
                ReviewRunner.RunAsync(
                    options,
                    config,
                    client,
                    Sandbox,
                    CancellationToken.None,
                    _ => Task.CompletedTask,
                    () => clock
                );
            await run.Should().ThrowAsync<InvalidOperationException>();
            var messages = handler.Requests.Count(r =>
                r.Uri.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal)
            );
            messages.Should().Be(scenario == "recent" ? 1 : 0);
            phases.Should().NotContain("cleanup");
            var owner = await store.GetAsync<JsonObject>(ownerKey);
            var attempt = await store.GetAsync<JsonObject>(attemptKey);
            if (scenario is "recent" or "setup-expired")
            {
                reads.Should().Be(2);
                owner!["state"]!.GetValue<string>().Should().Be("owned");
                if (scenario == "setup-expired")
                    attempt!["reviewIdempotencyKey"].Should().BeNull();
            }
            else
            {
                reads.Should().Be(1);
                owner.Should().BeNull();
                attempt.Should().BeNull();
                phases.Should().Equal("eligibility");
            }
            (await store.GetAsync<JsonObject>($"reviewd/review-run/{repo.NormalizedKey}/17")).Should().BeNull();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }

    // 2026-10-20: a cold gateway catalog made the host answer the review send with 503, and the PR was held
    // for hours. The send must be retried with the same idempotency key instead.
    [Fact]
    public async Task Runner_retries_a_503_review_send_with_the_same_idempotency_key()
    {
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-send503-{Guid.NewGuid():N}.db");
        var repo = PrCommands.ParseRepo("org/project/Nova");
        var now = DateTimeOffset.Parse("2026-10-07T22:00:00Z");
        var handler = new FakeHttpMessageHandler()
            .OnJson(
                HttpMethod.Get,
                "/capabilities",
                """{"messageIdempotency":true,"spawnSuppression":true,"rootReasoningEffort":true,"conversationWorkingDirectory":true}"""
            )
            .On(
                request =>
                    request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/conversations",
                _ => Json("""{"threadId":"thread"}""")
            )
            .OnJson(HttpMethod.Get, "/api/conversations?", "[]")
            .OnJson(HttpMethod.Put, "/metadata", "{}")
            .OnSequence(
                HttpMethod.Post,
                "/messages",
                (HttpStatusCode.ServiceUnavailable, "{}"),
                (HttpStatusCode.ServiceUnavailable, "{}"),
                (HttpStatusCode.OK, """{"inputId":"original","idempotencyKeyHonored":true}""")
            )
            .OnJson(HttpMethod.Get, "/status?", """{"status":"Errored","runId":"run","response":null}""")
            .OnJson(HttpMethod.Get, "/run-state", """{"isInProgress":false,"currentRunId":null}""")
            .OnJson(HttpMethod.Get, "/subagents?", """{"schemaVersion":1,"nodes":[]}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var client = new LmStreamingS2SClient(http, null, null, null);
        using var config = new ReviewdConfig(new ConfigurationBuilder().Build());
        Task<JsonObject> Sandbox(string phase, JsonObject args, string operation, CancellationToken token) =>
            Task.FromResult(
                phase switch
                {
                    "eligibility" => new JsonObject
                    {
                        ["prId"] = "17",
                        ["status"] = "active",
                        ["isDraft"] = false,
                        ["headSha"] = new string('a', 40),
                        ["createdAt"] = now.AddHours(-1).ToString("O"),
                    },
                    "prepare" => new JsonObject
                    {
                        ["mergeSha"] = new string('b', 40),
                        ["contextPath"] = "context",
                        ["reviewDirectory"] = "reviews",
                        ["baselineComments"] = new JsonArray(),
                    },
                    _ => [],
                }
            );
        try
        {
            using var connection = PrThreadBindings.Connection(database);
            var store = new SqliteKvStore(connection);
            await store.SetAsync(
                $"reviewd/pr-snapshot/{repo.NormalizedKey}",
                new JsonObject
                {
                    ["17"] = new JsonObject
                    {
                        ["Lifecycle"] = "Open",
                        ["IsDraft"] = false,
                        ["HeadSha"] = new string('a', 40),
                    },
                }
            );
            var options = CliOptions.Parse([
                "review",
                "run",
                "--repo",
                repo.DisplayName,
                "--pr",
                "17",
                "--slot",
                "2",
                "--db",
                database,
                "--session",
                "session",
                "--workspace",
                "workspace",
                "--provider",
                "test",
                "--max-age-hours",
                "72",
                "--execute",
            ]);
            var delays = 0;
            var run = () =>
                ReviewRunner.RunAsync(
                    options,
                    config,
                    client,
                    Sandbox,
                    CancellationToken.None,
                    _ =>
                    {
                        delays++;
                        return Task.CompletedTask;
                    },
                    () => now
                );

            // The scripted run then errors; this test only cares how the send got there.
            await run.Should().ThrowAsync<InvalidOperationException>();

            var sends = handler
                .Requests.Where(r => r.Uri.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal))
                .Select(r => r.Body)
                .ToList();
            sends.Should().HaveCount(3);
            sends.Distinct().Should().ContainSingle("every resend carries the same idempotency key");
            delays.Should().BeGreaterThanOrEqualTo(2 * ReviewRunner.SendRetryPolls);
            var attempt = await store.GetAsync<JsonObject>($"reviewd/review-attempt/{repo.NormalizedKey}/17");
            attempt!["reviewInputId"]!.GetValue<string>().Should().Be("original");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }

    // Boundary case for the retry loop above: success on exactly the LAST allowed send
    // (SendAttempts == 11, so 10 failures then a success on attempt 11). Proves the full retry budget
    // is usable, not just the first couple of retries.
    [Fact]
    public async Task Runner_retries_a_503_review_send_up_to_the_last_allowed_attempt_then_succeeds()
    {
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-send503-last-{Guid.NewGuid():N}.db");
        var repo = PrCommands.ParseRepo("org/project/Nova");
        var now = DateTimeOffset.Parse("2026-10-07T22:00:00Z");
        var handler = new FakeHttpMessageHandler()
            .OnJson(
                HttpMethod.Get,
                "/capabilities",
                """{"messageIdempotency":true,"spawnSuppression":true,"rootReasoningEffort":true,"conversationWorkingDirectory":true}"""
            )
            .On(
                request =>
                    request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/conversations",
                _ => Json("""{"threadId":"thread"}""")
            )
            .OnJson(HttpMethod.Get, "/api/conversations?", "[]")
            .OnJson(HttpMethod.Put, "/metadata", "{}")
            .OnSequence(
                HttpMethod.Post,
                "/messages",
                (HttpStatusCode.ServiceUnavailable, "{}"),
                (HttpStatusCode.ServiceUnavailable, "{}"),
                (HttpStatusCode.ServiceUnavailable, "{}"),
                (HttpStatusCode.ServiceUnavailable, "{}"),
                (HttpStatusCode.ServiceUnavailable, "{}"),
                (HttpStatusCode.ServiceUnavailable, "{}"),
                (HttpStatusCode.ServiceUnavailable, "{}"),
                (HttpStatusCode.ServiceUnavailable, "{}"),
                (HttpStatusCode.ServiceUnavailable, "{}"),
                (HttpStatusCode.ServiceUnavailable, "{}"),
                (HttpStatusCode.OK, """{"inputId":"original","idempotencyKeyHonored":true}""")
            )
            .OnJson(HttpMethod.Get, "/status?", """{"status":"Errored","runId":"run","response":null}""")
            .OnJson(HttpMethod.Get, "/run-state", """{"isInProgress":false,"currentRunId":null}""")
            .OnJson(HttpMethod.Get, "/subagents?", """{"schemaVersion":1,"nodes":[]}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var client = new LmStreamingS2SClient(http, null, null, null);
        using var config = new ReviewdConfig(new ConfigurationBuilder().Build());
        Task<JsonObject> Sandbox(string phase, JsonObject args, string operation, CancellationToken token) =>
            Task.FromResult(
                phase switch
                {
                    "eligibility" => new JsonObject
                    {
                        ["prId"] = "17",
                        ["status"] = "active",
                        ["isDraft"] = false,
                        ["headSha"] = new string('a', 40),
                        ["createdAt"] = now.AddHours(-1).ToString("O"),
                    },
                    "prepare" => new JsonObject
                    {
                        ["mergeSha"] = new string('b', 40),
                        ["contextPath"] = "context",
                        ["reviewDirectory"] = "reviews",
                        ["baselineComments"] = new JsonArray(),
                    },
                    _ => [],
                }
            );
        try
        {
            using var connection = PrThreadBindings.Connection(database);
            var store = new SqliteKvStore(connection);
            await store.SetAsync(
                $"reviewd/pr-snapshot/{repo.NormalizedKey}",
                new JsonObject
                {
                    ["17"] = new JsonObject
                    {
                        ["Lifecycle"] = "Open",
                        ["IsDraft"] = false,
                        ["HeadSha"] = new string('a', 40),
                    },
                }
            );
            var options = CliOptions.Parse([
                "review",
                "run",
                "--repo",
                repo.DisplayName,
                "--pr",
                "17",
                "--slot",
                "2",
                "--db",
                database,
                "--session",
                "session",
                "--workspace",
                "workspace",
                "--provider",
                "test",
                "--max-age-hours",
                "72",
                "--execute",
            ]);
            var delays = 0;
            var run = () =>
                ReviewRunner.RunAsync(
                    options,
                    config,
                    client,
                    Sandbox,
                    CancellationToken.None,
                    _ =>
                    {
                        delays++;
                        return Task.CompletedTask;
                    },
                    () => now
                );

            // The scripted run errors AFTER the send succeeds (via the Errored status, same as the
            // sibling test above); this test only cares that the 11th send was still attempted and
            // accepted rather than the loop giving up before the documented budget was exhausted.
            await run.Should().ThrowAsync<InvalidOperationException>();

            var sends = handler
                .Requests.Where(r => r.Uri.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal))
                .Select(r => r.Body)
                .ToList();
            sends.Should().HaveCount(ReviewRunner.SendAttempts);
            sends.Distinct().Should().ContainSingle("every resend carries the same idempotency key");
            delays.Should().BeGreaterThanOrEqualTo((ReviewRunner.SendAttempts - 1) * ReviewRunner.SendRetryPolls);
            var attempt = await store.GetAsync<JsonObject>($"reviewd/review-attempt/{repo.NormalizedKey}/17");
            attempt!["reviewInputId"]!.GetValue<string>().Should().Be("original");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }

    // The other boundary: exhausting the documented 11-send retry budget without ever succeeding. The
    // 11th 503 is NOT retried (`sendAttempt < SendAttempts` is false at that point), so it must escape
    // as the transport failure itself rather than being silently absorbed or retried forever.
    [Fact]
    public async Task Runner_stops_retrying_a_503_review_send_once_the_retry_budget_is_exhausted()
    {
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-send503-exhausted-{Guid.NewGuid():N}.db");
        var repo = PrCommands.ParseRepo("org/project/Nova");
        var now = DateTimeOffset.Parse("2026-10-07T22:00:00Z");
        var handler = new FakeHttpMessageHandler()
            .OnJson(
                HttpMethod.Get,
                "/capabilities",
                """{"messageIdempotency":true,"spawnSuppression":true,"rootReasoningEffort":true,"conversationWorkingDirectory":true}"""
            )
            .On(
                request =>
                    request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/conversations",
                _ => Json("""{"threadId":"thread"}""")
            )
            .OnJson(HttpMethod.Get, "/api/conversations?", "[]")
            .OnJson(HttpMethod.Put, "/metadata", "{}")
            // A single entry; OnSequence repeats the last response once exhausted, so every send gets
            // a 503 — the host never recovers in this scenario.
            .OnSequence(HttpMethod.Post, "/messages", (HttpStatusCode.ServiceUnavailable, "{}"))
            .OnJson(HttpMethod.Get, "/run-state", """{"isInProgress":false,"currentRunId":null}""")
            .OnJson(HttpMethod.Get, "/subagents?", """{"schemaVersion":1,"nodes":[]}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var client = new LmStreamingS2SClient(http, null, null, null);
        using var config = new ReviewdConfig(new ConfigurationBuilder().Build());
        Task<JsonObject> Sandbox(string phase, JsonObject args, string operation, CancellationToken token) =>
            Task.FromResult(
                phase switch
                {
                    "eligibility" => new JsonObject
                    {
                        ["prId"] = "17",
                        ["status"] = "active",
                        ["isDraft"] = false,
                        ["headSha"] = new string('a', 40),
                        ["createdAt"] = now.AddHours(-1).ToString("O"),
                    },
                    "prepare" => new JsonObject
                    {
                        ["mergeSha"] = new string('b', 40),
                        ["contextPath"] = "context",
                        ["reviewDirectory"] = "reviews",
                        ["baselineComments"] = new JsonArray(),
                    },
                    _ => [],
                }
            );
        try
        {
            using var connection = PrThreadBindings.Connection(database);
            var store = new SqliteKvStore(connection);
            await store.SetAsync(
                $"reviewd/pr-snapshot/{repo.NormalizedKey}",
                new JsonObject
                {
                    ["17"] = new JsonObject
                    {
                        ["Lifecycle"] = "Open",
                        ["IsDraft"] = false,
                        ["HeadSha"] = new string('a', 40),
                    },
                }
            );
            var options = CliOptions.Parse([
                "review",
                "run",
                "--repo",
                repo.DisplayName,
                "--pr",
                "17",
                "--slot",
                "2",
                "--db",
                database,
                "--session",
                "session",
                "--workspace",
                "workspace",
                "--provider",
                "test",
                "--max-age-hours",
                "72",
                "--execute",
            ]);
            var delays = 0;
            var run = () =>
                ReviewRunner.RunAsync(
                    options,
                    config,
                    client,
                    Sandbox,
                    CancellationToken.None,
                    _ =>
                    {
                        delays++;
                        return Task.CompletedTask;
                    },
                    () => now
                );

            // The 11th attempt is not retried, so the send's own transport failure must escape
            // unchanged rather than the run reporting some other, unrelated terminal error.
            var thrown = await run.Should().ThrowAsync<HttpRequestException>();
            thrown.Which.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

            var sends = handler
                .Requests.Where(r => r.Uri.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal))
                .Select(r => r.Body)
                .ToList();
            sends.Should().HaveCount(ReviewRunner.SendAttempts, "no retry follows the last allowed attempt");
            sends.Distinct().Should().ContainSingle("every resend carries the same idempotency key");
            delays.Should().BeGreaterThanOrEqualTo((ReviewRunner.SendAttempts - 1) * ReviewRunner.SendRetryPolls);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }

    [Theory]
    [InlineData(false, "99999999")]
    [InlineData(true, "99999999")]
    [InlineData(false, "aaaaaaaa")]
    public async Task Runner_admits_pr_whose_prior_conversation_is_idle_and_refuses_one_still_running(
        bool running,
        string priorHead
    )
    {
        // The prior review's receipt lives in whichever db ran it; this db only sees the host conversation.
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-prior-{Guid.NewGuid():N}.db");
        var repo = PrCommands.ParseRepo("org/project/Nova");
        var handler = new FakeHttpMessageHandler()
            .OnJson(
                HttpMethod.Get,
                "/capabilities",
                """{"messageIdempotency":true,"spawnSuppression":true,"rootReasoningEffort":true,"conversationWorkingDirectory":true}"""
            )
            .OnJson(
                HttpMethod.Get,
                "/api/conversations?",
                $$"""[{"threadId":"prior","title":"Nova-17-{{priorHead}}","workspace":"workspace","mode":"workspace-agent","provider":"test","lastUpdated":1,"clientToolsDisabled":true}]"""
            )
            .OnJson(HttpMethod.Put, "/metadata", "{}")
            // A provider that cannot freeze (Copilot, Codex, Claude SDK loops) refuses the thaw; it was never frozen.
            .On(
                request => request.RequestUri!.AbsolutePath.EndsWith("/unfreeze", StringComparison.Ordinal),
                _ => new HttpResponseMessage(HttpStatusCode.Conflict)
                {
                    Content = new StringContent("""{"error":"not_freezable","threadId":"prior"}"""),
                }
            )
            .OnJson(HttpMethod.Get, "/prior/messages", Transcript(("User", ReviewPrompt('a')), ("Assistant", "Done.")))
            .OnJson(HttpMethod.Post, "/messages", """{"inputId":"original","idempotencyKeyHonored":true}""")
            .OnJson(HttpMethod.Get, "/status?", """{"status":"Errored","runId":"run","response":null}""")
            .OnJson(
                HttpMethod.Get,
                "/run-state",
                running
                    ? """{"isInProgress":true,"currentRunId":"run"}"""
                    : """{"isInProgress":false,"currentRunId":null}"""
            )
            .OnJson(HttpMethod.Get, "/subagents?", """{"schemaVersion":1,"nodes":[]}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var client = new LmStreamingS2SClient(http, null, null, null);
        using var config = new ReviewdConfig(new ConfigurationBuilder().Build());
        static Task<JsonObject> Sandbox(string phase, JsonObject args, string operation, CancellationToken token) =>
            Task.FromResult(
                phase switch
                {
                    "eligibility" => new JsonObject
                    {
                        ["prId"] = "17",
                        ["status"] = "active",
                        ["isDraft"] = false,
                        ["headSha"] = new string('a', 40),
                    },
                    "prepare" => new JsonObject
                    {
                        ["mergeSha"] = new string('b', 40),
                        ["contextPath"] = "context",
                        ["reviewDirectory"] = "reviews",
                        ["baselineComments"] = new JsonArray(),
                    },
                    _ => [],
                }
            );
        try
        {
            using var connection = PrThreadBindings.Connection(database);
            await new SqliteKvStore(connection).SetAsync(
                $"reviewd/pr-snapshot/{repo.NormalizedKey}",
                new JsonObject
                {
                    ["17"] = new JsonObject
                    {
                        ["Lifecycle"] = "Open",
                        ["IsDraft"] = false,
                        ["HeadSha"] = new string('a', 40),
                    },
                }
            );
            var options = CliOptions.Parse([
                "review",
                "run",
                "--repo",
                repo.DisplayName,
                "--pr",
                "17",
                "--slot",
                "2",
                "--db",
                database,
                "--session",
                "session",
                "--workspace",
                "workspace",
                "--provider",
                "test",
                "--execute",
            ]);
            var run = () =>
                ReviewRunner.RunAsync(
                    options,
                    config,
                    client,
                    Sandbox,
                    CancellationToken.None,
                    _ => Task.CompletedTask,
                    () => DateTimeOffset.UtcNow
                );
            // A conversation that already ran the live head's review means another db reviewed this revision.
            var sameHead = priorHead == "aaaaaaaa";
            if (sameHead)
                await run.Should()
                    .ThrowAsync<InvalidOperationException>()
                    .WithMessage("PR head was already reviewed by another run; skipped.");
            else if (running)
                await run.Should().ThrowAsync<ReviewRunner.ReviewDeferredException>();
            else
                (await run.Should().ThrowAsync<InvalidOperationException>())
                    .Which.Message.Should()
                    .NotBe("PR is not eligible for review.");
            handler
                .Requests.Count(r =>
                    r.Method == HttpMethod.Post && r.Uri.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal)
                )
                .Should()
                .Be(running || sameHead ? 0 : 1);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }

    [Theory]
    [InlineData("never-sent")]
    [InlineData("prompt-without-output")]
    [InlineData("earlier-head")]
    [InlineData("completed")]
    public async Task Runner_skips_a_head_only_when_its_titled_conversation_shows_the_review_ran(string history)
    {
        // The title is stamped before the review is sent. A send that failed (a 503) left "Nova-17-aaaaaaaa" on a
        // conversation that reviewed nothing, and every later batch skipped the PR as already reviewed.
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-titled-{Guid.NewGuid():N}.db");
        var repo = PrCommands.ParseRepo("org/project/Nova");
        var transcript = history switch
        {
            "never-sent" => Transcript(),
            "prompt-without-output" => Transcript(("User", ReviewPrompt('a'))),
            // An earlier head's review, retitled for the live head just before its own send failed.
            "earlier-head" => Transcript(("User", ReviewPrompt('d')), ("Assistant", "Done.")),
            _ => Transcript(("User", ReviewPrompt('a')), ("Assistant", "Done.")),
        };
        var handler = new FakeHttpMessageHandler()
            .OnJson(
                HttpMethod.Get,
                "/capabilities",
                """{"messageIdempotency":true,"spawnSuppression":true,"rootReasoningEffort":true,"conversationWorkingDirectory":true}"""
            )
            .OnJson(
                HttpMethod.Get,
                "/api/conversations?",
                """[{"threadId":"prior","title":"Nova-17-aaaaaaaa","workspace":"workspace","mode":"workspace-agent","provider":"test","lastUpdated":1,"clientToolsDisabled":true}]"""
            )
            .OnJson(HttpMethod.Get, "/prior/messages", transcript)
            .OnJson(HttpMethod.Put, "/metadata", "{}")
            .On(
                request => request.RequestUri!.AbsolutePath.EndsWith("/unfreeze", StringComparison.Ordinal),
                _ => new HttpResponseMessage(HttpStatusCode.Conflict)
                {
                    Content = new StringContent("""{"error":"not_freezable","threadId":"prior"}"""),
                }
            )
            .OnJson(HttpMethod.Post, "/messages", """{"inputId":"original","idempotencyKeyHonored":true}""")
            .OnJson(HttpMethod.Get, "/status?", """{"status":"Errored","runId":"run","response":null}""")
            .OnJson(HttpMethod.Get, "/run-state", """{"isInProgress":false,"currentRunId":null}""")
            .OnJson(HttpMethod.Get, "/subagents?", """{"schemaVersion":1,"nodes":[]}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var client = new LmStreamingS2SClient(http, null, null, null);
        using var config = new ReviewdConfig(new ConfigurationBuilder().Build());
        static Task<JsonObject> Sandbox(string phase, JsonObject args, string operation, CancellationToken token) =>
            Task.FromResult(
                phase == "prepare"
                    ? new JsonObject
                    {
                        ["mergeSha"] = new string('b', 40),
                        ["contextPath"] = "context",
                        ["reviewDirectory"] = "reviews",
                        ["baselineComments"] = new JsonArray(),
                    }
                    : []
            );
        try
        {
            using var connection = PrThreadBindings.Connection(database);
            await new SqliteKvStore(connection).SetAsync(
                $"reviewd/pr-snapshot/{repo.NormalizedKey}",
                new JsonObject
                {
                    ["17"] = new JsonObject
                    {
                        ["Lifecycle"] = "Open",
                        ["IsDraft"] = false,
                        ["HeadSha"] = new string('a', 40),
                    },
                }
            );
            var options = CliOptions.Parse([
                "review",
                "run",
                "--repo",
                repo.DisplayName,
                "--pr",
                "17",
                "--slot",
                "2",
                "--db",
                database,
                "--session",
                "session",
                "--workspace",
                "workspace",
                "--provider",
                "test",
                "--execute",
            ]);
            var run = () =>
                ReviewRunner.RunAsync(
                    options,
                    config,
                    client,
                    Sandbox,
                    CancellationToken.None,
                    _ => Task.CompletedTask,
                    () => DateTimeOffset.UtcNow
                );
            var failure = (await run.Should().ThrowAsync<InvalidOperationException>()).Which;
            var sends = handler
                .Requests.Where(r =>
                    r.Method == HttpMethod.Post
                    && r.Uri.AbsolutePath.EndsWith("/prior/messages", StringComparison.Ordinal)
                )
                .ToList();
            if (history == "completed")
            {
                failure.Message.Should().Be("PR head was already reviewed by another run; skipped.");
                sends.Should().BeEmpty();
                return;
            }
            // The titled conversation is reused, and the review is sent into it.
            failure.Message.Should().NotBe("PR head was already reviewed by another run; skipped.");
            sends.Should().ContainSingle();
            // Only a conversation that holds an earlier review is told it has one.
            const string reReview = "This conversation already reviewed an earlier revision";
            if (history == "earlier-head")
                sends[0].Body.Should().Contain(reReview);
            else
                sends[0].Body.Should().NotContain(reReview);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }

    // The persisted-message rows the host's GET api/conversations/{threadId}/messages returns.
    private static string Transcript(params (string Role, string Text)[] messages) =>
        new JsonArray(
            messages
                .Select(message =>
                    (JsonNode)
                        new JsonObject
                        {
                            ["messageType"] = "text",
                            ["role"] = message.Role,
                            ["timestamp"] = 1,
                            ["messageJson"] = new JsonObject { ["text"] = message.Text }.ToJsonString(),
                        }
                )
                .ToArray()
        ).ToJsonString();

    private static string ReviewPrompt(char head) =>
        $"Review org/project/Nova PR #17 in slot Nova-2.\nExpected source head {new string(head, 40)}; prepared merge {new string('b', 40)}.";

    [Theory]
    [InlineData('d')]
    [InlineData('a')]
    public async Task Runner_replaces_a_released_attempt_only_when_the_head_moved_on(char attemptHead)
    {
        // The reaper released slot 2 at review-completed; the stage stayed and the conversation is idle.
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-released-{Guid.NewGuid():N}.db");
        var repo = PrCommands.ParseRepo("org/project/Nova");
        var attemptKey = $"reviewd/review-attempt/{repo.NormalizedKey}/17";
        var handler = new FakeHttpMessageHandler()
            .OnJson(
                HttpMethod.Get,
                "/capabilities",
                """{"messageIdempotency":true,"spawnSuppression":true,"rootReasoningEffort":true,"conversationWorkingDirectory":true}"""
            )
            .OnJson(
                HttpMethod.Get,
                "/api/conversations?",
                """[{"threadId":"prior","title":"Nova-17-dddddddd","workspace":"workspace","mode":"workspace-agent","provider":"test","lastUpdated":1,"clientToolsDisabled":true}]"""
            )
            .OnJson(HttpMethod.Put, "/metadata", "{}")
            .On(
                request => request.RequestUri!.AbsolutePath.EndsWith("/unfreeze", StringComparison.Ordinal),
                _ => new HttpResponseMessage(HttpStatusCode.Conflict)
                {
                    Content = new StringContent("""{"error":"not_freezable","threadId":"prior"}"""),
                }
            )
            .OnJson(HttpMethod.Post, "/messages", """{"inputId":"original","idempotencyKeyHonored":true}""")
            .OnJson(HttpMethod.Get, "/status?", """{"status":"Errored","runId":"run","response":null}""")
            .OnJson(HttpMethod.Get, "/run-state", """{"isInProgress":false,"currentRunId":null}""")
            .OnJson(HttpMethod.Get, "/subagents?", """{"schemaVersion":1,"nodes":[]}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var client = new LmStreamingS2SClient(http, null, null, null);
        using var config = new ReviewdConfig(new ConfigurationBuilder().Build());
        static Task<JsonObject> Sandbox(string phase, JsonObject args, string operation, CancellationToken token) =>
            Task.FromResult(
                phase switch
                {
                    "eligibility" => new JsonObject
                    {
                        ["prId"] = "17",
                        ["status"] = "active",
                        ["isDraft"] = false,
                        ["headSha"] = new string('a', 40),
                    },
                    "prepare" => new JsonObject
                    {
                        ["mergeSha"] = new string('b', 40),
                        ["contextPath"] = "context",
                        ["reviewDirectory"] = "reviews",
                        ["baselineComments"] = new JsonArray(),
                    },
                    _ => [],
                }
            );
        try
        {
            using var connection = PrThreadBindings.Connection(database);
            var store = new SqliteKvStore(connection);
            await store.SetAsync(
                $"reviewd/pr-snapshot/{repo.NormalizedKey}",
                new JsonObject
                {
                    ["17"] = new JsonObject
                    {
                        ["Lifecycle"] = "Open",
                        ["IsDraft"] = false,
                        ["HeadSha"] = new string('a', 40),
                    },
                }
            );
            await store.SetAsync(
                attemptKey,
                new JsonObject
                {
                    ["repository"] = repo.DisplayName,
                    ["prId"] = "17",
                    ["slot"] = 2,
                    ["workspaceId"] = "workspace",
                    ["stage"] = "review-completed",
                    ["cleanupState"] = "released",
                    ["claimToken"] = "earlier-claim",
                    ["reviewInputId"] = "earlier-input",
                    ["threadId"] = "prior",
                    ["headSha"] = new string(attemptHead, 40),
                }
            );
            var options = CliOptions.Parse([
                "review",
                "run",
                "--repo",
                repo.DisplayName,
                "--pr",
                "17",
                "--slot",
                "2",
                "--db",
                database,
                "--session",
                "session",
                "--workspace",
                "workspace",
                "--provider",
                "test",
                "--execute",
            ]);
            var run = () =>
                ReviewRunner.RunAsync(
                    options,
                    config,
                    client,
                    Sandbox,
                    CancellationToken.None,
                    _ => Task.CompletedTask,
                    () => DateTimeOffset.UtcNow
                );
            var earlierHead = attemptHead == 'd';
            var failure = (await run.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
            var messages = handler.Requests.Count(r =>
                r.Uri.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal)
            );
            using var history = connection.CreateCommand();
            history.CommandText = "SELECT value FROM cache WHERE key LIKE 'reviewd/attempt-history/%'";
            var archived = await history.ExecuteScalarAsync() as string;
            if (earlierHead)
            {
                // The new head is reviewed fresh, and the attempt it overwrites is kept in history.
                failure.Should().NotStartWith("An incomplete review attempt exists");
                messages.Should().Be(1);
                archived.Should().Contain("earlier-claim");
            }
            else
            {
                // The same head may already have posted, so it is never re-sent.
                failure.Should().StartWith("An incomplete review attempt exists");
                messages.Should().Be(0);
                archived.Should().BeNull();
                (await store.GetAsync<JsonObject>(attemptKey))!["claimToken"]!
                    .GetValue<string>()
                    .Should()
                    .Be("earlier-claim");
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }

    [Theory]
    [InlineData("prior", 1, null, true, null)]
    [InlineData("other", 1, "PR head was already reviewed by another run; skipped.", true, null)]
    // The batch can admit a failed entry again without a new pick, so the runner holds the reset cap too.
    [InlineData("prior", 3, "PR head was reset 3 times; skipped until a new push.", true, null)]
    // A reset conversation from before headless provisioning is passed over: the retry runs in a new headless one.
    [InlineData("prior", 1, null, false, null)]
    [InlineData("other", 1, "PR head was already reviewed by another run; skipped.", false, null)]
    // That retry was reset in turn before its new conversation got this head's title: the titled one it passed over is
    // still its own, and stays named for the next retry.
    [InlineData("prior", 2, null, false, "fresh")]
    public async Task Runner_reviews_its_own_reset_review_of_the_same_head_again(
        string titledThread,
        int resetCount,
        string? refusal,
        bool headless,
        string? retryThread
    )
    {
        // Kay9: a review the reaper reset before its publication was verified is reviewed again, in its own
        // conversation or a new headless one, told to post only what is missing. A conversation for this head that is
        // not ours still means another run reviewed it.
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-reset-retry-{Guid.NewGuid():N}.db");
        var repo = PrCommands.ParseRepo("org/project/Nova");
        var attemptKey = $"reviewd/review-attempt/{repo.NormalizedKey}/17";
        var handler = new FakeHttpMessageHandler()
            .OnJson(
                HttpMethod.Get,
                "/capabilities",
                """{"messageIdempotency":true,"spawnSuppression":true,"rootReasoningEffort":true,"conversationWorkingDirectory":true}"""
            )
            .OnJson(
                HttpMethod.Get,
                "/api/conversations?",
                $$"""[{"threadId":"{{titledThread}}","title":"Nova-17-aaaaaaaa","workspace":"workspace","mode":"workspace-agent","provider":"test","lastUpdated":1,"clientToolsDisabled":{{(headless ? "true" : "false")}}}{{(retryThread is null ? "" : $$""",{"threadId":"{{retryThread}}","title":"New conversation","workspace":"workspace","mode":"workspace-agent","provider":"test","lastUpdated":2,"clientToolsDisabled":true}""")}}]"""
            )
            .On(
                request =>
                    request.RequestUri!.AbsolutePath == "/api/conversations" && request.Method == HttpMethod.Post,
                _ => Json("""{"threadId":"fresh"}""")
            )
            // The titled conversation ran this head's review, so only our own reset is admitted past it.
            .OnJson(
                HttpMethod.Get,
                $"/{titledThread}/messages",
                Transcript(("User", ReviewPrompt('a')), ("Assistant", "Done."))
            )
            .OnJson(HttpMethod.Put, "/metadata", "{}")
            .On(
                request => request.RequestUri!.AbsolutePath.EndsWith("/unfreeze", StringComparison.Ordinal),
                _ => new HttpResponseMessage(HttpStatusCode.Conflict)
                {
                    Content = new StringContent("""{"error":"not_freezable","threadId":"prior"}"""),
                }
            )
            .OnJson(HttpMethod.Post, "/messages", """{"inputId":"original","idempotencyKeyHonored":true}""")
            .OnJson(HttpMethod.Get, "/status?", """{"status":"Errored","runId":"run","response":null}""")
            .OnJson(HttpMethod.Get, "/run-state", """{"isInProgress":false,"currentRunId":null}""")
            .OnJson(HttpMethod.Get, "/subagents?", """{"schemaVersion":1,"nodes":[]}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var client = new LmStreamingS2SClient(http, null, null, null);
        using var config = new ReviewdConfig(new ConfigurationBuilder().Build());
        static Task<JsonObject> Sandbox(string phase, JsonObject args, string operation, CancellationToken token) =>
            Task.FromResult(
                phase switch
                {
                    "eligibility" => new JsonObject
                    {
                        ["prId"] = "17",
                        ["status"] = "active",
                        ["isDraft"] = false,
                        ["headSha"] = new string('a', 40),
                    },
                    "prepare" => new JsonObject
                    {
                        ["mergeSha"] = new string('b', 40),
                        ["contextPath"] = "context",
                        ["reviewDirectory"] = "reviews",
                        ["baselineComments"] = new JsonArray(),
                    },
                    _ => [],
                }
            );
        try
        {
            using var connection = PrThreadBindings.Connection(database);
            var store = new SqliteKvStore(connection);
            await store.SetAsync(
                $"reviewd/pr-snapshot/{repo.NormalizedKey}",
                new JsonObject
                {
                    ["17"] = new JsonObject
                    {
                        ["Lifecycle"] = "Open",
                        ["IsDraft"] = false,
                        ["HeadSha"] = new string('a', 40),
                    },
                }
            );
            await store.SetAsync(
                attemptKey,
                new JsonObject
                {
                    ["repository"] = repo.DisplayName,
                    ["prId"] = "17",
                    ["slot"] = 2,
                    ["workspaceId"] = "workspace",
                    ["stage"] = "failed-released",
                    ["stageBeforeReclaim"] = "review-completed",
                    ["resetCount"] = resetCount,
                    ["cleanupState"] = "released",
                    ["claimToken"] = "earlier-claim",
                    ["reviewInputId"] = "earlier-input",
                    ["threadId"] = retryThread ?? "prior",
                    ["headSha"] = new string('a', 40),
                }
            );
            if (retryThread is not null)
            {
                var stored = (await store.GetAsync<JsonObject>(attemptKey))!;
                stored["resetThreadId"] = "prior";
                await store.SetAsync(attemptKey, stored);
                await PrThreadBindings.SetAsync(database, repo, "17", retryThread, CancellationToken.None);
            }
            var options = CliOptions.Parse([
                "review",
                "run",
                "--repo",
                repo.DisplayName,
                "--pr",
                "17",
                "--slot",
                "2",
                "--db",
                database,
                "--session",
                "session",
                "--workspace",
                "workspace",
                "--provider",
                "test",
                "--execute",
            ]);
            var run = () =>
                ReviewRunner.RunAsync(
                    options,
                    config,
                    client,
                    Sandbox,
                    CancellationToken.None,
                    _ => Task.CompletedTask,
                    () => DateTimeOffset.UtcNow
                );
            var failure = (await run.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
            var sent = handler
                .Requests.Where(r =>
                    r.Method == HttpMethod.Post && r.Uri.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal)
                )
                .ToList();
            if (refusal is null)
            {
                failure.Should().NotContain("already reviewed by another run");
                // Verify accepts only what this run posts and needs a summary among it, so the retry must post a new
                // summary rather than rely on the one its reset run may already have posted.
                sent.Should()
                    .ContainSingle()
                    .Which.Uri.AbsolutePath.Should()
                    .Contain(headless ? "/prior/" : "/fresh/");
                sent.Single()
                    .Body.Should()
                    .Contain("stopped before its publication was")
                    .And.Contain("Always post a new summary for this run")
                    .And.Contain("only what this run posts")
                    .And.NotContain("post only the findings and summary that are missing");
                using var reread = PrThreadBindings.Connection(database);
                var retried = (await new SqliteKvStore(reread).GetAsync<JsonObject>(attemptKey))!;
                retried["resetCount"]!.GetValue<int>().Should().Be(resetCount);
                (retried["resetThreadId"]?.GetValue<string>()).Should().Be(headless ? null : "prior");
            }
            else
            {
                failure.Should().Be(refusal);
                sent.Should().BeEmpty();
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }

    [Theory]
    [InlineData("not-idle", true)]
    [InlineData("capacity", true)]
    // A reset conversation from before headless provisioning is passed over, and the refusal comes before the retry's
    // new conversation exists.
    [InlineData("not-idle", false)]
    public async Task A_reset_retry_refused_before_review_is_picked_and_retried_without_a_new_push(
        string refusal,
        bool headless
    )
    {
        // The retry of a reset gets a fresh receipt. A slot or capacity refusal at prepare releases that receipt as
        // failed-released, and it must still read as a reset retry: otherwise pick skips the PR behind its bound
        // conversation, and the runner refuses the head that conversation already reviewed, until a new push.
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-reset-refused-{Guid.NewGuid():N}.db");
        var selection = database + ".selection.json";
        var repo = PrCommands.ParseRepo("org/project/Nova");
        var attemptKey = $"reviewd/review-attempt/{repo.NormalizedKey}/17";
        var head = new string('a', 40);
        var handler = new FakeHttpMessageHandler()
            .OnJson(
                HttpMethod.Get,
                "/capabilities",
                """{"messageIdempotency":true,"spawnSuppression":true,"rootReasoningEffort":true,"conversationWorkingDirectory":true}"""
            )
            .OnJson(
                HttpMethod.Get,
                "/api/conversations?",
                $$"""[{"threadId":"prior","title":"Nova-17-aaaaaaaa","workspace":"workspace","mode":"workspace-agent","provider":"test","lastUpdated":1,"clientToolsDisabled":{{(headless ? "true" : "false")}}}]"""
            )
            .On(
                request =>
                    request.RequestUri!.AbsolutePath == "/api/conversations" && request.Method == HttpMethod.Post,
                _ => Json("""{"threadId":"fresh"}""")
            )
            .OnJson(HttpMethod.Get, "/prior/messages", Transcript(("User", ReviewPrompt('a')), ("Assistant", "Done.")))
            .OnJson(HttpMethod.Put, "/metadata", "{}")
            .On(
                request => request.RequestUri!.AbsolutePath.EndsWith("/unfreeze", StringComparison.Ordinal),
                _ => new HttpResponseMessage(HttpStatusCode.Conflict)
                {
                    Content = new StringContent("""{"error":"not_freezable","threadId":"prior"}"""),
                }
            )
            .OnJson(HttpMethod.Post, "/messages", """{"inputId":"retry","idempotencyKeyHonored":true}""")
            .OnJson(HttpMethod.Get, "/status?", """{"status":"Errored","runId":"run","response":null}""")
            .OnJson(HttpMethod.Get, "/run-state", """{"isInProgress":false,"currentRunId":null}""")
            .OnJson(HttpMethod.Get, "/subagents?", """{"schemaVersion":1,"nodes":[]}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var client = new LmStreamingS2SClient(http, null, null, null);
        using var config = new ReviewdConfig(new ConfigurationBuilder().Build());
        var refuse = true;
        Task<JsonObject> Sandbox(string phase, JsonObject args, string operation, CancellationToken token)
        {
            if (phase == "prepare" && refuse)
                throw refusal == "not-idle"
                    ? new ReviewRunner.SlotNotIdleException("Native slot retains an owner.")
                    : new SandboxException(SandboxErrorKind.Unavailable, "refused", 503)
                    {
                        ErrorCode = "operation_capacity_exhausted",
                    };
            return Task.FromResult(
                phase == "prepare"
                    ? new JsonObject
                    {
                        ["mergeSha"] = new string('b', 40),
                        ["contextPath"] = "context",
                        ["reviewDirectory"] = "reviews",
                        ["baselineComments"] = new JsonArray(),
                    }
                    : []
            );
        }
        try
        {
            using (var connection = PrThreadBindings.Connection(database))
            {
                var store = new SqliteKvStore(connection);
                await store.SetAsync(
                    $"reviewd/pr-snapshot/{repo.NormalizedKey}",
                    new JsonObject
                    {
                        ["17"] = new JsonObject
                        {
                            ["PrId"] = 17,
                            ["Lifecycle"] = "Open",
                            ["IsDraft"] = false,
                            ["HeadSha"] = head,
                        },
                    }
                );
                await store.SetAsync(
                    attemptKey,
                    new JsonObject
                    {
                        ["repository"] = repo.DisplayName,
                        ["prId"] = "17",
                        ["slot"] = 2,
                        ["workspaceId"] = "workspace",
                        ["stage"] = "failed-released",
                        ["stageBeforeReclaim"] = "review-running",
                        ["resetCount"] = 1,
                        ["cleanupState"] = "released",
                        ["claimToken"] = "reset-claim",
                        ["reviewInputId"] = "earlier-input",
                        ["threadId"] = "prior",
                        ["headSha"] = head,
                    }
                );
            }
            var options = CliOptions.Parse([
                "review",
                "run",
                "--repo",
                repo.DisplayName,
                "--pr",
                "17",
                "--slot",
                "2",
                "--db",
                database,
                "--session",
                "session",
                "--workspace",
                "workspace",
                "--provider",
                "test",
                "--execute",
            ]);
            var run = () =>
                ReviewRunner.RunAsync(
                    options,
                    config,
                    client,
                    Sandbox,
                    CancellationToken.None,
                    _ => Task.CompletedTask,
                    () => DateTimeOffset.UtcNow
                );

            await run.Should().ThrowAsync<Exception>();
            using (var reread = PrThreadBindings.Connection(database))
            {
                var refused = (await new SqliteKvStore(reread).GetAsync<JsonObject>(attemptKey))!;
                refused["admissionRefused"].Should().NotBeNull("prepare refused the retry before it reviewed");
                PrSelection
                    .IsResetForRetry(System.Text.Json.JsonSerializer.SerializeToElement(refused), "prior", head)
                    .Should()
                    .BeTrue();
            }
            await PrSelection.RunAsync(
                CliOptions.Parse([
                    "pr",
                    "pick",
                    "--repo",
                    repo.DisplayName,
                    "--db",
                    database,
                    "--selection-out",
                    selection,
                ]),
                CancellationToken.None,
                DateTimeOffset.UtcNow
            );
            JsonNode.Parse(await File.ReadAllTextAsync(selection))!["repositories"]!
                .AsObject()
                .Should()
                .ContainKey(repo.DisplayName, "the PR is offered again without a new push");

            refuse = false;
            var failure = (await run.Should().ThrowAsync<InvalidOperationException>()).Which.Message;

            failure.Should().NotContain("already reviewed by another run");
            var sent = handler
                .Requests.Where(r =>
                    r.Method == HttpMethod.Post && r.Uri.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal)
                )
                .Should()
                .ContainSingle()
                .Which;
            sent.Uri.AbsolutePath.Should().Contain(headless ? "/prior/" : "/fresh/");
            sent.Body.Should().Contain("stopped before its publication was");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
            File.Delete(selection);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("xhigh")]
    public async Task Runner_records_and_provisions_optional_effort_and_guards_resume_effort_mismatch(string? effort)
    {
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-effort-{Guid.NewGuid():N}.db");
        var repo = PrCommands.ParseRepo("org/project/Nova");
        var attemptKey = $"reviewd/review-attempt/{repo.NormalizedKey}/17";
        var handler = new FakeHttpMessageHandler()
            .OnJson(
                HttpMethod.Get,
                "/capabilities",
                """{"messageIdempotency":true,"spawnSuppression":true,"rootReasoningEffort":true,"conversationWorkingDirectory":true}"""
            )
            .On(
                request =>
                    request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/conversations",
                _ => Json("""{"threadId":"thread","reasoningEffortAccepted":true}""")
            )
            // No prior conversation exists for this PR: the durable host-title lookup finds none.
            .OnJson(HttpMethod.Get, "/api/conversations?", "[]")
            .OnJson(HttpMethod.Put, "/metadata", "{}")
            .OnJson(HttpMethod.Post, "/messages", """{"inputId":"original","idempotencyKeyHonored":true}""")
            .OnJson(HttpMethod.Get, "/status?", """{"status":"Errored","runId":"run","response":null}""")
            .OnJson(HttpMethod.Get, "/run-state", """{"isInProgress":false,"currentRunId":null}""")
            .OnJson(HttpMethod.Get, "/subagents?", """{"schemaVersion":1,"nodes":[]}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var client = new LmStreamingS2SClient(http, null, null, null);
        using var config = new ReviewdConfig(new ConfigurationBuilder().Build());
        CliOptions Options(string? requested, bool resume = false) =>
            CliOptions.Parse([
                "review",
                "run",
                "--repo",
                repo.DisplayName,
                "--pr",
                "17",
                "--slot",
                "2",
                "--db",
                database,
                "--session",
                "session",
                "--workspace",
                "workspace",
                "--provider",
                "test",
                "--execute",
                .. requested is null ? [] : new string[] { "--reasoning-effort", requested },
                .. resume ? new string[] { "--resume" } : [],
            ]);
        var sandboxCalls = 0;
        Task<JsonObject> Sandbox(string phase, JsonObject args, string operation, CancellationToken token)
        {
            sandboxCalls++;
            return Task.FromResult(
                phase == "prepare"
                    ? new JsonObject
                    {
                        ["mergeSha"] = new string('b', 40),
                        ["contextPath"] = "context",
                        ["reviewDirectory"] = "reviews",
                        ["baselineComments"] = new JsonArray(),
                    }
                    : new JsonObject
                    {
                        ["prId"] = "17",
                        ["status"] = "active",
                        ["isDraft"] = false,
                        ["headSha"] = new string('a', 40),
                        ["mergeSha"] = new string('b', 40),
                    }
            );
        }
        try
        {
            using var connection = PrThreadBindings.Connection(database);
            var store = new SqliteKvStore(connection);
            await store.SetAsync(
                $"reviewd/pr-snapshot/{repo.NormalizedKey}",
                new JsonObject
                {
                    ["17"] = new JsonObject
                    {
                        ["Lifecycle"] = "Open",
                        ["IsDraft"] = false,
                        ["HeadSha"] = new string('a', 40),
                    },
                }
            );
            var run = () =>
                ReviewRunner.RunAsync(
                    Options(effort),
                    config,
                    client,
                    Sandbox,
                    CancellationToken.None,
                    _ => Task.CompletedTask
                );
            await run.Should().ThrowAsync<InvalidOperationException>();
            var attempt = (await store.GetAsync<JsonObject>(attemptKey))!;
            attempt.ContainsKey("reasoningEffort").Should().Be(effort is not null);
            var recordedEffort = attempt["reasoningEffort"]?.GetValue<string>();
            recordedEffort.Should().Be(effort);
            var provision = handler.Requests.Single(r =>
                r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/api/conversations"
            );
            var emittedEffort = JsonNode.Parse(provision.Body!)!["reasoningEffort"]?.GetValue<string>();
            emittedEffort.Should().Be(effort);
            var requestCount = handler.Requests.Count;
            var phaseCount = sandboxCalls;
            var mismatch = () =>
                ReviewRunner.RunAsync(
                    Options(effort is null ? "xhigh" : null, true),
                    config,
                    client,
                    Sandbox,
                    CancellationToken.None
                );
            await mismatch.Should().ThrowAsync<InvalidOperationException>().WithMessage("*effort differs*");
            // The former "effort cannot be verified by the binding API" refusal is gone: a re-review now
            // reuses the existing conversation (switching the model if needed) instead of refusing.
            handler.Requests.Should().HaveCount(requestCount);
            sandboxCalls.Should().Be(phaseCount);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }

    [Fact]
    public async Task Runner_prepares_before_sending_reuses_thread_and_preserves_success_on_publication_failure()
    {
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-run-{Guid.NewGuid():N}.db");
        var repo = PrCommands.ParseRepo("org/project/Nova");
        var key = $"reviewd/review-run/{repo.NormalizedKey}/17";
        var sends = 0;
        var provisions = 0;
        var polls = 0;
        var phases = new List<string>();
        var verifySummaryThreads = new List<string>();
        string? lostInput = null;
        var handler = new FakeHttpMessageHandler()
            .OnJson(
                HttpMethod.Get,
                "/capabilities",
                """{"messageIdempotency":true,"spawnSuppression":true,"rootReasoningEffort":true,"conversationWorkingDirectory":true}"""
            )
            .On(
                request =>
                    request.RequestUri!.AbsolutePath == "/api/conversations" && request.Method == HttpMethod.Post,
                _ =>
                {
                    provisions++;
                    return Json("""{"threadId":"thread"}""");
                }
            )
            .OnJson(
                HttpMethod.Get,
                "/api/conversations?",
                """[{"threadId":"thread","workspace":"workspace","provider":"test","mode":"workspace-agent","clientToolsDisabled":true}]"""
            )
            .On(
                request => request.RequestUri!.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal),
                _ =>
                {
                    phases.Should().Contain(p => p == "prepare" || p == "inspect-prepared");
                    sends++;
                    polls = 0;
                    return Json($$"""{"inputId":"input{{sends}}","idempotencyKeyHonored":true}""");
                }
            )
            .OnJson(HttpMethod.Put, "/metadata", "{}")
            .OnJson(HttpMethod.Post, "/unfreeze", """{"threadId":"thread","frozen":false}""")
            .On(
                request =>
                    lostInput is { } lost
                    && request.RequestUri!.Query.EndsWith("inputId=" + lost, StringComparison.Ordinal),
                _ => Json("""{"status":"NotStarted","runId":null}""")
            )
            .OnJson(HttpMethod.Get, "/status?", """{"status":"Completed","runId":"run","response":{"text":"done"}}""")
            .On(
                request => request.RequestUri!.AbsolutePath.EndsWith("/run-state", StringComparison.Ordinal),
                _ =>
                {
                    polls++;
                    return Json(
                        polls == 1
                            ? """{"isInProgress":true,"currentRunId":"next-run"}"""
                            : """{"isInProgress":false,"currentRunId":null}"""
                    );
                }
            )
            .OnJson(HttpMethod.Get, "/subagents?", """{"schemaVersion":1,"nodes":[]}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var client = new LmStreamingS2SClient(http, null, null, null);
        using var config = new ReviewdConfig(new ConfigurationBuilder().Build());
        var options = CliOptions.Parse([
            "review",
            "run",
            "--repo",
            repo.DisplayName,
            "--pr",
            "17",
            "--slot",
            "2",
            "--db",
            database,
            "--session",
            "session",
            "--workspace",
            "workspace",
            "--provider",
            "test",
            "--execute",
        ]);
        var failPublication = false;
        var refuseCommit = false;
        string? retryableCommit = null;
        try
        {
            using var connection = PrThreadBindings.Connection(database);
            var store = new SqliteKvStore(connection);
            await SnapshotAsync('a');
            await ReviewRunner.RunAsync(
                options,
                config,
                client,
                SandboxAsync,
                CancellationToken.None,
                _ => Task.CompletedTask
            );
            var first = (await store.GetAsync<JsonObject>(key))!;
            first["stage"]!.GetValue<string>().Should().Be("completed-verified");
            phases.Should().Equal("prepare", "inspect", "inspect", "verify", "commit", "cleanup");
            var metadata = handler.Requests.Single(r => r.Method == HttpMethod.Put);
            JsonNode.Parse(metadata.Body!)!["title"]!.GetValue<string>().Should().Be("Nova-17-aaaaaaaa");
            handler
                .Requests.Where(r => r.Uri.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal))
                .Should()
                .OnlyContain(r => r.Uri.AbsolutePath == "/api/conversations/thread/messages");
            handler
                .Requests.First(r => r.Uri.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal))
                .Body.Should()
                .Contain("setup-context.json")
                .And.Contain("Do not reset or rerun setup");
            await SnapshotAsync('d');
            var beforeReReview = handler.Requests.Count;
            first["reviewCompletedAt"] = DateTimeOffset.UtcNow.AddHours(-1);
            await store.SetAsync(key, first);
            phases.Clear();
            await ReviewRunner.RunAsync(
                options,
                config,
                client,
                SandboxAsync,
                CancellationToken.None,
                _ => Task.CompletedTask
            );
            provisions.Should().Be(1);
            sends.Should().Be(4);
            // A reclaim leaves the thread frozen; the re-review thaws it before its send, or the run parks.
            var reReview = handler.Requests.Skip(beforeReReview).ToList();
            var thaw = reReview.FindIndex(r =>
                r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/api/conversations/thread/unfreeze"
            );
            thaw.Should().BeGreaterThan(-1, "a re-review must thaw the conversation it reuses");
            var send = reReview.FindIndex(r => r.Uri.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal));
            reReview
                .FindIndex(thaw, r => r.Uri.AbsolutePath.EndsWith("/run-state", StringComparison.Ordinal))
                .Should()
                .BeInRange(thaw + 1, send - 1, "work queued behind the freeze settles before the review is sent");
            // The re-review's verify learns the first round's summary thread, where a reply may stand as its summary.
            verifySummaryThreads.Should().Equal("[]", "[7]");
            var last = (await store.GetAsync<JsonObject>(key))!;
            last["reviewCompletedAt"] = DateTimeOffset.UtcNow.AddHours(-1);
            await store.SetAsync(key, last);
            var baseline = last.DeepClone();
            await SnapshotAsync('e');
            failPublication = true;
            phases.Clear();
            var run = () =>
                ReviewRunner.RunAsync(
                    options,
                    config,
                    client,
                    SandboxAsync,
                    CancellationToken.None,
                    _ => Task.CompletedTask
                );
            await run.Should().ThrowAsync<InvalidOperationException>().WithMessage("publication unavailable");
            phases.Should().NotContain("commit");
            JsonNode.DeepEquals(await store.GetAsync<JsonObject>(key), baseline).Should().BeTrue();
            var count = sends;
            await run.Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("An incomplete review attempt exists*");
            sends.Should().Be(count);
            failPublication = false;
            var attemptKey = $"reviewd/review-attempt/{repo.NormalizedKey}/17";
            var verificationPending = (await store.GetAsync<JsonObject>(attemptKey))!;
            verificationPending["stage"]!.GetValue<string>().Should().Be("verify-pending");
            var verificationResume = CliOptions.Parse([
                "review",
                "run",
                "--repo",
                repo.DisplayName,
                "--pr",
                "17",
                "--slot",
                "2",
                "--db",
                database,
                "--session",
                "session",
                "--workspace",
                "workspace",
                "--provider",
                "test",
                "--resume",
                "--execute",
            ]);
            refuseCommit = true;
            var refusedCommit = () =>
                ReviewRunner.RunAsync(
                    verificationResume,
                    config,
                    client,
                    SandboxAsync,
                    CancellationToken.None,
                    _ => Task.CompletedTask
                );
            await refusedCommit.Should().ThrowAsync<SandboxException>();
            refuseCommit = false;
            (await store.GetAsync<JsonObject>(attemptKey))!["stage"]!
                .GetValue<string>()
                .Should()
                .Be("verify-pending", "a refused commit never ran, so verification resumes");
            foreach (
                var refusal in new[]
                {
                    "An existing Git operation or index lock must be resolved first.",
                    "Source changed during artifact capture or commit hook; side effects preserved for inspection.",
                }
            )
            {
                retryableCommit = refusal;
                await refusedCommit
                    .Should()
                    .ThrowAsync<InvalidOperationException>()
                    .WithMessage("*" + refusal[..20] + "*");
                retryableCommit = null;
                (await store.GetAsync<JsonObject>(attemptKey))!["stage"]!
                    .GetValue<string>()
                    .Should()
                    .Be("verify-pending", "other sandbox work stopped the commit, and a rerun is safe");
            }
            // The first verification found the confirmation lost (the host never ran it); a retry must not
            // wait on it again, or every retry stalls the lane for the whole lost-input bound.
            var lost = (await store.GetAsync<JsonObject>(attemptKey))!;
            lost["confirmationLost"] = true;
            await store.SetAsync(attemptKey, lost);
            lostInput = lost["confirmationInputId"]!.GetValue<string>();
            var requestsBeforeResume = handler.Requests.Count;
            phases.Clear();
            await ReviewRunner.RunAsync(
                verificationResume,
                config,
                client,
                SandboxAsync,
                CancellationToken.None,
                _ => Task.CompletedTask
            );
            sends.Should().Be(count, "verification recovery must not resend either retained input");
            provisions.Should().Be(1);
            handler
                .Requests.Skip(requestsBeforeResume)
                .Should()
                .OnlyContain(request => request.Method == HttpMethod.Get);
            handler
                .Requests.Skip(requestsBeforeResume)
                .Should()
                .NotContain(request => request.Uri.Query.EndsWith("inputId=" + lostInput, StringComparison.Ordinal));
            lostInput = null;
            phases.Should().Equal("inspect-prepared", "verify", "commit", "cleanup");
            var recovered = (await store.GetAsync<JsonObject>(attemptKey))!;
            recovered["stage"]!.GetValue<string>().Should().Be("completed-verified");
            foreach (var input in new[] { "reviewInputId", "confirmationInputId" })
                recovered[input]!.GetValue<string>().Should().Be(verificationPending[input]!.GetValue<string>());
            foreach (var refusal in new[] { "missing-confirmation", "commit-pending", "changed-head" })
            {
                var refused = verificationPending.DeepClone().AsObject();
                if (refusal == "missing-confirmation")
                {
                    refused.Remove("confirmationInputId");
                    refused.Remove("confirmationIdempotencyKey");
                }
                else if (refusal == "commit-pending")
                    refused["stage"] = "commit-pending";
                else
                    refused["headSha"] = new string('f', 40);
                await store.SetAsync(attemptKey, refused);
                phases.Clear();
                var requestsBeforeRefusal = handler.Requests.Count;
                var retry = () =>
                    ReviewRunner.RunAsync(
                        verificationResume,
                        config,
                        client,
                        SandboxAsync,
                        CancellationToken.None,
                        _ => Task.CompletedTask
                    );
                await retry.Should().ThrowAsync<InvalidOperationException>();
                phases.Should().BeEmpty();
                handler.Requests.Should().HaveCount(requestsBeforeRefusal);
                sends.Should().Be(count);
            }
            var interrupted = baseline.DeepClone().AsObject();
            using (var removeClaim = connection.CreateCommand())
            {
                removeClaim.CommandText = "DELETE FROM cache WHERE key=@key";
                removeClaim.Parameters.AddWithValue("@key", SlotRelease.OwnerKey("workspace", repo.NormalizedKey, 2));
                await removeClaim.ExecuteNonQueryAsync();
            }
            interrupted.Remove("claimToken");
            interrupted.Remove("cleanupState");
            interrupted["headSha"] = new string('e', 40);
            interrupted["stage"] = "review-running";
            foreach (var field in new[] { "confirmationInputId", "confirmationIdempotencyKey", "confirmationResponse" })
                interrupted.Remove(field);
            await store.SetAsync(attemptKey, interrupted);
            phases.Clear();
            var resume = CliOptions.Parse([
                "review",
                "run",
                "--repo",
                repo.DisplayName,
                "--pr",
                "17",
                "--slot",
                "2",
                "--db",
                database,
                "--session",
                "session",
                "--workspace",
                "workspace",
                "--provider",
                "test",
                "--resume",
                "--execute",
            ]);
            await ReviewRunner.RunAsync(
                resume,
                config,
                client,
                SandboxAsync,
                CancellationToken.None,
                _ => Task.CompletedTask
            );
            sends.Should().Be(count + 1, "resume sends only confirmation, never the admitted review");
            provisions.Should().Be(1);
            phases.Should().Equal("inspect-prepared", "inspect", "verify", "commit", "cleanup");
            (await store.GetAsync<JsonObject>(attemptKey))!["stage"]!
                .GetValue<string>()
                .Should()
                .Be("completed-verified");
            // A review input the host swallowed may still have been reviewed by the run that answered the parked
            // question (seen live), so it is confirmed and verify decides, rather than failing the review.
            using (var removeClaim = connection.CreateCommand())
            {
                removeClaim.CommandText = "DELETE FROM cache WHERE key=@key";
                removeClaim.Parameters.AddWithValue("@key", SlotRelease.OwnerKey("workspace", repo.NormalizedKey, 2));
                await removeClaim.ExecuteNonQueryAsync();
            }
            await store.SetAsync(attemptKey, interrupted);
            lostInput = interrupted["reviewInputId"]!.GetValue<string>();
            phases.Clear();
            var clock = DateTimeOffset.UtcNow;
            await ReviewRunner.RunAsync(
                resume,
                config,
                client,
                SandboxAsync,
                CancellationToken.None,
                _ =>
                {
                    clock = clock.AddMinutes(1);
                    return Task.CompletedTask;
                },
                () => clock
            );
            lostInput = null;
            sends.Should().Be(count + 2, "a lost review is confirmed, never resent");
            phases.Should().Equal("inspect-prepared", "inspect", "verify", "commit", "cleanup");
            var confirmed = (await store.GetAsync<JsonObject>(attemptKey))!;
            confirmed["stage"]!.GetValue<string>().Should().Be("completed-verified");
            confirmed["reviewLost"]!.GetValue<bool>().Should().BeTrue();

            Task SnapshotAsync(char head) =>
                store.SetAsync(
                    $"reviewd/pr-snapshot/{repo.NormalizedKey}",
                    new Dictionary<string, object>
                    {
                        ["17"] = new
                        {
                            Lifecycle = "Open",
                            IsDraft = false,
                            HeadSha = new string(head, 40),
                        },
                    }
                );
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
            File.Delete(database + ".review-lock");
        }

        Task<JsonObject> SandboxAsync(string phase, JsonObject args, string operation, CancellationToken ct)
        {
            phases.Add(phase);
            if (phase == "verify" && failPublication)
            {
                throw new InvalidOperationException("publication unavailable");
            }
            if (phase == "verify")
                verifySummaryThreads.Add(args["summaryThreads"]!.ToJsonString());
            if (phase == "commit")
            {
                polls.Should().BeGreaterThanOrEqualTo(3, "input completion alone must not allow commits");
                if (refuseCommit)
                    throw new SandboxException(SandboxErrorKind.Unavailable, "refused", 503)
                    {
                        ErrorCode = "operation_capacity_exhausted",
                    };
                if (retryableCommit is not null)
                    ReviewRunner.Settle("commit", 1, "", "review_common.ReviewError: " + retryableCommit, operation);
            }
            var json = phase switch
            {
                "prepare" =>
                    """{"mergeSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","contextPath":".worktrees/Nova-2/PRs/nova-17/setup-context.json","reviewDirectory":".worktrees/Nova-2/PRs/nova-17","baselineComments":[]}""",
                "commit" => """{"status":"committed","commit":"cccccccccccccccccccccccccccccccccccccccc"}""",
                "verify" => """{"comments":[{"threadId":7,"commentId":1,"kind":"summary"}]}""",
                "cleanup" => new JsonObject
                {
                    ["claimToken"] = args["claimToken"]!.DeepClone(),
                    ["cleanBaselineVerified"] = true,
                    ["evidencePreserved"] = true,
                }.ToJsonString(),
                _ => "{}",
            };
            return Task.FromResult(JsonNode.Parse(json)!.AsObject());
        }
    }

    // A conversation provisioned before DisableClientTools still has AskUserQuestion, and a question there
    // parks the review with nobody to answer it. A fresh review continues only a headless conversation;
    // anything else gets a new headless one, and the binding moves to it.
    [Theory]
    [InlineData(true, true, "99999999")]
    [InlineData(true, false, "99999999")]
    [InlineData(false, true, "99999999")]
    [InlineData(false, false, "99999999")]
    // The already-reviewed check still sees a conversation that is passed over for reuse.
    [InlineData(false, false, "aaaaaaaa")]
    public async Task Runner_continues_only_a_headless_conversation_and_provisions_a_headless_one_otherwise(
        bool bound,
        bool headless,
        string priorHead
    )
    {
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-headless-{Guid.NewGuid():N}.db");
        var repo = PrCommands.ParseRepo("org/project/Nova");
        var flag = headless ? ""","clientToolsDisabled":true""" : "";
        var handler = new FakeHttpMessageHandler()
            .OnJson(
                HttpMethod.Get,
                "/capabilities",
                """{"messageIdempotency":true,"spawnSuppression":true,"rootReasoningEffort":true,"conversationWorkingDirectory":true}"""
            )
            .On(
                request =>
                    request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/conversations",
                _ => Json("""{"threadId":"fresh"}""")
            )
            .OnJson(
                HttpMethod.Get,
                "/api/conversations?",
                $$"""[{"threadId":"prior","title":"Nova-17-{{priorHead}}","workspace":"workspace","mode":"workspace-agent","provider":"test","lastUpdated":1{{flag}}}]"""
            )
            .OnJson(
                HttpMethod.Get,
                "/prior/messages",
                Transcript(("User", ReviewPrompt(priorHead[0])), ("Assistant", "Done."))
            )
            .OnJson(HttpMethod.Put, "/metadata", "{}")
            .On(
                request => request.RequestUri!.AbsolutePath.EndsWith("/unfreeze", StringComparison.Ordinal),
                _ => new HttpResponseMessage(HttpStatusCode.Conflict)
                {
                    Content = new StringContent("""{"error":"not_freezable","threadId":"prior"}"""),
                }
            )
            .OnJson(HttpMethod.Post, "/messages", """{"inputId":"original","idempotencyKeyHonored":true}""")
            .OnJson(HttpMethod.Get, "/status?", """{"status":"Errored","runId":"run","response":null}""")
            .OnJson(HttpMethod.Get, "/run-state", """{"isInProgress":false,"currentRunId":null}""")
            .OnJson(HttpMethod.Get, "/subagents?", """{"schemaVersion":1,"nodes":[]}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var client = new LmStreamingS2SClient(http, null, null, null);
        using var config = new ReviewdConfig(new ConfigurationBuilder().Build());
        static Task<JsonObject> Sandbox(string phase, JsonObject args, string operation, CancellationToken token) =>
            Task.FromResult(
                phase switch
                {
                    "eligibility" => new JsonObject
                    {
                        ["prId"] = "17",
                        ["status"] = "active",
                        ["isDraft"] = false,
                        ["headSha"] = new string('a', 40),
                    },
                    "prepare" => new JsonObject
                    {
                        ["mergeSha"] = new string('b', 40),
                        ["contextPath"] = "context",
                        ["reviewDirectory"] = "reviews",
                        ["baselineComments"] = new JsonArray(),
                    },
                    _ => [],
                }
            );
        try
        {
            using var connection = PrThreadBindings.Connection(database);
            await new SqliteKvStore(connection).SetAsync(
                $"reviewd/pr-snapshot/{repo.NormalizedKey}",
                new JsonObject
                {
                    ["17"] = new JsonObject
                    {
                        ["Lifecycle"] = "Open",
                        ["IsDraft"] = false,
                        ["HeadSha"] = new string('a', 40),
                    },
                }
            );
            if (bound)
                await PrThreadBindings.SetAsync(database, repo, "17", "prior", CancellationToken.None);
            var options = CliOptions.Parse([
                "review",
                "run",
                "--repo",
                repo.DisplayName,
                "--pr",
                "17",
                "--slot",
                "2",
                "--db",
                database,
                "--session",
                "session",
                "--workspace",
                "workspace",
                "--provider",
                "test",
                "--execute",
            ]);
            var run = () =>
                ReviewRunner.RunAsync(
                    options,
                    config,
                    client,
                    Sandbox,
                    CancellationToken.None,
                    _ => Task.CompletedTask,
                    () => DateTimeOffset.UtcNow
                );
            var failure = (await run.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
            var provisions = handler
                .Requests.Where(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/api/conversations")
                .ToList();
            var sends = handler
                .Requests.Where(r =>
                    r.Method == HttpMethod.Post && r.Uri.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal)
                )
                .Select(r => r.Uri.AbsolutePath)
                .ToList();
            var binding = await PrThreadBindings.GetAsync(database, repo, "17", CancellationToken.None);
            if (priorHead == "aaaaaaaa")
            {
                failure.Should().Be("PR head was already reviewed by another run; skipped.");
                provisions.Should().BeEmpty();
                sends.Should().BeEmpty();
            }
            else if (headless)
            {
                failure.Should().Contain("Errored");
                provisions.Should().BeEmpty();
                sends.Should().Equal("/api/conversations/prior/messages");
                binding.Should().Be("prior");
            }
            else
            {
                failure.Should().Contain("Errored");
                JsonNode.Parse(provisions.Should().ContainSingle().Subject.Body!)!["disableClientTools"]!
                    .GetValue<bool>()
                    .Should()
                    .BeTrue();
                sends.Should().Equal("/api/conversations/fresh/messages");
                binding.Should().Be("fresh", "the next run must find the headless conversation, not the old one");
                handler
                    .Requests.Should()
                    .NotContain(r =>
                        r.Method != HttpMethod.Get
                        && r.Uri.AbsolutePath.StartsWith("/api/conversations/prior", StringComparison.Ordinal)
                    );
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }

    // A passed-over legacy thread is never waited on, so a sub-agent still running there could publish for the PR
    // beside a fresh review. Its whole tree defers the review (the batch retries it) until it settles, whether or
    // not an earlier head's review completed: the child may have started after that review's cleanup.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Runner_admits_no_fresh_review_while_a_passed_over_thread_still_has_a_live_child(
        bool earlierReceipt
    )
    {
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-legacy-child-{Guid.NewGuid():N}.db");
        var repo = PrCommands.ParseRepo("org/project/Nova");
        var child = "running";
        var handler = new FakeHttpMessageHandler()
            .OnJson(
                HttpMethod.Get,
                "/capabilities",
                """{"messageIdempotency":true,"spawnSuppression":true,"rootReasoningEffort":true,"conversationWorkingDirectory":true}"""
            )
            .On(
                request =>
                    request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/conversations",
                _ => Json("""{"threadId":"fresh"}""")
            )
            .OnJson(
                HttpMethod.Get,
                "/api/conversations?",
                """[{"threadId":"prior","title":"Nova-17-99999999","workspace":"workspace","mode":"workspace-agent","provider":"test","lastUpdated":1}]"""
            )
            .On(
                request => request.RequestUri!.AbsolutePath == "/api/conversations/prior/subagents",
                _ =>
                    child == "busy"
                        ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                        {
                            Content = new StringContent("""{"code":"hierarchy_busy"}"""),
                        }
                        : Json(
                            $$"""{"schemaVersion":1,"nodes":[{"agentId":"child","threadId":"child-thread","parentThreadId":"prior","depth":1,"template":"publisher","status":"{{child}}"}]}"""
                        )
            )
            .OnJson(HttpMethod.Put, "/metadata", "{}")
            .OnJson(HttpMethod.Post, "/messages", """{"inputId":"original","idempotencyKeyHonored":true}""")
            .OnJson(HttpMethod.Get, "/status?", """{"status":"Errored","runId":"run","response":null}""")
            .OnJson(HttpMethod.Get, "/run-state", """{"isInProgress":false,"currentRunId":null}""")
            .OnJson(HttpMethod.Get, "/subagents?", """{"schemaVersion":1,"nodes":[]}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var client = new LmStreamingS2SClient(http, null, null, null);
        using var config = new ReviewdConfig(new ConfigurationBuilder().Build());
        static Task<JsonObject> Sandbox(string phase, JsonObject args, string operation, CancellationToken token) =>
            Task.FromResult(
                phase switch
                {
                    "eligibility" => new JsonObject
                    {
                        ["prId"] = "17",
                        ["status"] = "active",
                        ["isDraft"] = false,
                        ["headSha"] = new string('a', 40),
                    },
                    "prepare" => new JsonObject
                    {
                        ["mergeSha"] = new string('b', 40),
                        ["contextPath"] = "context",
                        ["reviewDirectory"] = "reviews",
                        ["baselineComments"] = new JsonArray(),
                    },
                    _ => [],
                }
            );
        try
        {
            using var connection = PrThreadBindings.Connection(database);
            await new SqliteKvStore(connection).SetAsync(
                $"reviewd/pr-snapshot/{repo.NormalizedKey}",
                new JsonObject
                {
                    ["17"] = new JsonObject
                    {
                        ["Lifecycle"] = "Open",
                        ["IsDraft"] = false,
                        ["HeadSha"] = new string('a', 40),
                    },
                }
            );
            if (earlierReceipt)
                await new SqliteKvStore(connection).SetAsync(
                    $"reviewd/review-run/{repo.NormalizedKey}/17",
                    new JsonObject
                    {
                        ["stage"] = "completed-verified",
                        ["reviewedHeadSha"] = new string('9', 40),
                        ["reviewCompletedAt"] = DateTimeOffset.UtcNow.AddHours(-1),
                    }
                );
            var options = CliOptions.Parse([
                "review",
                "run",
                "--repo",
                repo.DisplayName,
                "--pr",
                "17",
                "--slot",
                "2",
                "--db",
                database,
                "--session",
                "session",
                "--workspace",
                "workspace",
                "--provider",
                "test",
                "--execute",
            ]);
            var run = () =>
                ReviewRunner.RunAsync(
                    options,
                    config,
                    client,
                    Sandbox,
                    CancellationToken.None,
                    _ => Task.CompletedTask,
                    () => DateTimeOffset.UtcNow
                );
            foreach (var live in new[] { "running", "busy" })
            {
                child = live;
                await run.Should()
                    .ThrowAsync<ReviewRunner.ReviewDeferredException>($"a {live} child tree has not settled");
                handler.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get, "nothing is claimed or sent");
            }

            child = "completed";
            (await run.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("Errored");
            JsonNode.Parse(
                handler
                    .Requests.Should()
                    .ContainSingle(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/api/conversations")
                    .Subject.Body!
            )!["disableClientTools"]!
                .GetValue<bool>()
                .Should()
                .BeTrue();
            handler
                .Requests.Where(r => r.Uri.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal))
                .Select(r => r.Uri.AbsolutePath)
                .Should()
                .Equal("/api/conversations/fresh/messages");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }

    [Fact]
    public async Task Find_conversation_by_title_prefix_prefers_a_headless_match_over_a_newer_one()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            "/api/conversations?",
            """
            [
              {"threadId":"headless","title":"Nova-17-aaaaaaaa","workspace":"ws","mode":"m","provider":"p","lastUpdated":100,"clientToolsDisabled":true},
              {"threadId":"asks","title":"Nova-17-bbbbbbbb","workspace":"ws","mode":"m","provider":"p","lastUpdated":200,"clientToolsDisabled":false},
              {"threadId":"legacy","title":"Nova-17-cccccccc","workspace":"ws","mode":"m","provider":"p","lastUpdated":300}
            ]
            """
        );
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var client = new LmStreamingS2SClient(http, null, null, null);

        var match = await client.FindConversationByTitlePrefixAsync("Nova-17-", "ws", "m", CancellationToken.None);

        match.Should().Be(new S2SConversationMatch("headless", "p", true));
        (await client.GetClientToolsDisabledAsync("headless", CancellationToken.None)).Should().BeTrue();
        (await client.GetClientToolsDisabledAsync("asks", CancellationToken.None)).Should().BeFalse();
        (await client.GetClientToolsDisabledAsync("legacy", CancellationToken.None)).Should().BeFalse();
        (await client.GetClientToolsDisabledAsync("missing", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Find_conversation_by_title_prefix_matches_repo_pr_boundary_and_picks_newest()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            "/api/conversations?",
            """
            [
              {"threadId":"old","title":"Nova-17-aaaaaaaa","workspace":"ws","mode":"m","provider":"p1","lastUpdated":100},
              {"threadId":"new","title":"Nova-17-bbbbbbbb","workspace":"ws","mode":"m","provider":"p2","lastUpdated":200},
              {"threadId":"other-pr","title":"Nova-170-cccccccc","workspace":"ws","mode":"m","provider":"p1","lastUpdated":300},
              {"threadId":"other-ws","title":"Nova-17-dddddddd","workspace":"ws2","mode":"m","provider":"p1","lastUpdated":400}
            ]
            """
        );
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var client = new LmStreamingS2SClient(http, null, null, null);

        var match = await client.FindConversationByTitlePrefixAsync("Nova-17-", "ws", "m", CancellationToken.None);
        match.Should().NotBeNull();
        // Newest lastUpdated among the "Nova-17-" rows in the matching workspace/mode; the trailing dash
        // keeps pr 17 from matching pr 170, and a different workspace is excluded.
        match!.ThreadId.Should().Be("new");
        match.Provider.Should().Be("p2");

        (await client.FindConversationByTitlePrefixAsync("Nova-99-", "ws", "m", CancellationToken.None))
            .Should()
            .BeNull();
    }

    [Fact]
    public async Task Ensure_reusable_switches_provider_when_it_differs_and_never_refuses()
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(
                HttpMethod.Get,
                "/api/conversations?",
                """[{"threadId":"t","title":"Nova-17-aaaaaaaa","workspace":"ws","mode":"m","provider":"old-model","lastUpdated":1}]"""
            )
            .OnJson(HttpMethod.Post, "/provider", "{}");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var client = new LmStreamingS2SClient(http, null, null, null);

        await client.EnsureConversationReusableAsync("t", "ws", "new-model", "m", CancellationToken.None);

        var switchReq = handler
            .Requests.Should()
            .ContainSingle(r =>
                r.Method == HttpMethod.Post && r.Uri.AbsolutePath.EndsWith("/provider", StringComparison.Ordinal)
            )
            .Subject;
        JsonNode.Parse(switchReq.Body!)!["providerId"]!.GetValue<string>().Should().Be("new-model");
    }

    [Fact]
    public async Task Ensure_reusable_does_not_switch_when_provider_already_matches()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            "/api/conversations?",
            """[{"threadId":"t","title":"Nova-17-aaaaaaaa","workspace":"ws","mode":"m","provider":"same","lastUpdated":1}]"""
        );
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var client = new LmStreamingS2SClient(http, null, null, null);

        await client.EnsureConversationReusableAsync("t", "ws", "same", "m", CancellationToken.None);

        handler.Requests.Should().NotContain(r => r.Uri.AbsolutePath.EndsWith("/provider", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ensure_reusable_throws_on_workspace_or_mode_mismatch()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            "/api/conversations?",
            """[{"threadId":"t","title":"Nova-17-aaaaaaaa","workspace":"ws","mode":"m","provider":"p","lastUpdated":1}]"""
        );
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var client = new LmStreamingS2SClient(http, null, null, null);

        var act = () => client.EnsureConversationReusableAsync("t", "other-ws", "p", "m", CancellationToken.None);
        await act.Should().ThrowAsync<ReviewHostContractException>().WithMessage("*workspace/mode differs*");
    }

    private static HttpResponseMessage Json(string value) =>
        new(HttpStatusCode.OK) { Content = new StringContent(value) };
}
