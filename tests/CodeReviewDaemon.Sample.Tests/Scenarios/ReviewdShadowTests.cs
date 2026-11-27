extern alias Reviewd;

using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using AchieveAi.LmDotnetTools.Misc.Storage;
using AchieveAi.LmDotnetTools.Sandbox;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Reviewd::Reviewd.Sample;
using Xunit.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
///     <c>review run --shadow</c> is an A/B experiment arm: the same review on a fresh read-only-egress conversation,
///     recorded in would_post.json instead of posted, and titled apart so the live runner never reuses it.
/// </summary>
public sealed class ReviewdShadowTests(ITestOutputHelper output) : LoggingTestBase(output)
{
    private static readonly RepoIdentity Repo = PrCommands.ParseRepo("org/project/Nova");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-07T22:00:00Z");
    private static readonly string Head = new('a', 40);
    private readonly string _database = Path.Combine(Path.GetTempPath(), $"reviewd-shadow-{Guid.NewGuid():N}.db");

    // Not "shadow": judge-inputs echoes its store path, and the blind-judge test bans the word from every input.
    private readonly string _files = Path.Combine(Path.GetTempPath(), $"reviewd-variants-{Guid.NewGuid():N}");

    /// <summary>The flags of variant arm <paramref name="id" />, with a manifest that pins only its id.</summary>
    private string[] Variant(string id, string manifest = "")
    {
        Directory.CreateDirectory(_files);
        var path = Path.Combine(_files, id + ".json");
        File.WriteAllText(path, manifest.Length > 0 ? manifest : $$"""{"id":"{{id}}"}""");
        StudyFixture.Write(_files, ("exp-1", "experiment", null));
        return ["--variant", id, "--manifest", path, "--experiment", "exp-1", "--store", _files];
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_shadow_run_provisions_read_only_titles_apart_and_sends_the_shadow_prompt(bool shadow)
    {
        var handler = Host();
        var phases = new List<(string Phase, JsonObject Args)>();
        await SnapshotAsync();

        // The scripted host reports the review Errored after the send; this test only checks how it got there.
        var run = () => RunAsync(handler, phases, shadow ? ["--shadow", .. Variant("kb-h1")] : []);
        await run.Should().ThrowAsync<InvalidOperationException>();

        var provision = handler.Requests.Single(r =>
            r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/api/conversations"
        );
        var title = handler.Requests.Single(r => r.Uri.AbsolutePath.EndsWith("/metadata", StringComparison.Ordinal));
        var send = handler.Requests.First(r => r.Uri.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal));
        var attempt = await AttemptAsync();
        // A shadow review is jailed to its own PR folder like a live one.
        provision.Body.Should().Contain("\"dedicatedSandbox\":true");
        if (shadow)
        {
            provision.Body.Should().Contain("\"readOnlyEgress\":true");
            title.Body.Should().Contain("shadow-Nova-17-aaaaaaaa");
            send.Body.Should().Contain("SHADOW review").And.Contain("would_post.json");
            phases.Should().OnlyContain(p => p.Args["shadow"]!.GetValue<bool>());
            attempt["shadow"]!.GetValue<bool>().Should().BeTrue();
            attempt["variant"]!.GetValue<string>().Should().Be("kb-h1");
        }
        else
        {
            provision.Body.Should().NotContain("\"readOnlyEgress\":true");
            title.Body.Should().Contain("\"Nova-17-aaaaaaaa\"");
            send.Body.Should().NotContain("SHADOW");
            phases.Should().OnlyContain(p => p.Args["shadow"] == null);
            attempt.ContainsKey("shadow").Should().BeFalse();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_finished_shadow_run_records_what_it_would_post_and_never_a_publication(bool shadow)
    {
        // Readers of verifiedPublication count it as comments on the PR, so a shadow run must record its verify
        // result under wouldPost alone, and its commit step must be told not to push.
        var handler = Host(status: """{"status":"Completed","runId":"run","response":{"text":"verdict: APPROVE"}}""");
        var phases = new List<(string Phase, JsonObject Args)>();
        await SnapshotAsync();

        (await RunAsync(handler, phases, shadow ? ["--shadow"] : [])).Should().Be(0);

        var receipt = await ReceiptAsync($"reviewd/review-run/{Repo.NormalizedKey}/17");
        receipt["stage"]!.GetValue<string>().Should().Be("completed-verified");
        receipt.ContainsKey("wouldPost").Should().Be(shadow);
        receipt.ContainsKey("verifiedPublication").Should().Be(!shadow);
        receipt[shadow ? "wouldPost" : "verifiedPublication"]!["file"]!.GetValue<string>().Should().Be("checked");
        phases.Single(p => p.Phase == "commit").Args.ContainsKey("shadow").Should().Be(shadow);
    }

    [Fact]
    public async Task A_shadow_run_reviews_a_head_another_run_already_reviewed()
    {
        // The live reviewer's conversation for this head would refuse a live re-run as a duplicate post. A shadow run
        // posts nothing, so it neither reuses that conversation nor stops at it.
        var live = new JsonArray(
            new JsonObject
            {
                ["messageType"] = "text",
                ["role"] = "User",
                ["timestamp"] = 1,
                ["messageJson"] = new JsonObject
                {
                    ["text"] =
                        $"Review org/project/Nova PR #17 in slot Nova-2.\nExpected source head {Head}; prepared merge {new string('b', 40)}.",
                }.ToJsonString(),
            },
            new JsonObject
            {
                ["messageType"] = "text",
                ["role"] = "Assistant",
                ["timestamp"] = 2,
                ["messageJson"] = new JsonObject { ["text"] = "Done." }.ToJsonString(),
            }
        ).ToJsonString();
        var handler = Host(
            """[{"threadId":"live","title":"Nova-17-aaaaaaaa","workspace":"workspace","mode":"workspace-agent","provider":"test","lastUpdated":1,"clientToolsDisabled":true}]""",
            live
        );
        await SnapshotAsync();

        var run = () => RunAsync(handler, [], ["--shadow"]);

        (await run.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should()
            .NotBe("PR head was already reviewed by another run; skipped.");
        handler
            .Requests.Should()
            .Contain(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/api/conversations");
        handler.Requests.Should().NotContain(r => r.Uri.AbsolutePath.Contains("/live/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task A_resume_must_match_the_attempts_shadow_flag(bool shadowRun, bool shadowAttempt)
    {
        await SnapshotAsync();
        var attempt = new JsonObject
        {
            ["repository"] = Repo.DisplayName,
            ["prId"] = "17",
            ["slot"] = 2,
            ["stage"] = "review-running",
            ["threadId"] = "thread",
        };
        if (shadowAttempt)
        {
            attempt["shadow"] = true;
        }
        await PutAsync($"reviewd/review-attempt/{Repo.NormalizedKey}/17", attempt);
        var handler = new FakeHttpMessageHandler();

        var run = () => RunAsync(handler, [], shadowRun ? ["--shadow", "--resume"] : ["--resume"]);

        // A shadow run meets the live attempt at the database check first, so it never gets as far as the resume.
        (await run.Should().ThrowAsync<ArgumentException>()).WithMessage(
            shadowRun ? "This --db holds live reviews*" : "A shadow review attempt can only be resumed with --shadow*"
        );
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true, "another-read-only", true)]
    [InlineData(true, "read-only", false)]
    [InlineData(false, "another-read-only", false)]
    public async Task A_lost_shadow_commit_resumes_only_through_the_post_session_it_was_sent_in(
        bool lostCommit,
        string postSession,
        bool refused
    )
    {
        // The gateway knows a lost commit's operation id only in the session it was sent in. Resumed through another
        // post session, the resubmit runs as a second commit, times out on the first one's slot lock, and the
        // attempt stays at commit-pending. Without a lost commit, any marked post session may resume.
        await SnapshotAsync();
        var attempt = new JsonObject
        {
            ["repository"] = Repo.DisplayName,
            ["prId"] = "17",
            ["slot"] = 2,
            ["stage"] = "verify-pending",
            ["confirmationInputId"] = "confirmation",
            ["threadId"] = "thread",
            ["shadow"] = true,
            ["sessionId"] = "session",
            ["postSessionId"] = "read-only",
        };
        if (lostCommit)
        {
            attempt[ReviewRunner.CommitReplay] = new string('d', 32);
        }
        await PutAsync($"reviewd/review-attempt/{Repo.NormalizedKey}/17", attempt);
        var before = (await AttemptAsync()).ToJsonString();
        var handler = new FakeHttpMessageHandler();
        var phases = new List<(string Phase, JsonObject Args)>();

        var run = () => RunAsync(handler, phases, ["--shadow", "--resume"], postSession);

        var thrown = (await run.Should().ThrowAsync<Exception>()).Which;
        Logger.LogDebug(
            "Resume through {PostSession} with lost commit {LostCommit} threw {Type}: {Message}",
            postSession,
            lostCommit,
            thrown.GetType().Name,
            thrown.Message
        );
        if (refused)
        {
            thrown
                .Should()
                .BeOfType<InvalidOperationException>()
                .Which.Message.Should()
                .Be(
                    "This attempt's lost commit can only be replayed through --post-session read-only, the session it was sent in."
                );
            handler.Requests.Should().BeEmpty();
            phases.Should().BeEmpty();
            (await AttemptAsync()).ToJsonString().Should().Be(before);
        }
        else
        {
            // Past this check, the resume stops at its identity check: this attempt names no review input.
            thrown
                .Message.Should()
                .Be("Resume identity, admission or current PR does not match the retained attempt.");
        }
    }

    [Fact]
    public async Task A_commit_lost_after_a_resume_moved_to_a_new_post_session_is_replayed_through_that_session()
    {
        // A resume with no lost commit may move to another post session. A commit it then loses is known to the
        // gateway only in that session, so the attempt must name it; else every later restart through it is refused.
        const string completed = """{"status":"Completed","runId":"run","response":{"text":"verdict: APPROVE"}}""";
        var handler = Host(status: completed);
        // A resume finds the conversation the first run provisioned in the host's list.
        var resumed = Host(
            """[{"threadId":"thread","title":"shadow-Nova-17-aaaaaaaa","workspace":"workspace","mode":"workspace-agent","provider":"test","lastUpdated":1,"clientToolsDisabled":true}]""",
            status: completed
        );
        var phases = new List<(string Phase, JsonObject Args)>();
        var commits = new List<string>();
        Exception? commitFailure = new SandboxException(SandboxErrorKind.Unavailable, "busy", 503)
        {
            ErrorCode = "operation_capacity_exhausted",
        };
        void Commit(string phase, string operation)
        {
            if (phase != "commit")
            {
                return;
            }
            commits.Add(operation);
            if (commitFailure is { } failure)
            {
                throw failure;
            }
        }
        await SnapshotAsync();

        // Refused at capacity: nothing was lost, so the attempt waits at verify-pending with no replay.
        var first = () => RunAsync(handler, phases, ["--shadow"], "read-only", Commit);
        await first.Should().ThrowAsync<SandboxException>();
        (await AttemptAsync()).ContainsKey(ReviewRunner.CommitReplay).Should().BeFalse();

        // The resume moves to another post session, and that session's commit response is lost.
        commitFailure = new SandboxException(SandboxErrorKind.TransportTimeout, "transport timeout (00:00:30).");
        var moved = () => RunAsync(resumed, phases, ["--shadow", "--resume"], "another-read-only", Commit);
        await moved.Should().ThrowAsync<SandboxException>();
        var lost = await AttemptAsync();
        Logger.LogDebug(
            "After the lost commit the attempt names post session {PostSession} and replay {Replay}",
            lost["postSessionId"]?.ToJsonString(),
            lost[ReviewRunner.CommitReplay]?.ToJsonString()
        );
        lost["postSessionId"]!.GetValue<string>().Should().Be("another-read-only");
        lost[ReviewRunner.CommitReplay]!.GetValue<string>().Should().Be(commits[^1]);

        // The first session no longer knows the commit; the session it was sent in replays it under its id.
        var back = () => RunAsync(resumed, phases, ["--shadow", "--resume"], "read-only", Commit);
        (await back.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should()
            .Be(
                "This attempt's lost commit can only be replayed through --post-session another-read-only, the session it was sent in."
            );
        commitFailure = null;
        (await RunAsync(resumed, phases, ["--shadow", "--resume"], "another-read-only", Commit)).Should().Be(0);
        commits.Should().HaveCount(3);
        commits[2].Should().Be(commits[1], "the lost commit is resubmitted under its own operation id");
        (await AttemptAsync()).ContainsKey(ReviewRunner.CommitReplay).Should().BeFalse();
    }

    [Theory]
    [InlineData(null, "--shadow", "This --db holds live reviews*")]
    [InlineData("--shadow", null, "This --db belongs to a shadow arm; the live reviewer needs*")]
    [InlineData("--variant kb-h1", "--variant kb-h2", "*shadow arm 'kb-h1'; shadow arm 'kb-h2' needs*")]
    [InlineData("--variant kb-h1", "", "*shadow arm 'kb-h1'; a shadow arm needs*")]
    public async Task A_database_belongs_to_one_arm(string? first, string? second, string refusal)
    {
        // The first run pins the database to its arm; a run from any other arm is refused before it touches the host.
        // null is a database from before the pin: it holds a live attempt and no marker.
        string[] Arm(string? arm) =>
            arm switch
            {
                null => [],
                "" or "--shadow" => ["--shadow"],
                _ => ["--shadow", .. Variant(arm.Split(' ')[1])],
            };
        await SnapshotAsync();
        if (first is null)
        {
            await PutAsync(
                $"reviewd/review-attempt/{Repo.NormalizedKey}/17",
                new JsonObject { ["repository"] = Repo.DisplayName, ["stage"] = "review-running" }
            );
        }
        else
        {
            // The scripted host reports the review Errored after the send; by then the first run has pinned the db.
            var pin = () => RunAsync(Host(), [], Arm(first));
            await pin.Should().ThrowAsync<InvalidOperationException>();
        }
        var handler = new FakeHttpMessageHandler();

        var run = () => RunAsync(handler, [], Arm(second));

        (await run.Should().ThrowAsync<ArgumentException>()).WithMessage(refusal);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_dry_shadow_run_pins_nothing_and_the_same_arm_runs_again()
    {
        await SnapshotAsync();

        await ReviewArm.CheckAsync(_database, shadow: true, "kb-h1", pin: false, CancellationToken.None);
        await ReviewArm.CheckAsync(_database, shadow: false, null, pin: false, CancellationToken.None);
        await ReviewArm.CheckAsync(_database, shadow: true, "kb-h1", pin: true, CancellationToken.None);
        await ReviewArm.CheckAsync(_database, shadow: true, "kb-h1", pin: true, CancellationToken.None);

        var other = () => ReviewArm.CheckAsync(_database, shadow: false, null, pin: false, CancellationToken.None);
        await other.Should().ThrowAsync<ArgumentException>().WithMessage("This --db belongs to shadow arm 'kb-h1'*");
    }

    [Theory]
    [InlineData(false, true, "This --db belongs to the live reviewer; shadow arm 'kb-h1' needs*")]
    [InlineData(true, false, "This --db belongs to shadow arm 'kb-h1'; the live reviewer needs*")]
    public async Task Whichever_executed_run_starts_first_reserves_the_database(
        bool firstShadow,
        bool secondShadow,
        string refusal
    )
    {
        // A live and a shadow run that start together: the first insert wins, and the other run is refused before it
        // writes anything, instead of the live run later finding its own database pinned to shadow.
        await SnapshotAsync();
        await ReviewArm.CheckAsync(_database, firstShadow, firstShadow ? "kb-h1" : null, pin: true, default);

        var second = () =>
            ReviewArm.CheckAsync(_database, secondShadow, secondShadow ? "kb-h1" : null, pin: true, default);

        await second.Should().ThrowAsync<ArgumentException>().WithMessage(refusal);
        await ReviewArm.CheckAsync(_database, firstShadow, firstShadow ? "kb-h1" : null, pin: true, default);
    }

    [Fact]
    public async Task A_live_run_whose_pin_loses_the_race_after_an_empty_read_is_refused_and_the_shadow_pin_stays()
    {
        // The live run reads no pin, then a shadow run pins first: the trigger plays that shadow run, writing its pin
        // just before the live insert, which must then do nothing and read the shadow pin back.
        await SnapshotAsync();
        using (var connection = PrThreadBindings.Connection(_database))
        {
            await connection.OpenAsync();
            using var trigger = connection.CreateCommand();
            trigger.CommandText = $$"""
                CREATE TRIGGER rival_shadow_pin BEFORE INSERT ON cache
                WHEN NEW.key = '{{ReviewArm.Key}}' AND NEW.value LIKE '%"shadow":false%'
                BEGIN
                  INSERT INTO cache(key, value) VALUES ('{{ReviewArm.Key}}', '{"shadow":true,"variant":"kb-h1"}');
                END
                """;
            _ = await trigger.ExecuteNonQueryAsync();
        }

        var live = () => ReviewArm.CheckAsync(_database, shadow: false, null, pin: true, default);

        var refusal = await live.Should()
            .ThrowAsync<ArgumentException>()
            .WithMessage("This --db belongs to shadow arm 'kb-h1'; the live reviewer needs a --db of its own.");
        var pin = await ReceiptAsync(ReviewArm.Key);
        Logger.LogDebug("Live pin refused with {Refusal}; pin now {Pin}", refusal.Which.Message, pin.ToJsonString());
        pin.ToJsonString().Should().Be("""{"shadow":true,"variant":"kb-h1"}""");
    }

    [Theory]
    [InlineData("reviewd/batch-queue/workspace", true)]
    [InlineData("reviewd/batch-state/digest", true)]
    [InlineData("reviewd/batch-queue/workspace", false)]
    public async Task A_shadow_run_is_refused_on_a_database_holding_a_live_batch_queue(string key, bool live)
    {
        // A live queue with no attempt yet still belongs to the live reviewer: a shadow restart pinning it would
        // leave the live batch refused on its own database.
        await SnapshotAsync();
        var scope = new JsonObject { ["workspace"] = "workspace" };
        if (!live)
        {
            scope["shadow"] = true;
        }
        await PutAsync(key, new JsonObject { ["schemaVersion"] = 2, ["scope"] = scope });

        var shadow = () => ReviewArm.CheckAsync(_database, shadow: true, "kb-h1", pin: true, default);

        if (!live)
        {
            await shadow();
            return;
        }
        await shadow.Should().ThrowAsync<ArgumentException>().WithMessage("This --db holds live reviews*");
        await ReviewArm.CheckAsync(_database, shadow: false, null, pin: true, default);
    }

    [Fact]
    public async Task Only_a_shadow_run_starts_a_new_database()
    {
        // A mistyped live --db must fail rather than run against a new empty database.
        var live = () => ReviewArm.CheckAsync(_database, shadow: false, null, pin: true, default);

        await live.Should().ThrowAsync<Microsoft.Data.Sqlite.SqliteException>();
        File.Exists(_database).Should().BeFalse();
        await ReviewArm.CheckAsync(_database, shadow: true, "kb-h1", pin: true, default);
        File.Exists(_database).Should().BeTrue();
    }

    [Fact]
    public async Task A_fresh_shadow_run_never_continues_the_live_thread_bound_to_the_pr()
    {
        // The binding alone (no live receipt or attempt) does not make the database live, so the shadow run goes
        // ahead, but in a new read-only conversation rather than the bound one, which can post.
        await SnapshotAsync();
        await PrThreadBindings.SetAsync(_database, Repo, "17", "live", default);
        var handler = Host();

        var run = () => RunAsync(handler, [], ["--shadow"]);

        await run.Should().ThrowAsync<InvalidOperationException>();
        handler
            .Requests.Should()
            .Contain(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/api/conversations");
        handler.Requests.Should().NotContain(r => r.Uri.AbsolutePath.Contains("/live/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Batch_shadow_arm_is_forwarded_to_fresh_and_resumed_runs_and_a_restart_cannot_change_it()
    {
        using var batch = new ReviewdRunnerTests.BatchFixture(0, 1)
        {
            Arm = ["--shadow", "--post-session", "read-only", .. Variant("kb-h1")],
        };
        batch.Cancellation.Cancel();
        await batch.ExecuteAsync();
        batch.Restart();
        // The batch stopped while PR 3 ran on slot 1, so the restart resumes it.
        var state = batch.Read(batch.StateKey)!.AsObject();
        state["entries"]![2]!["status"] = "launching";
        state["entries"]![2]!["slot"] = 1;
        batch.Put(batch.StateKey, state);
        batch.PutAttempt(
            "3",
            new JsonObject
            {
                ["slot"] = 1,
                ["stage"] = "review-running",
                ["claimToken"] = "running",
                ["reviewInputId"] = "original",
                ["mergeSha"] = new string('b', 40),
                ["shadow"] = true,
                ["variant"] = "kb-h1",
            }
        );

        await batch.ExecuteAsync();

        batch.Runs.Should().Contain("3:resume").And.Contain("1:fresh");
        batch.RunArms.Should().HaveCount(3).And.OnlyContain(arm => arm == "shadow:kb-h1:read-only");
        var scope = batch.Read(batch.StateKey)!["scope"]!.AsObject();
        scope["shadow"]!.GetValue<bool>().Should().BeTrue();
        scope["variant"]!.GetValue<string>().Should().Be("kb-h1");
        scope["postSession"]!.GetValue<string>().Should().Be("read-only");
        // A restart that drops --shadow, or names another variant, would run this arm's queue as another arm.
        foreach (
            var arm in new[]
            {
                Array.Empty<string>(),
                ["--shadow", "--post-session", "read-only", .. Variant("kb-h2")],
                ["--shadow", "--post-session", "read-only"],
            }
        )
        {
            batch.Restart();
            batch.Arm = arm;
            var changed = () => batch.ExecuteAsync();
            await changed
                .Should()
                .ThrowAsync<ArgumentException>()
                .WithMessage("This --db belongs to shadow arm 'kb-h1'*");
        }
        batch.Runs.Should().HaveCount(3);
    }

    [Theory]
    [InlineData("--shadow", "*--shadow needs --post-session*")]
    [InlineData("--post-session read-only", "*--post-session needs --shadow.")]
    [InlineData("--shadow --post-session session", "--post-session must be a read-only session other than --session.")]
    public void A_shadow_run_names_a_read_only_post_session_and_only_a_shadow_run_does(string flags, string refusal)
    {
        foreach (var command in new[] { RunArguments, ["review", "batch", "--session", "session"] })
        {
            var parse = () => CliOptions.Parse([.. command, .. flags.Split(' ')]);

            parse.Should().Throw<ArgumentException>().WithMessage(refusal);
        }
        CliOptions
            .Parse([
                "sandbox",
                "create",
                "--workspace",
                "w",
                "--callback",
                "http://h",
                "--read-only-egress",
                "--read-repos",
                "org/project/Nova",
            ])
            .Has("read-only-egress")
            .Should()
            .BeTrue();
    }

    [Theory]
    [InlineData("--read-only-egress")]
    [InlineData("--read-repos org/project/Nova")]
    public void Read_only_egress_and_read_repos_are_each_refused_without_the_other(string flags)
    {
        var parse = () =>
            CliOptions.Parse(["sandbox", "create", "--workspace", "w", "--callback", "http://h", .. flags.Split(' ')]);

        parse.Should().Throw<ArgumentException>().WithMessage("--read-only-egress needs --read-repos*");
    }

    [Fact]
    public async Task Every_step_prepare_included_runs_in_the_read_only_session_when_there_is_one()
    {
        // The shadow agent can edit the workspace these steps run in, so no step may run with full egress; the
        // read-only session's egress allows prepare's fetch and read queries.
        var calls = new List<string>();
        var route = ReadOnlySessions.Route(
            (target, phase, args, _, _) =>
            {
                calls.Add($"{phase}@{target}");
                return Task.FromResult(new JsonObject());
            },
            "full",
            "read-only"
        );
        foreach (var phase in new[] { "eligibility", "prepare", "inspect", "verify", "commit", "cleanup" })
        {
            _ = await route(phase, [], phase + "-op", CancellationToken.None);
        }
        _ = await route("step-stop", new JsonObject { ["operation"] = "prepare-op" }, "s1", CancellationToken.None);
        _ = await route("step-activity", new JsonObject { ["operation"] = "commit-op" }, "s2", CancellationToken.None);
        _ = await route("step-stop", new JsonObject { ["operation"] = "unknown" }, "s3", CancellationToken.None);
        var live = ReadOnlySessions.Route(
            (target, phase, _, _, _) =>
            {
                calls.Add($"live {phase}@{target}");
                return Task.FromResult(new JsonObject());
            },
            "full",
            null
        );
        _ = await live("commit", [], "op", CancellationToken.None);

        calls
            .Should()
            .Equal(
                "eligibility@read-only",
                "prepare@read-only",
                "inspect@read-only",
                "verify@read-only",
                "commit@read-only",
                "cleanup@read-only",
                "step-stop@read-only",
                "step-activity@read-only",
                "step-stop@read-only",
                "live commit@full"
            );
    }

    [Fact]
    public async Task Only_a_session_marked_read_only_on_this_host_is_accepted_as_a_post_session()
    {
        var store = Path.Combine(Path.GetTempPath(), $"reviewd-tokens-{Guid.NewGuid():N}");
        Directory.CreateDirectory(store);
        try
        {
            var unmarked = () => ReadOnlySessions.Require(store, "session-1");
            var traversal = () => ReadOnlySessions.Require(store, "../session-1");

            unmarked.Should().Throw<ArgumentException>().WithMessage("--post-session session-1 was not created*");
            traversal.Should().Throw<ArgumentException>().WithMessage("*is not a sandbox session id.");
            await ReadOnlySessions.MarkAsync(
                store,
                "session-1",
                "workspace",
                [Repo.DisplayName],
                CancellationToken.None
            );
            unmarked.Should().NotThrow();
            ReadOnlySessions.Require(store, "session-1", Repo.DisplayName);
            var otherRepo = () => ReadOnlySessions.Require(store, "session-1", "org/project/Other");
            otherRepo
                .Should()
                .Throw<ArgumentException>()
                .WithMessage("--post-session session-1 cannot fetch org/project/Other*--read-repos org/project/Other.");
            // ADO names are case-insensitive: the marker's org/project/Nova covers ORG/Project/nova.
            ReadOnlySessions.Require(store, "session-1", "ORG/Project/nova");

            // A marker written before markers listed their repos grants no fetch, so its session is refused.
            await File.WriteAllTextAsync(
                Path.Combine(store, "read-only-sessions", "legacy.json"),
                """{"sessionId":"legacy","workspace":"workspace","readOnlyGitHub":true}"""
            );
            var legacy = () => ReadOnlySessions.Require(store, "legacy", Repo.DisplayName);
            legacy
                .Should()
                .Throw<ArgumentException>()
                .WithMessage("--post-session legacy cannot fetch org/project/Nova*");
        }
        finally
        {
            Directory.Delete(store, recursive: true);
        }
    }

    [Theory]
    [InlineData("""{"sessionId":"s","readOnlyEgress":true,"readRepos":["org/project/Nova"]}""", false)]
    [InlineData(
        """{"sessionId":"s","readOnlyEgress":true,"readOnlyGitHub":false,"readRepos":["org/project/Nova"]}""",
        false
    )]
    [InlineData(
        """{"sessionId":"s","readOnlyEgress":true,"readOnlyGitHub":"true","readRepos":["org/project/Nova"]}""",
        false
    )]
    [InlineData(
        """{"sessionId":"s","readOnlyEgress":true,"readOnlyGitHub":true,"readRepos":["org/project/Nova"]}""",
        true
    )]
    public async Task A_post_session_an_earlier_build_marked_is_refused_because_it_keeps_github_writes(
        string marker,
        bool accepted
    )
    {
        // An earlier build narrowed only ADO, so its marked session can still push to GitHub. Only a marker that
        // says GitHub was narrowed too is accepted, by a shadow run, a resume and a reap or reclaim alike.
        var store = Path.Combine(Path.GetTempPath(), $"reviewd-tokens-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(store, "read-only-sessions"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(store, "read-only-sessions", "s.json"), marker);

            var require = () => ReadOnlySessions.Require(store, "s", Repo.DisplayName);

            Logger.LogDebug("Marker {Marker} reads as marked {Marked}", marker, ReadOnlySessions.IsMarked(store, "s"));
            ReadOnlySessions.IsMarked(store, "s").Should().Be(accepted);
            if (accepted)
            {
                require.Should().NotThrow();
            }
            else
            {
                require
                    .Should()
                    .Throw<ArgumentException>()
                    .WithMessage(
                        "--post-session s was created by an earlier build whose read-only egress kept GitHub*"
                    );
            }
        }
        finally
        {
            Directory.Delete(store, recursive: true);
        }
    }

    [Theory]
    [InlineData("marked", null)]
    [InlineData("unmarked", "--post-session post was not created*")]
    [InlineData("other-repo", "--post-session post cannot fetch org/project/Nova*")]
    [InlineData("elsewhere", "--post-session does not identify the selected host workspace.")]
    [InlineData("none", null)]
    public async Task The_runner_entry_sends_every_step_to_a_marked_post_session_of_its_workspace_and_repo(
        string post,
        string? refusal
    )
    {
        // The entry review run and the batch's eligibility check build their steps here, so this is where a shadow
        // run's read-only session is checked and wired in.
        var store = Path.Combine(Path.GetTempPath(), $"reviewd-tokens-{Guid.NewGuid():N}");
        Directory.CreateDirectory(store);
        try
        {
            if (post != "unmarked")
            {
                string[] repos = post == "other-repo" ? ["org/project/Other"] : [Repo.DisplayName];
                await ReadOnlySessions.MarkAsync(store, "post", "workspace", repos, CancellationToken.None);
            }
            var options = CliOptions.Parse([
                .. RunArguments,
                .. post == "none" ? Array.Empty<string>() : ["--shadow", "--post-session", "post"],
            ]);
            var targets = new List<(string Session, string Phase)>();

            var build = () =>
                ReviewRunner.StepExecutorAsync(
                    options,
                    () => store,
                    "workspace",
                    (id, _) => Task.FromResult<string?>(id == "post" && post == "elsewhere" ? "other" : "workspace"),
                    (session, phase, _, _, _) =>
                    {
                        targets.Add((session, phase));
                        return Task.FromResult(new JsonObject());
                    },
                    CancellationToken.None
                );

            if (refusal is not null)
            {
                await build.Should().ThrowAsync<Exception>().WithMessage(refusal);
                return;
            }
            var step = await build();
            foreach (var phase in new[] { "eligibility", "prepare", "verify", "commit" })
            {
                await step(phase, [], Guid.NewGuid().ToString("N"), CancellationToken.None);
            }
            var read = post == "none" ? "session" : "post";
            targets.Should().Equal((read, "eligibility"), (read, "prepare"), (read, "verify"), (read, "commit"));
        }
        finally
        {
            Directory.Delete(store, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_shadow_batch_checks_eligibility_in_its_post_session(bool shadow)
    {
        // Calls the batch's real eligibility check (ReviewBatch's caller), client construction included: the host and
        // gateway clients send through scripted endpoints, so the session each submit reaches is read off the wire.
        var store = Path.Combine(Path.GetTempPath(), $"reviewd-tokens-{Guid.NewGuid():N}");
        Directory.CreateDirectory(store);
        try
        {
            await ReadOnlySessions.MarkAsync(store, "post", "workspace", [Repo.DisplayName], default);
            var host = new FakeHttpMessageHandler().OnJson(
                HttpMethod.Get,
                "api/workspaces/workspace",
                """{"id":"workspace","name":"nova","directoryRelPath":"workspace","marketplaces":[]}"""
            );
            var gateway = new ScriptedSandboxGateway { CommandStdout = "{}", WorkspaceRelativePath = "workspace" };
            using var config = HostConfig(store, new HostAndGateway(host, gateway));
            var options = CliOptions.Parse([
                "review",
                "batch",
                "--session",
                "session",
                "--workspace",
                "workspace",
                .. shadow ? ["--shadow", "--post-session", "post"] : Array.Empty<string>(),
            ]);

            _ = await ReviewRunner.CheckEligibilityAsync(options, config, Repo.DisplayName, "17", 2, Head, default);

            // The submit's args are ["-c", <script>, <phase>, <payload>].
            var argv = JsonNode.Parse(gateway.LastSubmitBody!)!["args"]!.AsArray();
            Logger.LogDebug(
                "Eligibility submits to {Sessions}: phase {Phase}, payload {Payload}",
                string.Join(",", gateway.SubmitSessions),
                argv[2]?.ToJsonString(),
                argv[3]?.ToJsonString()
            );
            gateway.SubmitSessions.Should().Equal(shadow ? "post" : "session");
            argv[2]!.GetValue<string>().Should().Be("eligibility");
            var args = JsonNode.Parse(argv[3]!.GetValue<string>())!;
            args["repo"]!.GetValue<string>().Should().Be("Nova");
            args["slot"]!.GetValue<int>().Should().Be(2);
        }
        finally
        {
            Directory.Delete(store, recursive: true);
        }
    }

    [Fact]
    public async Task A_batch_restart_without_shadow_flags_skips_the_slot_sweep_of_a_shadow_database()
    {
        // The startup sweep runs before the batch's own arm check, so the sweep checks the arm itself: a restart that
        // drops --shadow and --post-session would otherwise reset the shadow arm's slots with full egress.
        await SnapshotAsync();
        await ReviewArm.CheckAsync(_database, shadow: true, "kb-h1", pin: true, default);
        var store = Path.Combine(Path.GetTempPath(), $"reviewd-tokens-{Guid.NewGuid():N}");
        Directory.CreateDirectory(store);
        try
        {
            // No gateway or host is configured, so getting past the check would fail differently.
            await File.WriteAllTextAsync(Path.Combine(store, "msal-ado.bin"), "");
            using var config = new ReviewdConfig(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(
                        new Dictionary<string, string?> { ["Auth:TokenStoreDir"] = store, ["Auth:Ado:ClientId"] = "c" }
                    )
                    .Build()
            );
            var options = CliOptions.Parse([
                "review",
                "batch",
                "--db",
                _database,
                "--session",
                "session",
                "--workspace",
                "workspace",
                "--execute",
            ]);

            var skipped = await ReviewBatch.SweepSlotsAsync(options, config, undatedExpired: true, default);

            Logger.LogDebug("Startup sweep skipped: {Reason}", skipped);
            skipped.Should().StartWith("This --db belongs to a shadow arm, so --session session must be");
        }
        finally
        {
            Directory.Delete(store, recursive: true);
        }
    }

    [Fact]
    public async Task A_marker_that_cannot_be_written_names_the_kept_session_and_fails()
    {
        // A file where the marker directory belongs makes the write fail after the session already exists.
        var store = Path.Combine(Path.GetTempPath(), $"reviewd-tokens-{Guid.NewGuid():N}");
        Directory.CreateDirectory(store);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(store, "read-only-sessions"), "");
            using var error = new StringWriter();

            var mark = () =>
                SandboxCommands.MarkCreatedSessionAsync(store, "s-1", "workspace", [Repo.DisplayName], error, default);

            var failure = await mark.Should().ThrowAsync<IOException>();
            Logger.LogDebug(
                "Marker write failed with {Failure}; stderr {Error}",
                failure.Which.Message,
                error.ToString()
            );
            error
                .ToString()
                .Should()
                .StartWith(
                    "Session s-1 was created with read-only egress but its read-only marker could not be written"
                );
            ReadOnlySessions.IsMarked(store, "s-1").Should().BeFalse();
        }
        finally
        {
            Directory.Delete(store, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sandbox_create_sends_the_read_only_policy_and_marks_the_session_the_gateway_created(bool readOnly)
    {
        // Drives the production create (the policy it sends and the marker it writes) over a real SandboxClient: a
        // read-only session whose policy or marker was dropped, or whose marker named another session, would leave a
        // shadow run either refused or posting with full egress. GitHub is signed in too: a read-only session must not
        // be able to push the store branch there.
        var store = Path.Combine(Path.GetTempPath(), $"reviewd-tokens-{Guid.NewGuid():N}");
        Directory.CreateDirectory(store);
        try
        {
            using var config = HostConfig(store, github: true);
            var gateway = new CreateGateway("s-created");
            using var http = new HttpClient(gateway, disposeHandler: false);
            using var sandbox = SandboxOver(http);
            var options = CliOptions.Parse([
                "sandbox",
                "create",
                "--workspace",
                "workspace",
                "--callback",
                "http://127.0.0.1:5203",
                .. readOnly ? ["--read-only-egress", "--read-repos", Repo.DisplayName] : Array.Empty<string>(),
            ]);
            var asked = new List<string>();
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exit = await SandboxCommands.CreateAsync(
                options,
                config,
                sandbox,
                (_, repos, _) =>
                {
                    asked.AddRange(repos.Select(repo => repo.DisplayName));
                    return Task.FromResult<IReadOnlyList<string>>(["/org/project/_git/Nova/git-upload-pack"]);
                },
                output,
                error,
                default
            );

            Logger.LogDebug(
                "Create sent {Body}; printed {Output}; stderr {Error}",
                gateway.CreateBody,
                output.ToString(),
                error.ToString()
            );
            exit.Should().Be(0);
            var rules = JsonNode.Parse(gateway.CreateBody!)!["network"]!["rules"]!.AsArray();
            var ado = rules.Single(rule => rule!["id"]!.GetValue<string>() == "ado")!;
            var methods = ado["methods"]!.AsArray().Select(method => method!.GetValue<string>());
            var githubMethods = rules.Single(rule => rule!["id"]!.GetValue<string>() == "github")!["methods"]!
                .AsArray()
                .Select(method => method!.GetValue<string>());
            var posts = rules
                .Where(rule => rule!["methods"]!.AsArray().Any(method => method!.GetValue<string>() == "POST"))
                .SelectMany(rule => rule!["paths"]!.AsArray().Select(path => path!.GetValue<string>()));
            ReadOnlySessions.IsMarked(store, "s-created").Should().Be(readOnly);
            if (readOnly)
            {
                asked.Should().Equal(Repo.DisplayName);
                methods.Should().Equal("GET", "HEAD");
                githubMethods.Should().Equal("GET", "HEAD");
                posts.Should().Equal("/org/project/_git/Nova/git-upload-pack");
                ReadOnlySessions.Require(store, "s-created", Repo.DisplayName);
            }
            else
            {
                asked.Should().BeEmpty();
                methods.Should().BeEmpty();
                githubMethods.Should().BeEmpty();
            }
        }
        finally
        {
            Directory.Delete(store, recursive: true);
        }
    }

    [Theory]
    [InlineData(true, false, "This --db belongs to a shadow arm, so --session session must be*")]
    [InlineData(true, true, null)]
    [InlineData(false, false, null)]
    public async Task An_operator_reap_or_reclaim_of_a_shadow_database_needs_a_read_only_session(
        bool shadowDb,
        bool marked,
        string? refusal
    )
    {
        // reap-slots and reclaim-slot reset slots with workspace helpers a shadow agent can edit.
        await SnapshotAsync();
        await ReviewArm.CheckAsync(_database, shadowDb, shadowDb ? "kb-h1" : null, pin: true, default);
        var store = Path.Combine(Path.GetTempPath(), $"reviewd-tokens-{Guid.NewGuid():N}");
        Directory.CreateDirectory(store);
        try
        {
            if (marked)
            {
                await ReadOnlySessions.MarkAsync(store, "session", "workspace", [Repo.DisplayName], default);
            }

            var check = () =>
                ReviewArm.RequireReadOnlySessionForShadowAsync(_database, () => store, "session", default);

            if (refusal is null)
            {
                await check.Should().NotThrowAsync();
            }
            else
            {
                await check.Should().ThrowAsync<ArgumentException>().WithMessage(refusal);
            }
        }
        finally
        {
            Directory.Delete(store, recursive: true);
        }
    }

    [Theory]
    [InlineData("reap-slots")]
    [InlineData("reclaim-slot")]
    public async Task Reap_and_reclaim_refuse_a_shadow_database_with_an_unmarked_session_before_any_host_call(
        string command
    )
    {
        await SnapshotAsync();
        await ReviewArm.CheckAsync(_database, shadow: true, "kb-h1", pin: true, default);
        var store = Path.Combine(Path.GetTempPath(), $"reviewd-tokens-{Guid.NewGuid():N}");
        Directory.CreateDirectory(store);
        try
        {
            // No gateway or host is configured, so getting past the check would fail differently.
            await File.WriteAllTextAsync(Path.Combine(store, "msal-ado.bin"), "");
            using var config = new ReviewdConfig(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(
                        new Dictionary<string, string?> { ["Auth:TokenStoreDir"] = store, ["Auth:Ado:ClientId"] = "c" }
                    )
                    .Build()
            );
            string[] common = ["--db", _database, "--workspace", "workspace", "--session", "session", "--execute"];
            var options = CliOptions.Parse(
                command == "reap-slots"
                    ? ["review", command, .. common]
                    : ["review", command, "--repo", Repo.DisplayName, "--pr", "17", "--slot", "2", .. common]
            );

            var run = () =>
                command == "reap-slots"
                    ? SlotReaper.RunAsync(options, config, default)
                    : ReclaimSlotCommand.RunAsync(options, config, default);

            await run.Should().ThrowAsync<ArgumentException>().WithMessage("This --db belongs to a shadow arm*");
        }
        finally
        {
            Directory.Delete(store, recursive: true);
        }
    }

    [Theory]
    [InlineData("https://Contoso@dev.azure.com/Contoso/Weve%20DA/_git/Nova%20Repo")]
    [InlineData("https://contoso.visualstudio.com/DefaultCollection/Weve%20DA/_git/Nova%20Repo")]
    public void Read_only_post_paths_are_the_ones_prepare_sends_for_the_repo(string remoteUrl)
    {
        // Prepare's git fetch lowercases the organization and re-escapes the names from remoteUrl; its REST client
        // keeps the organization as given and addresses the PR query by project and repository id.
        using var record = System.Text.Json.JsonDocument.Parse(
            new JsonObject
            {
                ["id"] = "r-id",
                ["remoteUrl"] = remoteUrl,
                ["project"] = new JsonObject { ["id"] = "p-id" },
            }.ToJsonString()
        );

        var paths = AdoReadOnlyPaths.For(PrCommands.ParseRepo("Contoso/Weve DA/Nova Repo"), record.RootElement);

        paths
            .Should()
            .Equal(
                "/Contoso/Weve%20DA/_git/Nova%20Repo/git-upload-pack",
                "/Contoso/_apis/wit/workitemsbatch",
                "/Contoso/p-id/_apis/git/repositories/r-id/pullrequestquery",
                "/contoso/Weve%20DA/_git/Nova%20Repo/git-upload-pack",
                "/contoso/_apis/wit/workitemsbatch",
                "/contoso/p-id/_apis/git/repositories/r-id/pullrequestquery"
            );
    }

    [Theory]
    [InlineData("https://github.com/Contoso/Nova", true)]
    [InlineData("https://dev.azure.com/Contoso/Weve%20DA/_git/Nova", false)]
    public void An_unexpected_repository_record_creates_no_read_only_session(string remoteUrl, bool withProject)
    {
        var record = new JsonObject { ["id"] = "r-id", ["remoteUrl"] = remoteUrl };
        if (withProject)
        {
            record["project"] = new JsonObject { ["id"] = "p-id" };
        }
        using var document = System.Text.Json.JsonDocument.Parse(record.ToJsonString());

        var paths = () => AdoReadOnlyPaths.For(Repo, document.RootElement);

        paths.Should().Throw<InvalidOperationException>().WithMessage("*no read-only session was created.");
    }

    [Fact]
    public async Task A_variant_run_sends_its_own_task_prompt_and_records_what_produced_the_review()
    {
        // The arm's template drops the quality rules for its own; the receipt names the prompt, manifest and build.
        Directory.CreateDirectory(_files);
        var template = Path.Combine(_files, "b-007.md");
        await File.WriteAllTextAsync(
            template,
            "B-007 RULES. Review {{repo}} #{{pr}} at {{head}}; save {{directory}}/would_post.json."
        );
        var flags = Variant(
            "b-007",
            """{"id":"b-007","provider":"test","taskPrompt":"b-007.md","plugins":{"alias":"cr-b-007","sha":"1111111111111111111111111111111111111111"}}"""
        );
        var handler = Host()
            .OnJson(
                HttpMethod.Get,
                "api/workspaces/workspace",
                """{"id":"workspace","name":"variant-b-007","directoryRelPath":"exp-b","marketplaces":[],"pluginSelection":[{"marketplace":"cr-b-007","plugin":"code-reviewer"}]}"""
            );
        await SnapshotAsync();
        _aliasSha = new string('1', 40);

        var run = () => RunAsync(handler, [], ["--shadow", "--task-prompt-file", template, .. flags]);
        await run.Should().ThrowAsync<InvalidOperationException>();

        var send = handler.Requests.First(r => r.Uri.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal));
        send.Body.Should().Contain("B-007 RULES. Review org/project/Nova #17 at " + Head);
        send.Body.Should().NotContain("Review quality rules");
        var provenance = (await AttemptAsync())["provenance"]!.AsObject();
        provenance["taskPromptSha256"]!
            .GetValue<string>()
            .Should()
            .Be(VariantManifest.Sha256Hex(await File.ReadAllTextAsync(template)));
        provenance["manifestSha256"]!
            .GetValue<string>()
            .Should()
            .Be(VariantManifest.Sha256Hex(await File.ReadAllBytesAsync(flags[3])));
        provenance["experimentId"]!.GetValue<string>().Should().Be("exp-1");
        provenance["pluginSha"]!.GetValue<string>().Should().Be(new string('1', 40));
        provenance["mode"]!.GetValue<string>().Should().Be("workspace-agent");
        provenance["reviewdBuild"]!.GetValue<string>().Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("""{"id":"other"}""", "*pins id other, but this run uses b-007.")]
    [InlineData("""{"id":"b-007","provider":"claude"}""", "*pins provider claude, but this run uses test.")]
    [InlineData("""{"id":"b-007","effort":"xhigh"}""", "*pins reasoning effort xhigh, but this run uses none.")]
    [InlineData("""{"id":"b-007","taskPrompt":"b-007.md"}""", "*pins task prompt SHA-256*")]
    [InlineData("""{"id":"b-007","plugins":{"sha":"main"}}""", "*plugins.sha*must be a 40-character commit SHA.")]
    public async Task A_run_that_differs_from_its_manifest_is_refused_before_any_host_call(
        string manifest,
        string refusal
    )
    {
        // The manifest's task prompt differs from the default one this run sends.
        Directory.CreateDirectory(_files);
        await File.WriteAllTextAsync(Path.Combine(_files, "b-007.md"), "Mine: {{directory}} {{head}}");
        await SnapshotAsync();
        var handler = new FakeHttpMessageHandler();

        var run = () => RunAsync(handler, [], ["--shadow", .. Variant("b-007", manifest)]);

        (await run.Should().ThrowAsync<ArgumentException>()).WithMessage(refusal);
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("--task-prompt-file t.md", "--task-prompt-file sets a shadow review's task prompt and needs --shadow.")]
    [InlineData(
        "--shadow --post-session p --variant b --manifest m.json",
        "--variant, --manifest and --experiment go*"
    )]
    [InlineData(
        "--shadow --post-session p --manifest m.json --experiment e",
        "--variant, --manifest and --experiment go*"
    )]
    [InlineData("--shadow --post-session p --variant b --manifest m --experiment a/b", "--experiment must be*")]
    public void Task_prompt_manifest_and_experiment_belong_to_a_shadow_variant(string flags, string refusal)
    {
        foreach (var command in new[] { RunArguments, ["review", "batch", "--session", "session"] })
        {
            var parse = () => CliOptions.Parse([.. command, .. flags.Split(' ')]);

            parse.Should().Throw<ArgumentException>().WithMessage(refusal);
        }
    }

    [Theory]
    [InlineData("Save {{directory}}/would_post.json for {{head}} on {{branch}}.", "*unknown placeholder {{branch}}*")]
    [InlineData(
        "Save {{directory}}/would_post.json for {{head}} of PR {{pr_number}}.",
        "*unknown placeholder {{pr_number}}*"
    )]
    [InlineData(
        "Save {{directory}}/would_post.json for {{head}} by {{reviewer2}}.",
        "*unknown placeholder {{reviewer2}}*"
    )]
    [InlineData("Save {{directory}}/would_post.json for {{head}} of {{ pr }}.", "*unknown placeholder {{ pr }}*")]
    [InlineData("Review {{pr}} at {{head}}.", "*must use {{directory}}.")]
    [InlineData("Save {{directory}}/would_post.json.", "*must use {{head}}.")]
    public async Task A_task_prompt_template_must_say_where_and_what_it_reviews(string template, string refusal)
    {
        Directory.CreateDirectory(_files);
        var path = Path.Combine(_files, "t.md");
        await File.WriteAllTextAsync(path, template);
        await SnapshotAsync();
        var handler = new FakeHttpMessageHandler();

        var run = () => RunAsync(handler, [], ["--shadow", "--task-prompt-file", path]);

        (await run.Should().ThrowAsync<ArgumentException>()).WithMessage(refusal);
        handler.Requests.Should().BeEmpty();
        ShadowTaskPrompt.Validate(ShadowTaskPrompt.Default, "default");
    }

    [Fact]
    public async Task A_resume_must_send_the_task_prompt_its_attempt_started_with()
    {
        await SnapshotAsync();
        await PutAsync(
            $"reviewd/review-attempt/{Repo.NormalizedKey}/17",
            new JsonObject
            {
                ["repository"] = Repo.DisplayName,
                ["prId"] = "17",
                ["slot"] = 2,
                ["stage"] = "review-running",
                ["threadId"] = "thread",
                ["shadow"] = true,
                ["provenance"] = new JsonObject { ["taskPromptSha256"] = new string('0', 64) },
            }
        );
        var handler = new FakeHttpMessageHandler();

        var run = () => RunAsync(handler, [], ["--shadow", "--resume"]);

        (await run.Should().ThrowAsync<ArgumentException>()).WithMessage(
            "This attempt was started with taskPromptSha256 0000*; a resume must use the same arm*"
        );
        handler.Requests.Should().BeEmpty();
    }

    // A manifest's pins are recorded on every receipt, so a run may only use the workspace and mode variant up built
    // for that manifest; the host holds that binding.
    [Theory]
    [InlineData("directory", "workspace workspace uses directory exp-a, not exp-b")]
    [InlineData("plugin", "workspace workspace takes code-reviewer from claude-plugins, not cr-b-007")]
    [InlineData("mode name", "mode mode-1 is named 'code-review-a', not 'code-review-b-007'")]
    [InlineData("mode setting", "mode mode-1 differs from * in systemPrompt, subAgentPrompt")]
    [InlineData(
        "clone head",
        "the review-repo clone in exp-b is at 9999999999999999999999999999999999999999, not "
            + "2222222222222222222222222222222222222222"
    )]
    [InlineData(
        "alias commit",
        "marketplace alias cr-b-007 resolves to 9999999999999999999999999999999999999999, not "
            + "3333333333333333333333333333333333333333"
    )]
    [InlineData("none", null)]
    [InlineData("mixed-case mode file", null)]
    public async Task A_run_on_a_workspace_or_mode_not_built_for_its_manifest_is_refused_before_provisioning(
        string mismatch,
        string? refusal
    )
    {
        Directory.CreateDirectory(_files);
        // The host binds a field in any letter case, so a run checks the file's "SystemPrompt" as systemPrompt.
        await File.WriteAllTextAsync(
            Path.Combine(_files, "mode.json"),
            mismatch == "mixed-case mode file"
                ? """{"SystemPrompt":"Review as b-007."}"""
                : """{"systemPrompt":"Review as b-007."}"""
        );
        if (mismatch == "clone head")
        {
            _reviewRepoHead = new string('9', 40);
        }
        if (mismatch == "alias commit")
        {
            _aliasSha = new string('9', 40);
        }
        await SnapshotAsync();
        var handler = ArmHost(mismatch);

        var phases = new List<(string Phase, JsonObject Args)>();

        var run = () => RunAsync(handler, phases, ["--shadow", "--mode", "mode-1", .. Variant("b-007", ArmManifest)]);

        if (refusal is null)
        {
            await run.Should().ThrowAsync<InvalidOperationException>("the scripted review never finishes");
            handler
                .Requests.Should()
                .Contain(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/api/conversations");
            phases[0].Phase.Should().Be("review-repo-head", "the clone's commit is read before any other step");
            // The receipt says its start checked the pins, so a resume may keep its reviewer, and records the
            // KnowledgeBase the arm was reviewed with.
            var provenance = (await AttemptAsync())["provenance"]!;
            provenance[ShadowArm.PinsVerified]!.GetValue<bool>().Should().BeTrue();
            provenance["knowledgeBaseTree"]!.GetValue<string>().Should().Be(new string('5', 40));
            return;
        }
        (await run.Should().ThrowAsync<InvalidOperationException>()).WithMessage(
            $"Manifest b-007 was not provisioned as this run's arm: {refusal}. Run variant up*"
        );
        handler.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get, "nothing is provisioned or sent");
        phases.Select(p => p.Phase).Should().BeSubsetOf(["review-repo-head"], "only the read-only head step may run");
    }

    // A preview (no --execute) of a pinned arm checks the host's workspace and mode only: it reads neither the
    // review-repo clone's commit nor the marketplace alias, and writes no arm pin and no attempt.
    [Fact]
    public async Task A_preview_of_a_pinned_arm_reads_no_clone_or_alias_and_pins_nothing()
    {
        await WriteArmModeFileAsync();
        await SnapshotAsync();
        var handler = ArmHost();
        var phases = new List<(string Phase, JsonObject Args)>();

        var exit = await RunAsync(
            handler,
            phases,
            ["--shadow", "--mode", "mode-1", .. Variant("b-007", ArmManifest)],
            execute: false
        );

        exit.Should().Be(0);
        phases.Select(p => p.Phase).Should().NotContain("review-repo-head", "a preview never reads the clone");
        _marketplaceReads.Should().Be(0, "a preview never resolves the marketplace alias");
        handler.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get, "a preview sends only GET reads");
        using var connection = PrThreadBindings.Connection(_database);
        var store = new SqliteKvStore(connection);
        (await store.GetAsync<JsonObject>(ReviewArm.Key)).Should().BeNull("a preview does not pin the --db");
        (await store.GetAsync<JsonObject>($"reviewd/review-attempt/{Repo.NormalizedKey}/17"))
            .Should()
            .BeNull("a preview claims no attempt");
    }

    /// <summary>
    ///     A host holding ArmManifest's workspace and mode as variant up builds them, but for <paramref name="mismatch"/>:
    ///     directory, plugin, mode name or mode setting.
    /// </summary>
    private static FakeHttpMessageHandler ArmHost(string mismatch = "none")
    {
        var workspace = new JsonObject
        {
            ["id"] = "workspace",
            ["name"] = "variant-b-007",
            ["directoryRelPath"] = mismatch == "directory" ? "exp-a" : "exp-b",
            ["marketplaces"] = new JsonArray(),
            ["pluginSelection"] = new JsonArray(
                new JsonObject
                {
                    ["marketplace"] = mismatch == "plugin" ? "claude-plugins" : "cr-b-007",
                    ["plugin"] = "code-reviewer",
                },
                new JsonObject { ["marketplace"] = "sandbox-plugins", ["plugin"] = "review-setup" }
            ),
        };
        // The host writes unset fields as null; the stored mode matches the file unless a setting was edited.
        var mode = new JsonObject
        {
            ["id"] = "mode-1",
            ["name"] = mismatch == "mode name" ? "code-review-a" : "code-review-b-007",
            ["systemPrompt"] = mismatch == "mode setting" ? "Review as a." : "Review as b-007.",
            ["subAgentPrompt"] = mismatch == "mode setting" ? "left over" : null,
            ["isSystemDefined"] = false,
        };
        return Host()
            .OnJson(HttpMethod.Get, "api/workspaces/workspace", workspace.ToJsonString())
            .OnJson(HttpMethod.Get, "api/chat-modes/mode-1", mode.ToJsonString());
    }

    // The step a run reads the clone with: the embedded ReviewRunnerSandbox.py over a real repo gives the head and the
    // commit's KnowledgeBase tree, which the receipt records, or null for a commit with no KnowledgeBase. A replacement
    // ref cannot change the recorded tree, and a tree git cannot read refuses the step instead of reading as absent.
    [SkippableTheory]
    [InlineData("knowledge")]
    [InlineData("none")]
    [InlineData("replaced")]
    [InlineData("damaged")]
    public async Task The_clone_read_gives_the_head_and_its_KnowledgeBase_tree(string clone)
    {
        var python = await ReviewdSummaryReplyTests.FindPythonAsync();
        Skip.If(python is null, "no Python interpreter on PATH, so the sandbox step cannot be run");
        var root = Path.Combine(_files, "repo");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        await GitAsync(root, "init", "-q", "-b", "main");
        await GitAsync(root, "config", "user.email", "t@example.com");
        await GitAsync(root, "config", "user.name", "t");
        await GitAsync(root, "config", "commit.gpgsign", "false");
        await File.WriteAllTextAsync(Path.Combine(root, "src", "a.cs"), "a\n");
        if (clone != "none")
        {
            Directory.CreateDirectory(Path.Combine(root, "KnowledgeBase"));
            await File.WriteAllTextAsync(Path.Combine(root, "KnowledgeBase", "a.md"), "a\n");
        }
        await GitAsync(root, "add", "-A");
        await GitAsync(root, "commit", "-q", "-m", "store");
        var head = await GitAsync(root, "rev-parse", "HEAD");
        var knowledge = clone == "none" ? null : await GitAsync(root, "rev-parse", "HEAD:KnowledgeBase");
        if (clone == "replaced")
        {
            // Another commit with other knowledge stands in for the head wherever git honours replacement refs.
            await File.WriteAllTextAsync(Path.Combine(root, "KnowledgeBase", "a.md"), "b\n");
            await GitAsync(root, "commit", "-q", "-am", "other");
            var other = await GitAsync(root, "rev-parse", "HEAD");
            await GitAsync(root, "checkout", "-q", head);
            await GitAsync(root, "replace", head, other);
        }
        if (clone == "damaged")
        {
            var rootTree = await GitAsync(root, "rev-parse", "HEAD^{tree}");
            var loose = Path.Combine(root, ".git", "objects", rootTree[..2], rootTree[2..]);
            File.SetAttributes(loose, FileAttributes.Normal);
            File.Delete(loose);
        }
        var script = Path.Combine(_files, "sandbox.py");
        using (
            var resource = typeof(ReviewRunner).Assembly.GetManifestResourceStream(
                "Reviewd.Sample.ReviewRunnerSandbox.py"
            )!
        )
        using (var file = File.Create(script))
        {
            await resource.CopyToAsync(file);
        }
        var start = new ProcessStartInfo(python!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = root,
        };
        foreach (var argument in new[] { "-I", script, "review-repo-head", "{}" })
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (clone == "damaged")
        {
            process.ExitCode.Should().NotBe(0);
            error.Should().Contain("git ls-tree failed reading KnowledgeBase");
            (await output).Should().BeEmpty();
            return;
        }
        process.ExitCode.Should().Be(0, error);
        var read = await output;
        JsonNode
            .DeepEquals(JsonNode.Parse(read), new JsonObject { ["head"] = head, ["knowledgeBaseTree"] = knowledge })
            .Should()
            .BeTrue(read);
    }

    // A resume keeps its reviewer and the plugin commit its sandbox mounted. An attempt whose start did not check the
    // clone's and the alias's commits (one started before runs did) may hold another, so it is not resumed as this arm.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_resume_of_an_attempt_whose_start_did_not_check_its_pins_is_refused(bool checkedAtStart)
    {
        await WriteArmModeFileAsync();
        var flags = Variant("b-007", ArmManifest);
        var arm = (
            await ShadowArm.ResolveAsync(
                CliOptions.Parse([.. RunArguments, "--shadow", "--post-session", "read-only", .. flags]),
                "test",
                CancellationToken.None
            )
        )!;
        var provenance = arm.Provenance("mode-1", "test", null);
        if (checkedAtStart)
        {
            provenance[ShadowArm.PinsVerified] = true;
        }
        await SnapshotAsync();
        await PutAsync(
            $"reviewd/review-attempt/{Repo.NormalizedKey}/17",
            new JsonObject
            {
                ["repository"] = Repo.DisplayName,
                ["prId"] = "17",
                ["slot"] = 2,
                ["stage"] = "review-running",
                ["threadId"] = "thread",
                ["shadow"] = true,
                ["provenance"] = provenance,
            }
        );
        var handler = ArmHost();

        var error = await Record.ExceptionAsync(() =>
            RunAsync(handler, [], ["--shadow", "--resume", "--mode", "mode-1", .. flags])
        );

        if (checkedAtStart)
        {
            // Past the arm check, the runner's next resume check (identity) refuses this bare attempt row: that exact
            // outcome, and nothing earlier.
            error
                .Should()
                .BeOfType<InvalidOperationException>()
                .Which.Message.Should()
                .Be("Resume identity, admission or current PR does not match the retained attempt.");
            return;
        }
        error
            .Should()
            .BeOfType<ArgumentException>()
            .Which.Message.Should()
            .Be(
                "This attempt started before a run checked its manifest's clone and alias commits, so its reviewer may "
                    + "have mounted another plugin commit. Release it and review the PR again."
            );
        handler.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get, "nothing is provisioned or sent");
    }

    // A gateway that does not answer the clone's head read or the alias lookup says nothing about the PR or the arm:
    // the review is deferred, which the batch retries with backoff, not refused or held.
    [Theory]
    [InlineData("the review-repo clone's commit")]
    [InlineData("marketplace alias cr-b-007")]
    public async Task A_gateway_that_does_not_answer_a_pin_read_defers_the_review_until_it_does(string read)
    {
        await WriteArmModeFileAsync();
        await SnapshotAsync();
        var handler = ArmHost();
        var flags = Variant("b-007", ArmManifest);
        if (read.StartsWith("marketplace", StringComparison.Ordinal))
        {
            _aliasTimeouts = 2;
        }
        else
        {
            _headTimeouts = 2;
        }

        var run = () => RunAsync(handler, [], ["--shadow", "--mode", "mode-1", .. flags]);

        for (var unanswered = 0; unanswered < 2; unanswered++)
        {
            (await run.Should().ThrowAsync<ReviewRunner.ReviewDeferredException>()).WithMessage(
                $"The gateway did not answer reading {read}: *Retry deferred."
            );
        }
        handler
            .Requests.Should()
            .OnlyContain(r => r.Method == HttpMethod.Get, "a deferred review sends only GET reads");
        await run.Should().ThrowAsync<InvalidOperationException>("the scripted review never finishes");
        handler
            .Requests.Should()
            .Contain(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/api/conversations");
    }

    // The SDK maps only HttpRequestException to a transport failure, so an answer cut mid-body (HttpIOException) or a
    // connection reset while it is read (an IOException over a SocketException) reaches the run as an IOException.
    // Before the claim neither pin read changes anything, so it defers the review like a read that got no answer.
    [Theory]
    [InlineData("the review-repo clone's commit", "cut")]
    [InlineData("the review-repo clone's commit", "reset")]
    [InlineData("marketplace alias cr-b-007", "cut")]
    public async Task A_pin_read_ending_in_an_IOException_defers_the_review_until_a_read_succeeds(
        string read,
        string fault
    )
    {
        await WriteArmModeFileAsync();
        await SnapshotAsync();
        var handler = ArmHost();
        var flags = Variant("b-007", ArmManifest);
        _pinReadFault = () =>
            fault == "cut"
                ? new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely.")
                : new IOException(
                    "Unable to read data from the transport connection.",
                    new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionReset)
                );
        if (read.StartsWith("marketplace", StringComparison.Ordinal))
        {
            _aliasTimeouts = 2;
        }
        else
        {
            _headTimeouts = 2;
        }

        var run = () => RunAsync(handler, [], ["--shadow", "--mode", "mode-1", .. flags]);

        for (var cut = 0; cut < 2; cut++)
        {
            (await run.Should().ThrowAsync<ReviewRunner.ReviewDeferredException>()).WithMessage(
                $"The gateway's answer to {read} was cut: *Retry deferred."
            );
        }
        handler
            .Requests.Should()
            .OnlyContain(r => r.Method == HttpMethod.Get, "a deferred review sends only GET reads");
        await run.Should().ThrowAsync<InvalidOperationException>("the scripted review never finishes");
        handler
            .Requests.Should()
            .Contain(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/api/conversations");
    }

    // The mode file is read once: the hash the receipt and a resume record is of the very bytes the run checks, so an
    // edit after the arm was resolved cannot make the host's mode pass for the file's hash.
    [Fact]
    public async Task A_run_checks_the_mode_file_bytes_it_hashed_even_if_the_file_changes_after()
    {
        Directory.CreateDirectory(_files);
        var modeFile = Path.Combine(_files, "mode.json");
        await File.WriteAllTextAsync(modeFile, """{"systemPrompt":"Review as b-007, v1."}""");
        var flags = Variant("b-007", """{"id":"b-007","mode":{"name":"code-review-b-007","file":"mode.json"}}""");
        var arm = (
            await ShadowArm.ResolveAsync(
                CliOptions.Parse([.. RunArguments, "--shadow", "--post-session", "read-only", .. flags]),
                "test",
                CancellationToken.None
            )
        )!;
        // Edited (and rebuilt on the host) after the run resolved its arm.
        await File.WriteAllTextAsync(modeFile, """{"systemPrompt":"Review as b-007, v2."}""");
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            "api/chat-modes/mode-b",
            """{"id":"mode-b","name":"code-review-b-007","systemPrompt":"Review as b-007, v2."}"""
        );
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };

        var check = () =>
            arm.CheckProvisionedAsync(
                new LmStreamingS2SClient(http, null, null, null),
                "workspace",
                "mode-b",
                null,
                null,
                CancellationToken.None
            );

        arm.ModeFileSha256.Should().Be(VariantManifest.Sha256Hex("""{"systemPrompt":"Review as b-007, v1."}"""));
        (await check.Should().ThrowAsync<InvalidOperationException>()).WithMessage(
            "*mode mode-b differs from * in systemPrompt*"
        );
    }

    // A resumed attempt finishes under the arm it started with: its manifest and its experiment, each on its own.
    [Theory]
    [InlineData("manifestSha256", "0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("experimentId", "exp-0")]
    [InlineData("none", null)]
    public async Task A_resume_must_use_the_manifest_and_experiment_its_attempt_started_with(
        string changed,
        string? started
    )
    {
        var flags = Variant("b-007", """{"id":"b-007","provider":"test"}""");
        var provenance = new JsonObject
        {
            ["taskPromptSha256"] = VariantManifest.Sha256Hex(ShadowTaskPrompt.Default),
            ["experimentId"] = "exp-1",
            ["manifestSha256"] = VariantManifest.Sha256Hex(await File.ReadAllBytesAsync(flags[3])),
        };
        // What this run is: a resume of an attempt that recorded anything else is refused.
        var current = started is null ? null : provenance[changed]!.ToString();
        if (started is not null)
        {
            provenance[changed] = started;
        }
        await SnapshotAsync();
        await PutAsync(
            $"reviewd/review-attempt/{Repo.NormalizedKey}/17",
            new JsonObject
            {
                ["repository"] = Repo.DisplayName,
                ["prId"] = "17",
                ["slot"] = 2,
                ["stage"] = "review-running",
                ["threadId"] = "thread",
                ["shadow"] = true,
                ["provenance"] = provenance,
            }
        );
        var handler = new FakeHttpMessageHandler();
        var phases = new List<(string Phase, JsonObject Args)>();

        var error = await Record.ExceptionAsync(() => RunAsync(handler, phases, ["--shadow", "--resume", .. flags]));

        if (started is null)
        {
            error?.Message.Should().NotContain("a resume must use the same arm", "the same arm passes the check");
            return;
        }
        error
            .Should()
            .BeOfType<ArgumentException>()
            .Which.Message.Should()
            .Be($"This attempt was started with {changed} {started}; a resume must use the same arm, not {current}.");
        handler.Requests.Should().BeEmpty("nothing is provisioned or sent");
        phases.Should().BeEmpty("no sandbox step runs");
    }

    // A restarted batch resumes every attempt it still owns; one whose arm changed is refused before it resumes any.
    [Theory]
    [InlineData("taskPromptSha256", "0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("manifestSha256", "0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("experimentId", "exp-0")]
    [InlineData("none", null)]
    public async Task A_restarted_batch_whose_arm_changed_is_refused_before_it_resumes_anything(
        string changed,
        string? started
    )
    {
        var flags = Variant("b-007", """{"id":"b-007","provider":"test"}""");
        var arm = (
            await ShadowArm.ResolveAsync(
                CliOptions.Parse([.. RunArguments, "--shadow", "--post-session", "read-only", .. flags]),
                "test",
                CancellationToken.None
            )
        )!;
        var provenance = arm.Provenance("mode", "test", null);
        if (started is not null)
        {
            provenance[changed] = started;
        }
        async Task Attempt(int pr, string stage, string cleanup)
        {
            await PutAsync(
                $"reviewd/review-attempt/{Repo.NormalizedKey}/{pr}",
                new JsonObject
                {
                    ["repository"] = Repo.DisplayName,
                    ["prId"] = pr.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["stage"] = stage,
                    ["reviewInputId"] = "input",
                    ["claimToken"] = "claim",
                    ["cleanupState"] = cleanup,
                    ["shadow"] = true,
                    ["provenance"] = provenance.DeepClone(),
                }
            );
        }
        await Attempt(17, "review-running", "owned");
        // None of these resumes: a released slot holds nothing, and a failed or verified released attempt starts over.
        await Attempt(18, "review-running", "released");
        await Attempt(19, "failed-released", "released");
        await Attempt(20, "completed-verified", "released");

        var check = () => ReviewBatch.CheckResumesAsync(_database, arm, "mode", "test", null, CancellationToken.None);

        if (started is null)
        {
            await check.Should().NotThrowAsync("every retained attempt started with this arm");
            return;
        }
        var refusal = (await check.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        refusal
            .Should()
            .StartWith(
                "This batch's arm differs from the one 1 retained attempt(s) started with, so none could resume."
            )
            .And.Contain($"org/project/Nova #17: This attempt was started with {changed} {started};")
            .And.NotContainAny("#18", "#19", "#20");
    }

    // The batch itself refuses at start, before its sweep, its lease or any review: deleting the start call fails this.
    [Fact]
    public async Task A_batch_started_with_another_arm_than_a_retained_attempt_refuses_before_anything_runs()
    {
        var flags = Variant("b-007", """{"id":"b-007","provider":"test"}""");
        await PutAsync(
            $"reviewd/review-attempt/{Repo.NormalizedKey}/17",
            new JsonObject
            {
                ["repository"] = Repo.DisplayName,
                ["prId"] = "17",
                ["stage"] = "review-running",
                ["reviewInputId"] = "input",
                ["claimToken"] = "claim",
                ["cleanupState"] = "owned",
                ["shadow"] = true,
                ["provenance"] = new JsonObject { ["taskPromptSha256"] = new string('0', 64) },
            }
        );
        var host = new FakeHttpMessageHandler();
        var gateway = new FakeHttpMessageHandler();
        using var config = new ReviewdConfig(new ConfigurationBuilder().Build())
        {
            Transport = new HostAndGateway(host, gateway),
        };
        var options = CliOptions.Parse([
            "review",
            "batch",
            "--selection",
            Path.Combine(_files, "selection.json"),
            "--db",
            _database,
            "--session",
            "session",
            "--workspace",
            "workspace",
            "--provider",
            "test",
            "--shadow",
            "--post-session",
            "read-only",
            .. flags,
            "--execute",
        ]);
        // A batch past its start would sweep and then wait on its selection: the deadline ends that, so a missing
        // start check fails here instead of hanging.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var start = () => ReviewBatch.RunBatchAsync(options, config, deadline.Token);

        (await start.Should().ThrowAsync<InvalidOperationException>()).WithMessage(
            "This batch's arm differs from the one 1 retained attempt(s) started with, so none could resume.*"
        );
        gateway.Requests.Should().BeEmpty("the startup sweep never ran");
        host.Requests.Should().BeEmpty();
    }

    // The host binds a chat-mode field in any letter case; the client sends and checks each under the host's name.
    [Fact]
    public void A_mode_field_in_any_letter_case_goes_to_the_host_under_its_own_name()
    {
        var replaced = LmStreamingS2SClient.ChatModeReplacement(
            JsonNode.Parse("""{"Name":"m","SystemPrompt":"p","SubAgentPrompt":"s","custom":1}""")!.AsObject()
        );
        var twice = () =>
            LmStreamingS2SClient.ChatModeReplacement(
                JsonNode.Parse("""{"subAgentPrompt":"a","SubAgentPrompt":"b"}""")!.AsObject()
            );

        replaced["name"]!.GetValue<string>().Should().Be("m");
        replaced["systemPrompt"]!.GetValue<string>().Should().Be("p");
        replaced["subAgentPrompt"]!.GetValue<string>().Should().Be("s");
        replaced["custom"]!.GetValue<int>().Should().Be(1, "a field the host does not know is sent as written");
        replaced.Select(field => field.Key).Should().NotContain(["Name", "SystemPrompt", "SubAgentPrompt"]);
        twice
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("The chat mode sets subAgentPrompt twice, in different letter cases.");
    }

    // A rebuilt arm may change only its mode file; a resume must still finish under the mode its attempt started with.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_resume_must_use_the_mode_file_its_attempt_started_with(bool changed)
    {
        Directory.CreateDirectory(_files);
        var modeFile = Path.Combine(_files, "mode-b.json");
        await File.WriteAllTextAsync(modeFile, """{"systemPrompt":"Review as b-007, v1."}""");
        var started = VariantManifest.Sha256Hex(await File.ReadAllBytesAsync(modeFile));
        if (changed)
        {
            // Edited and rebuilt with variant up, so the host's mode matches the new file.
            await File.WriteAllTextAsync(modeFile, """{"systemPrompt":"Review as b-007, v2."}""");
        }
        var flags = Variant("b-007", """{"id":"b-007","mode":{"name":"code-review-b-007","file":"mode-b.json"}}""");
        await SnapshotAsync();
        await PutAsync(
            $"reviewd/review-attempt/{Repo.NormalizedKey}/17",
            new JsonObject
            {
                ["repository"] = Repo.DisplayName,
                ["prId"] = "17",
                ["slot"] = 2,
                ["stage"] = "review-running",
                ["threadId"] = "thread",
                ["shadow"] = true,
                ["provenance"] = new JsonObject
                {
                    ["taskPromptSha256"] = VariantManifest.Sha256Hex(ShadowTaskPrompt.Default),
                    ["experimentId"] = "exp-1",
                    ["manifestSha256"] = VariantManifest.Sha256Hex(await File.ReadAllBytesAsync(flags[3])),
                    ["modeFileSha256"] = started,
                },
            }
        );
        var stored = new JsonObject
        {
            ["id"] = "mode-b",
            ["name"] = "code-review-b-007",
            ["systemPrompt"] = changed ? "Review as b-007, v2." : "Review as b-007, v1.",
        };
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            "api/chat-modes/mode-b",
            stored.ToJsonString()
        );

        var error = await Record.ExceptionAsync(() =>
            RunAsync(handler, [], ["--shadow", "--resume", "--mode", "mode-b", .. flags])
        );

        if (changed)
        {
            error
                .Should()
                .BeOfType<ArgumentException>()
                .Which.Message.Should()
                .Match($"This attempt was started with modeFileSha256 {started}; a resume must use the same arm*");
            handler.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get, "the host only read the mode");
        }
        else
        {
            error?.Message.Should().NotContain("modeFileSha256", "an unchanged mode file passes the identity check");
        }
    }

    // A replaced mode must hold what a fresh one would: a field the mode file dropped is cleared, not kept.
    [Fact]
    public async Task Variant_up_replaces_an_existing_mode_with_exactly_what_a_fresh_one_holds()
    {
        var fresh = VariantHost();
        var rebuilt = VariantHost(
            """[{"id":"mode-1","name":"code-review-b-007","isSystemDefined":false,"subAgentPrompt":"old rule"}]"""
        );

        await VariantUpAsync(fresh, new string('2', 40));
        await VariantUpAsync(rebuilt, new string('2', 40));

        var created = JsonNode.Parse(
            fresh.Requests.Single(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/api/chat-modes").Body!
        )!;
        var replaced = JsonNode.Parse(
            rebuilt
                .Requests.Single(r => r.Method == HttpMethod.Put && r.Uri.AbsolutePath == "/api/chat-modes/mode-1")
                .Body!
        )!;
        rebuilt
            .Requests.Should()
            .NotContain(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/api/chat-modes");
        JsonNode
            .DeepEquals(replaced, created)
            .Should()
            .BeTrue($"{replaced.ToJsonString()} vs {created.ToJsonString()}");
        var body = replaced.AsObject();
        body["systemPrompt"]!.GetValue<string>().Should().Be("Review as b-007.");
        // Written out, not read from the client's list, so a field dropped from that list fails here.
        string[] optional =
        [
            "description",
            "enabledTools",
            "enabledBuiltInTools",
            "enabledCapabilityTools",
            "subAgentPrompt",
            "subAgentPromptPlacement",
            "subAgentRequiredTools",
            "subAgentReasoningEffort",
            "subAgentModelIntelligenceByType",
            "defaultSubAgentModelIntelligence",
            "env",
            "tiers",
        ];
        body.Select(field => field.Key).Should().BeEquivalentTo(["name", "systemPrompt", .. optional]);
        foreach (var field in optional)
        {
            body.ContainsKey(field).Should().BeTrue(field + " is sent");
            body[field].Should().BeNull(field + " is cleared, as a fresh mode leaves it");
        }
    }

    // The collector itself, over real git: the arm's KnowledgeBase/ tree is compared with the champion commit's.
    [Fact]
    public async Task Variant_up_compares_the_whole_KnowledgeBase_tree_with_the_champion_over_real_git()
    {
        var root = Path.Combine(_files, "clone");
        Directory.CreateDirectory(Path.Combine(root, "KnowledgeBase"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        async Task<string> Commit(string message, params (string Path, string Text)[] files)
        {
            foreach (var (path, text) in files)
            {
                await File.WriteAllTextAsync(Path.Combine(root, path), text);
            }
            await GitAsync(root, "add", "-A");
            await GitAsync(root, "commit", "-q", "-m", message);
            return await GitAsync(root, "rev-parse", "HEAD");
        }
        await GitAsync(root, "init", "-q", "-b", "main");
        await GitAsync(root, "config", "user.email", "t@example.com");
        await GitAsync(root, "config", "user.name", "t");
        await GitAsync(root, "config", "commit.gpgsign", "false");
        var older = await Commit("older", ("KnowledgeBase/a.md", "a v1\n"), ("src/x.cs", "x v1\n"));
        var champion = await Commit("champion", ("KnowledgeBase/a.md", "a v2\n"));
        await GitAsync(root, "checkout", "-q", "-b", "arm-src");
        var sourceOnly = await Commit("source", ("src/x.cs", "x v2\n"));
        await GitAsync(root, "checkout", "-q", "-b", "arm-kb", champion);
        var knowledge = await Commit("knowledge", ("KnowledgeBase/b.md", "b\n"));
        VariantUp.Git git = async (what, arguments, _) =>
        {
            var (exit, output, error) = await RunGitAsync(root, arguments);
            return exit == 0 ? output : throw new InvalidOperationException($"{what} failed: {error}");
        };
        async Task<VariantUp.Clone> At(string commit, string against)
        {
            await GitAsync(root, "checkout", "-q", commit);
            return await VariantUp.ReadCloneAsync(git, against, CancellationToken.None);
        }

        var edits = await At(knowledge, champion);
        var source = await At(sourceOnly, champion);
        var ancestor = await At(older, champion);
        var absent = () => At(knowledge, new string('f', 40));

        edits.Head.Should().Be(knowledge);
        edits.KnowledgeChanges.Should().Equal("KnowledgeBase/b.md");
        source.KnowledgeChanges.Should().BeEmpty("a source-only arm holds the champion's KnowledgeBase");
        ancestor
            .KnowledgeChanges.Should()
            .Equal(["KnowledgeBase/a.md"], "an older base forks with no delta of its own");
        (await absent.Should().ThrowAsync<InvalidOperationException>()).WithMessage(
            $"Comparing KnowledgeBase/ with champion {new string('f', 40)} (fetch it into the clone first) failed: *"
        );
    }

    // variant up's own RunAsync: it opens a session on the arm's directory and reads the clone there with git (run
    // here over a real repo). A champion the clone lacks is refused before any write; the champion's commit builds.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Variant_up_reads_the_clone_over_real_git_and_refuses_a_champion_it_lacks_before_any_write(
        bool championPresent
    )
    {
        var root = Path.Combine(_files, "clone");
        Directory.CreateDirectory(Path.Combine(root, "KnowledgeBase"));
        await GitAsync(root, "init", "-q", "-b", "main");
        await GitAsync(root, "config", "user.email", "t@example.com");
        await GitAsync(root, "config", "user.name", "t");
        await GitAsync(root, "config", "commit.gpgsign", "false");
        await File.WriteAllTextAsync(Path.Combine(root, "KnowledgeBase", "a.md"), "a\n");
        await GitAsync(root, "add", "-A");
        await GitAsync(root, "commit", "-q", "-m", "champion");
        var head = await GitAsync(root, "rev-parse", "HEAD");
        var champion = championPresent ? head : new string('f', 40);
        await WriteArmModeFileAsync();
        var flags = Variant("b-007", ArmManifest.Replace(new string('2', 40), head, StringComparison.Ordinal));
        var host = VariantHost();
        var gateway = new VariantGateway(
            new ScriptedSandboxGateway
            {
                Execute = async (executable, arguments) =>
                {
                    executable.Should().Be("git");
                    return await RunGitAsync(root, [.. arguments]);
                },
            }
        );
        using var config = new ReviewdConfig(new ConfigurationBuilder().Build())
        {
            Transport = new HostAndGateway(host, gateway),
        };
        var options = CliOptions.Parse([
            "variant",
            "up",
            "--manifest",
            flags[3],
            "--experiment",
            "exp-1",
            "--store",
            _files,
            "--from-workspace",
            "base",
            "--champion",
            champion,
            "--execute",
        ]);

        var up = () => VariantUp.RunAsync(options, config, CancellationToken.None);

        if (championPresent)
        {
            (await up()).Should().Be(0);
            gateway.Created.Should().Equal("exp-b");
            gateway.Registered.Should().Equal("cr-b-007");
            host.Requests.Should().Contain(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/api/workspaces");
            return;
        }
        (await up.Should().ThrowAsync<InvalidOperationException>()).WithMessage(
            $"Comparing KnowledgeBase/ with champion {champion} (fetch it into the clone first) failed in exp-b: *"
        );
        gateway.Created.Should().Equal("exp-b");
        gateway.Registered.Should().BeEmpty("no alias is registered");
        host.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get, "no mode or workspace was made");
    }

    /// <summary>
    ///     A gateway for variant up: it opens sessions and registers marketplaces (each recorded), and sends everything
    ///     else, the git commands included, to <paramref name="exec"/>.
    /// </summary>
    private sealed class VariantGateway(ScriptedSandboxGateway exec) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _exec = new(exec, disposeHandler: false);

        public List<string> Created { get; } = [];
        public List<string> Registered { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/api/v1/sandboxes")
            {
                var create = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
                Created.Add(create["workspace"]!.GetValue<string>());
                return Json(new JsonObject { ["session_id"] = "sess-1", ["container_id"] = null });
            }
            if (request.Method == HttpMethod.Put && path.Contains("/apps/self/marketplaces/", StringComparison.Ordinal))
            {
                var register = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
                var alias = Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]);
                Registered.Add(alias);
                return Json(
                    new JsonObject
                    {
                        ["app_id"] = "codereview-daemon",
                        ["alias"] = alias,
                        ["url"] = register["url"]!.DeepClone(),
                        ["repo_key"] = "key",
                        ["ref"] = register["ref"]?.DeepClone(),
                        ["resolved_sha"] = register["ref"]?.DeepClone(),
                        ["created_at"] = "2026-11-18T00:00:00Z",
                        ["updated_at"] = "2026-11-18T00:00:00Z",
                        ["last_error"] = null,
                    }
                );
            }
            return await _exec.SendAsync(request, cancellationToken);
        }

        private static HttpResponseMessage Json(JsonObject body) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
            };
    }

    // The host stores a mode value as it parses it: a number written as a string comes back as a number, and a field or
    // array element the file lacks would be one the run never asked for; every run would refuse such an arm, so
    // variant up reads the saved mode back and refuses it, naming the field. A nested name in another letter case is
    // bound by the host and stored in its own (tiers.Caps.Skill as caps.skill): that arm builds. An env name is data
    // the host matches exactly, so FOO stored as foo differs.
    [Theory]
    [InlineData("tiers", """{"caps":{"skill":3}}""", """{"caps":{"skill":3}}""", true)]
    [InlineData("tiers", """{"Caps":{"Skill":3}}""", """{"caps":{"skill":3}}""", true)]
    [InlineData("tiers", """{"caps":{"skill":"3"}}""", """{"caps":{"skill":3}}""", false)]
    [InlineData("tiers", """{"caps":{"skill":3}}""", """{"caps":{"skill":3,"agent":2}}""", false)]
    [InlineData("tiers", """{"loaded":["skill:a"]}""", """{"loaded":["skill:a","skill:b"]}""", false)]
    [InlineData("env", """{"FOO":"1"}""", """{"FOO":"1"}""", true)]
    [InlineData("env", """{"FOO":"1"}""", """{"foo":"1"}""", false)]
    public async Task Variant_up_refuses_a_mode_the_host_stores_otherwise_than_its_file(
        string field,
        string fileValue,
        string storedValue,
        bool builds
    )
    {
        var handler = VariantHost(
            stored: $$"""{"id":"mode-1","name":"code-review-b-007","systemPrompt":"Review as b-007.","{{field}}":{{storedValue}}}"""
        );

        var up = () =>
            VariantUpAsync(
                handler,
                new string('2', 40),
                modeFile: $$"""{"systemPrompt":"Review as b-007.","{{field}}":{{fileValue}}}"""
            );

        if (builds)
        {
            (await up()).Result.Should().ContainKey("modeId");
            return;
        }
        (await up.Should().ThrowAsync<InvalidOperationException>()).WithMessage(
            $"The host stored mode mode-1 differently from * in {field}, so every run of this arm would be refused.*"
        );
        handler
            .Requests.Should()
            .NotContain(
                r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/api/workspaces",
                "no workspace is made for a mode that runs would refuse"
            );
    }

    private static readonly string S_champion = new('4', 40);

    private const string ArmManifest = """
        {"id":"b-007","reviewRepo":{"directory":"exp-b","ref":"exp/b-007","sha":"2222222222222222222222222222222222222222"},
         "plugins":{"alias":"cr-b-007","url":"https://github.com/o/ClaudePlugins","sha":"3333333333333333333333333333333333333333"},
         "mode":{"name":"code-review-b-007","file":"mode.json"},"provider":"test"}
        """;

    /// <summary>
    ///     A host with the base workspace and no arm yet; the arm's mode and workspace are created. The saved mode reads
    ///     back as the last create or replace sent it, unless <paramref name="stored"/> says what the host stored.
    /// </summary>
    private static FakeHttpMessageHandler VariantHost(string modes = "[]", string? stored = null)
    {
        var handler = new FakeHttpMessageHandler();
        string Saved()
        {
            var sent = JsonNode
                .Parse(
                    handler
                        .Requests.Last(r =>
                            (r.Method == HttpMethod.Post || r.Method == HttpMethod.Put)
                            && r.Uri.AbsolutePath.StartsWith("/api/chat-modes", StringComparison.Ordinal)
                        )
                        .Body!
                )!
                .AsObject();
            sent["id"] = "mode-1";
            return sent.ToJsonString();
        }
        return handler
            .On(
                request =>
                    request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/chat-modes/mode-1",
                _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(stored ?? Saved()) }
            )
            .OnJson(
                HttpMethod.Get,
                "api/workspaces/base",
                """{"id":"base","name":"nova","directoryRelPath":"nova-reviews","marketplaces":["claude-plugins","sandbox-plugins"],"pluginSelection":[{"marketplace":"claude-plugins","plugin":"code-reviewer"},{"marketplace":"sandbox-plugins","plugin":"review-setup"}],"pluginsRevision":3}"""
            )
            .OnJson(
                HttpMethod.Get,
                "api/workspaces",
                """[{"id":"base","name":"nova","directoryRelPath":"nova-reviews","marketplaces":[]}]"""
            )
            .OnJson(HttpMethod.Get, "api/chat-modes", modes)
            .OnJson(HttpMethod.Post, "api/chat-modes", """{"id":"mode-1"}""")
            .OnJson(HttpMethod.Put, "api/chat-modes/", """{"id":"mode-1"}""")
            .OnJson(
                HttpMethod.Post,
                "api/workspaces",
                """{"id":"ws-b","name":"variant-b-007","directoryRelPath":"exp-b","marketplaces":[]}"""
            );
    }

    private async Task<(JsonObject Result, List<string> Calls)> VariantUpAsync(
        FakeHttpMessageHandler handler,
        string cloneSha,
        bool execute = true,
        string modeFile = """{"systemPrompt":"Review as b-007."}""",
        string? store = null,
        params string[] knowledgeChanges
    )
    {
        Directory.CreateDirectory(_files);
        await File.WriteAllTextAsync(Path.Combine(_files, "mode.json"), modeFile);
        var flags = Variant("b-007", ArmManifest);
        var calls = new List<string>();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var options = CliOptions.Parse([
            "variant",
            "up",
            "--manifest",
            flags[3],
            "--experiment",
            "exp-1",
            "--store",
            store ?? _files,
            "--from-workspace",
            "base",
            "--champion",
            S_champion,
            .. execute ? new[] { "--execute" } : [],
        ]);
        var result = await VariantUp.RunCoreAsync(
            options,
            new LmStreamingS2SClient(http, null, null, null),
            (alias, url, sha, _) =>
            {
                calls.Add($"register {alias} {url} {sha}");
                return Task.FromResult(sha);
            },
            (directory, champion, _) =>
            {
                calls.Add($"head {directory} against {champion}");
                return Task.FromResult(new VariantUp.Clone(cloneSha, knowledgeChanges));
            },
            CancellationToken.None
        );
        return (result, calls);
    }

    [Fact]
    public async Task Variant_up_builds_the_arm_from_the_base_workspace_with_only_the_reviewer_from_its_alias()
    {
        var handler = VariantHost();

        var (result, calls) = await VariantUpAsync(handler, new string('2', 40));

        calls
            .Should()
            .Equal(
                $"head exp-b against {S_champion}",
                $"register cr-b-007 https://github.com/o/ClaudePlugins {new string('3', 40)}"
            );
        var mode = handler.Requests.Single(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/api/chat-modes");
        JsonNode.Parse(mode.Body!)!["name"]!.GetValue<string>().Should().Be("code-review-b-007");
        var created = JsonNode.Parse(
            handler.Requests.Single(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/api/workspaces").Body!
        )!;
        created["name"]!.GetValue<string>().Should().Be("variant-b-007");
        created["directoryRelPath"]!.GetValue<string>().Should().Be("exp-b");
        created["marketplaces"]!
            .AsArray()
            .Select(m => m!.GetValue<string>())
            .Should()
            .Equal("claude-plugins", "sandbox-plugins", "cr-b-007");
        created["pluginSelection"]!
            .AsArray()
            .Select(p => $"{p!["marketplace"]}/{p["plugin"]}")
            .Should()
            .Equal("cr-b-007/code-reviewer", "sandbox-plugins/review-setup");
        result["workspaceId"]!.GetValue<string>().Should().Be("ws-b");
        result["next"]!
            .GetValue<string>()
            .Should()
            .Contain("--workspace ws-b --provider test --mode mode-1 --shadow")
            .And.Contain("--variant b-007 --manifest \"")
            .And.Contain($"--experiment exp-1 --store \"{_files}\" ")
            .And.Contain("--read-only-egress --read-repos <org/project/repo,");
        result["champion"]!.GetValue<string>().Should().Be(S_champion);
    }

    // A --store typed with trailing separators prints without them, so the copied command's closing quote is not
    // escaped by a trailing backslash, and the quotes keep a path with a space whole.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Variant_up_prints_the_store_quoted_without_its_trailing_separators(int separators)
    {
        var store = Path.Combine(_files, "review store");
        StudyFixture.Write(store, ("exp-1", "experiment", null));
        var typed = store + new string(Path.DirectorySeparatorChar, separators);

        var (result, _) = await VariantUpAsync(VariantHost(), new string('2', 40), store: typed);

        var next = result["next"]!.GetValue<string>();
        Logger.LogInformation("--store {Store} printed as {Next}", typed, next);
        next.Should().Contain($"--experiment exp-1 --store \"{store}\" ");
    }

    [Fact]
    public async Task Variant_up_changes_nothing_when_the_clone_is_not_at_the_pinned_commit_or_on_a_preview()
    {
        var handler = VariantHost();

        var preview = await VariantUpAsync(handler, new string('2', 40), execute: false);
        var wrongClone = () => VariantUpAsync(handler, new string('9', 40));

        preview.Calls.Should().BeEmpty();
        preview.Result["preview"]!.GetValue<bool>().Should().BeTrue();
        (await wrongClone.Should().ThrowAsync<InvalidOperationException>()).WithMessage(
            $"The review-repo clone in exp-b is at {new string('9', 40)}, but manifest b-007 pins*"
        );
        handler.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
    }

    // An arm is reviewed and judged in its own workspace, with that workspace's KnowledgeBase, so an arm holding
    // another one than the frozen champion would be judged with other knowledge.
    [Fact]
    public async Task Variant_up_refuses_an_arm_whose_commit_changes_the_KnowledgeBase_and_changes_nothing()
    {
        var handler = VariantHost();

        var changed = () =>
            VariantUpAsync(
                handler,
                new string('2', 40),
                knowledgeChanges: ["KnowledgeBase/nova/a.md", "KnowledgeBase/_index.jsonl"]
            );

        (await changed.Should().ThrowAsync<InvalidOperationException>()).WithMessage(
            $"Manifest b-007's commit holds a different KnowledgeBase/ from champion {S_champion} "
                + "(KnowledgeBase/nova/a.md, KnowledgeBase/_index.jsonl). Every arm reviews and is judged with*"
        );
        handler.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get, "no mode or workspace was made");
    }

    // The study store is the only state between rounds: no arm is built or reviewed outside an experiment record it
    // holds. review run, review batch and variant up each refuse before any host or gateway call.
    [Theory]
    [InlineData("no record", "The study at * has no record exp-2.*")]
    [InlineData("another kind", "Study record exp-2 is a hypothesis record, not an experiment.")]
    [InlineData("superseded", "Study record exp-2 was superseded by exp-3;*")]
    [InlineData("no study", "--store * holds no study at .experimentation/reviewer-evolution/*")]
    public async Task An_arm_under_an_experiment_the_study_does_not_hold_is_neither_built_nor_reviewed(
        string study,
        string refusal
    )
    {
        var flags = Variant("b-007", """{"id":"b-007","provider":"test"}""");
        var store = _files;
        switch (study)
        {
            case "another kind":
                StudyFixture.Write(_files, ("exp-2", "hypothesis", null));
                break;
            case "superseded":
                StudyFixture.Write(_files, ("exp-2", "experiment", null), ("exp-3", "experiment", "exp-2"));
                break;
            case "no study":
                store = Path.Combine(_files, "empty");
                Directory.CreateDirectory(store);
                break;
            default:
                break; // "no record": exp-2 is in no record
        }
        flags[5] = "exp-2";
        flags[7] = store;
        var host = new FakeHttpMessageHandler();
        var gateway = new FakeHttpMessageHandler();
        using var config = new ReviewdConfig(new ConfigurationBuilder().Build())
        {
            Transport = new HostAndGateway(host, gateway),
        };
        using var http = new HttpClient(host) { BaseAddress = new Uri("http://localhost/") };
        string[] shadow = ["--session", "session", "--workspace", "workspace", "--provider", "test", "--shadow"];
        // The public entry: on --execute it reads the workspace and session from the host and gateway before its run.
        var run = CliOptions.Parse([
            "review",
            "run",
            "--repo",
            Repo.DisplayName,
            "--pr",
            "17",
            "--slot",
            "2",
            "--db",
            _database,
            "--max-age-hours",
            "72",
            .. shadow,
            "--post-session",
            "read-only",
            .. flags,
            "--execute",
        ]);
        var batch = CliOptions.Parse([
            "review",
            "batch",
            "--selection",
            Path.Combine(_files, "selection.json"),
            "--db",
            _database,
            .. shadow,
            "--post-session",
            "read-only",
            .. flags,
            "--execute",
        ]);
        var up = CliOptions.Parse([
            "variant",
            "up",
            "--manifest",
            flags[3],
            "--experiment",
            "exp-2",
            "--store",
            store,
            "--from-workspace",
            "base",
            "--champion",
            S_champion,
        ]);

        var entries = new (string Name, Func<Task> Run)[]
        {
            ("review run", () => ReviewRunner.RunAsync(run, config, CancellationToken.None)),
            ("review batch", () => ReviewBatch.RunBatchAsync(batch, config, CancellationToken.None)),
            (
                "variant up",
                () =>
                    VariantUp.RunCoreAsync(
                        up,
                        new LmStreamingS2SClient(http, null, null, null),
                        (_, _, _, _) => Task.FromResult(""),
                        (_, _, _) => Task.FromResult(new VariantUp.Clone("", [])),
                        CancellationToken.None
                    )
            ),
        };

        foreach (var (name, entry) in entries)
        {
            var refused = await entry.Should().ThrowAsync<ArgumentException>();
            Logger.LogInformation(
                "Study {Study}, store {Store}: {Entry} refused with {Refusal}",
                study,
                store,
                name,
                refused.Which.Message
            );
            refused.WithMessage(refusal);
        }
        host.Requests.Should().BeEmpty();
        gateway.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Variant_up_needs_a_fully_pinned_manifest_and_an_experiment()
    {
        var manifest = Variant("b-007")[3];
        string[] command =
        [
            "variant",
            "up",
            "--manifest",
            manifest,
            "--from-workspace",
            "base",
            "--champion",
            S_champion,
        ];
        var noExperiment = () => CliOptions.Parse(command);
        var unpinned = () =>
            VariantUp.RunCoreAsync(
                CliOptions.Parse([.. command, "--experiment", "exp-1", "--store", _files, "--execute"]),
                null!,
                (_, _, _, _) => Task.FromResult(""),
                (_, _, _) => Task.FromResult(new VariantUp.Clone("", [])),
                CancellationToken.None
            );

        // An arm is never built outside an experiment record, nor from a manifest that leaves a layer unpinned.
        noExperiment.Should().Throw<ArgumentException>().WithMessage("*--experiment*");
        (await unpinned.Should().ThrowAsync<ArgumentException>()).WithMessage(
            "Variant manifest b-007 must pin reviewRepo.directory, reviewRepo.sha, plugins.alias, plugins.url, "
                + "plugins.sha, mode.name, mode.file for variant up."
        );
    }

    [Fact]
    public void A_variant_needs_shadow()
    {
        var parse = () => CliOptions.Parse([.. RunArguments, "--variant", "kb-h1"]);

        parse.Should().Throw<ArgumentException>().WithMessage("--variant names a shadow arm and needs --shadow.");
    }

    [Fact]
    public async Task A_shadow_run_refuses_GitHub_before_touching_anything()
    {
        var handler = new FakeHttpMessageHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        using var config = new ReviewdConfig(new ConfigurationBuilder().Build());
        var options = CliOptions.Parse([
            "review",
            "run",
            "--repo",
            "owner/repo",
            "--pr",
            "17",
            "--slot",
            "2",
            "--db",
            _database,
            "--session",
            "session",
            "--workspace",
            "workspace",
            "--provider",
            "test",
            "--shadow",
            "--post-session",
            "read-only",
            "--execute",
        ]);

        var run = () =>
            ReviewRunner.RunAsync(
                options,
                config,
                new LmStreamingS2SClient(http, null, null, null),
                (_, _, _, _) => throw new InvalidOperationException("sandbox must not be reached"),
                CancellationToken.None,
                _ => Task.CompletedTask,
                () => Now
            );

        (await run.Should().ThrowAsync<ArgumentException>()).WithMessage("--shadow supports Azure DevOps*");
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("kb-h1", true)]
    [InlineData("a", true)]
    [InlineData("0skill-edit-2", true)]
    [InlineData("KB", false)]
    [InlineData("-lead", false)]
    [InlineData("has space", false)]
    [InlineData("exp/kb", false)]
    [InlineData("a1234567890123456789012345678901234567890", false)]
    public void Variant_labels_are_short_lowercase_slugs(string label, bool valid)
    {
        foreach (var command in new[] { RunArguments, ["review", "batch"] })
        {
            var parse = () =>
                CliOptions.Parse([
                    .. command,
                    "--shadow",
                    "--post-session",
                    "read-only",
                    "--variant",
                    label,
                    "--manifest",
                    "m.json",
                    "--experiment",
                    "e1",
                    "--store",
                    "s",
                ]);
            if (valid)
            {
                parse().Get("variant").Should().Be(label);
            }
            else
            {
                parse.Should().Throw<ArgumentException>().WithMessage("--variant*");
            }
        }
    }

    [Fact]
    public void Shadow_is_a_flag_that_takes_no_value()
    {
        var options = CliOptions.Parse([.. RunArguments, "--shadow", "--post-session", "read-only", "--execute"]);

        options.Has("shadow").Should().BeTrue();
        options.Has("execute").Should().BeTrue();
    }

    private static readonly string[] RunArguments =
    [
        "review",
        "run",
        "--repo",
        "org/project/Nova",
        "--pr",
        "17",
        "--db",
        "x.db",
        "--session",
        "session",
        "--workspace",
        "workspace",
        "--provider",
        "test",
    ];

    [SkippableFact]
    public async Task Would_post_verification_needs_this_revision_and_exactly_one_well_formed_summary()
    {
        var python = await ReviewdSummaryReplyTests.FindPythonAsync();
        Skip.If(python is null, "no Python interpreter on PATH, so the sandbox's would-post rule cannot be run");
        static JsonObject Record(params JsonObject[] comments) =>
            new()
            {
                ["prId"] = "17",
                ["headSha"] = "h",
                ["mergeSha"] = "m",
                ["comments"] = new JsonArray([.. comments]),
            };
        static JsonObject Comment(string kind, string content = "text", string? file = null, JsonNode? line = null) =>
            new()
            {
                ["kind"] = kind,
                ["content"] = content,
                ["filePath"] = file,
                ["line"] = line,
            };
        var summary = Comment("summary");
        var wrongHead = Record(summary.DeepClone().AsObject());
        wrongHead["headSha"] = "other";
        var badReply = Comment("reply");
        badReply["threadId"] = -1;
        (string Name, JsonObject Record, bool Passes)[] cases =
        [
            ("summary only", Record(summary.DeepClone().AsObject()), true),
            (
                "summary, inline and reply",
                Record(summary.DeepClone().AsObject(), Comment("inline", file: "/a.cs", line: 3), Comment("reply")),
                true
            ),
            ("file-level inline", Record(summary.DeepClone().AsObject(), Comment("inline", file: "/a.cs")), true),
            ("another head", wrongHead, false),
            ("no comments", Record(), false),
            ("no summary", Record(Comment("inline", file: "/a.cs", line: 3)), false),
            ("two summaries", Record(summary.DeepClone().AsObject(), Comment("summary")), false),
            ("inline without a file", Record(summary.DeepClone().AsObject(), Comment("inline", line: 3)), false),
            (
                "inline on line 0",
                Record(summary.DeepClone().AsObject(), Comment("inline", file: "/a.cs", line: 0)),
                false
            ),
            ("unknown kind", Record(summary.DeepClone().AsObject(), Comment("vote")), false),
            ("blank summary", Record(Comment("summary", content: "  ")), false),
            ("negative thread id", Record(summary.DeepClone().AsObject(), badReply), false),
            // Commit sets aside any file over 1 MiB, so a record that size would pass verify and then go missing.
            ("over 1 MiB", Record(Comment("summary", content: new string('x', 1024 * 1024))), false),
        ];

        var results = await ReviewdSummaryReplyTests.RunAsync(
            python!,
            ["verify_would_post", "ARCHIVE_FILE_LIMIT"],
            "passes(lambda: verify_would_post(json.dumps(c), 17, 'h', 'm'))",
            new JsonArray([.. cases.Select(c => c.Record)]).ToJsonString()
        );

        cases
            .Zip(results, (c, passed) => (c.Name, Passed: passed))
            .Should()
            .Equal(cases.Select(c => (c.Name, Passed: c.Passes)));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ReviewdSqlitePools.Clear(_database);
            if (File.Exists(_database))
            {
                File.Delete(_database);
            }
            if (Directory.Exists(_files))
            {
                // git writes its objects read-only, and Windows will not delete a read-only file.
                foreach (var file in Directory.EnumerateFiles(_files, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(_files, recursive: true);
            }
        }
        base.Dispose(disposing);
    }

    // Words that name the arm in ArmReceipt: none may reach the judge.
    private static readonly string[] S_armLabels =
    [
        "shadow",
        "b-007",
        "exp-1",
        "arm-provider-x",
        "arm-post",
        "arm-session",
    ];

    /// <summary>A verified shadow review's receipt, with every field that names its arm.</summary>
    private static JsonObject ArmReceipt(JsonObject wouldPost, string commit) =>
        new()
        {
            ["repository"] = Repo.DisplayName,
            ["prId"] = "17",
            ["slot"] = 2,
            ["workspaceId"] = "workspace",
            ["sessionId"] = "arm-session",
            ["providerId"] = "arm-provider-x",
            ["reasoningEffort"] = "high",
            ["headSha"] = Head,
            ["reviewedHeadSha"] = Head,
            ["mergeSha"] = new string('e', 40),
            ["startedAt"] = "2026-10-07T20:00:00Z",
            ["finishedAt"] = "2026-10-07T21:00:00Z",
            ["stage"] = "completed-verified",
            ["threadId"] = "thread-arm",
            ["shadow"] = true,
            ["postSessionId"] = "arm-post",
            ["variant"] = "b-007",
            ["provenance"] = new JsonObject
            {
                ["provider"] = "arm-provider-x",
                ["experimentId"] = "exp-1",
                ["taskPromptSha256"] = new string('7', 64),
            },
            ["initialPrompt"] = "You are the SHADOW reviewer of arm b-007.",
            ["reviewResponse"] = "Shadow review done.",
            ["confirmationResponse"] = "verdict: APPROVE",
            ["wouldPost"] = wouldPost.DeepClone(),
            ["artifactCommit"] = new JsonObject
            {
                ["status"] = "committed",
                ["commit"] = commit,
                ["branch"] = "review/nova-17",
                ["reviewDirectory"] = "PRs/nova-17",
            },
            ["prepared"] = new JsonObject
            {
                ["mergeSha"] = new string('e', 40),
                ["reviewDirectory"] = "PRs/nova-17",
                ["baselineComments"] = new JsonArray(),
            },
        };

    // A shadow review is judged from what it would have posted, blind to its arm: judge run sends judge-inputs the
    // review's verified would-post record, its receipts cut to their neutral fields, and the would-post prompt section.
    // The arm's agent could edit the store that phase runs git in, so only a read-only session may run it.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_shadow_review_is_judged_blind_and_only_in_a_read_only_session(bool readOnly)
    {
        var store = Path.Combine(Path.GetTempPath(), $"reviewd-tokens-{Guid.NewGuid():N}");
        Directory.CreateDirectory(store);
        try
        {
            if (readOnly)
            {
                await ReadOnlySessions.MarkAsync(store, "judge-session", "workspace", [Repo.DisplayName], default);
            }
            var wouldPost = new JsonObject
            {
                ["file"] = "would_post.json",
                ["comments"] = new JsonArray(
                    new JsonObject { ["kind"] = "summary", ["contentSha256"] = new string('c', 64) }
                ),
            };
            await PutAsync($"reviewd/review-run/{Repo.NormalizedKey}/17", ArmReceipt(wouldPost, new string('d', 40)));
            var retried = ArmReceipt(wouldPost, new string('d', 40));
            retried["stage"] = "failed-released";
            retried["threadId"] = "thread-old";
            await PutAsync("reviewd/attempt-history/0123456789abcdef", retried);
            var host = new FakeHttpMessageHandler()
                .OnJson(
                    HttpMethod.Get,
                    "api/workspaces/workspace",
                    """{"id":"workspace","name":"nova","directoryRelPath":"workspace","marketplaces":[]}"""
                )
                .OnJson(HttpMethod.Get, "api/providers", """{"providers":[{"id":"gpt-6.1-sol","available":true}]}""")
                .OnJson(HttpMethod.Get, "api/chat-modes/workspace-agent", "{}")
                .OnJson(HttpMethod.Get, "api/conversations/thread-arm/usage", "{}", HttpStatusCode.NotFound)
                .OnJson(HttpMethod.Get, "api/conversations/thread-old/usage", "{}", HttpStatusCode.NotFound)
                .OnCurrentReviewHostCapabilities();
            var gateway = new ScriptedSandboxGateway
            {
                WorkspaceRelativePath = "workspace",
                CommandStdout = """
                    {"status":"ok","n":1,"judgmentRel":"PRs/nova-17/judgment-1","summaryMd":"summary",
                     "before":{"path":"PRs/nova-17/judgment-1/before.json","sha256":"b"}}
                    """,
            };
            using var config = HostConfig(store, new HostAndGateway(host, gateway));
            string[] arguments =
            [
                "judge",
                "run",
                "--repo",
                Repo.DisplayName,
                "--pr",
                "17",
                "--db",
                _database,
                "--session",
                "judge-session",
                "--workspace",
                "workspace",
                "--provider",
                "gpt-6.1-sol",
                "--mode",
                "workspace-agent",
            ];

            var exit = await JudgeRunner.RunAsync(CliOptions.Parse(arguments), config, default);

            Logger.LogDebug(
                "judge run exit {Exit}, read-only {ReadOnly}, submits to {Sessions}",
                exit,
                readOnly,
                string.Join(",", gateway.SubmitSessions)
            );
            if (!readOnly)
            {
                exit.Should().Be(1);
                gateway.SubmitSessions.Should().BeEmpty("nothing runs where the arm could make git post");
                return;
            }
            exit.Should().Be(0);
            gateway.SubmitSessions.Should().Equal("judge-session");
            // The submit's args are ["-B", "-c", <script>, "judge-inputs", <args JSON>, <prompt template>].
            var argv = JsonNode.Parse(gateway.LastSubmitBody!)!["args"]!
                .AsArray()
                .Select(a => a!.GetValue<string>())
                .ToList();
            var json = argv[argv.IndexOf("judge-inputs") + 1];
            var template = argv[^1];
            Logger.LogDebug("judge-inputs args {Args}", json);
            foreach (var label in S_armLabels)
            {
                json.Should().NotContainEquivalentOf(label);
                template.Should().NotContainEquivalentOf(label);
            }
            var args = JsonNode.Parse(json)!;
            JsonNode.DeepEquals(args["wouldPost"], wouldPost).Should().BeTrue("judge-inputs checks ours against it");
            args["host"]!["receipt"]!
                .AsObject()
                .Select(field => field.Key)
                .Should()
                .BeEquivalentTo(
                    "repository",
                    "prId",
                    "slot",
                    "headSha",
                    "reviewedHeadSha",
                    "mergeSha",
                    "startedAt",
                    "finishedAt",
                    "threadId",
                    "stage",
                    "artifactCommit",
                    "prepared"
                );
            args["host"]!["attemptHistory"]![0]!["threadId"]!.GetValue<string>().Should().Be("thread-old");
            template.Should().Contain("## 12. A would-post review");

            // An executed run records the judgment as a shadow one, so judge close refuses it. The section has its own
            // identity field: the base template's sha, which judge batch matches, stays the embedded prompt's.
            // (The scripted host cannot provision a judge, so the run then fails; the receipt is written before.)
            await JudgeRunner.RunAsync(CliOptions.Parse([.. arguments, "--execute"]), config, default, leaseHeld: true);

            var judgment = await ReceiptAsync($"reviewd/judge-run/{Repo.NormalizedKey}/17/1");
            Logger.LogDebug("judgment receipt {Receipt}", judgment.ToJsonString());
            judgment["shadow"]!.GetValue<bool>().Should().BeTrue();
            judgment["identity"]!["promptTemplateSha256"]!
                .GetValue<string>()
                .Should()
                .Be(await JudgeRunner.EmbeddedPromptSha256Async(default));
            judgment["identity"]!["wouldPostPromptSha256"]!
                .GetValue<string>()
                .Should()
                .Be(
                    Convert.ToHexStringLower(
                        SHA256.HashData(Encoding.UTF8.GetBytes(await ResourceAsync("merged-judge-would-post.md")))
                    )
                );
        }
        finally
        {
            Directory.Delete(store, recursive: true);
        }
    }

    [Theory]
    [InlineData("legacy", "completed", 0)]
    [InlineData("legacy-delegate", "completed", 0)]
    [InlineData("legacy-rejudge", "completed", 0)]
    [InlineData("pending-nudge", "capped", 1)]
    [InlineData("matching-pending-nudge", "completed", 0)]
    [InlineData("mismatched-pending-nudge", "failed", 1)]
    [InlineData("unconfirmed-freeze-false", "nudge-send-pending", 1)]
    [InlineData("unconfirmed-freeze-missing", "nudge-send-pending", 1)]
    [InlineData("configured-fallback-nudge", "nudge-send-pending", 1)]
    [InlineData("missing-fallback-capability-nudge", "nudge-send-pending", 1)]
    [InlineData("draft", "capped", 1)]
    [InlineData("missing-capability", "post-check", 1)]
    [InlineData("fallback", "failed", 1)]
    [InlineData("missing-usage", "failed", 1)]
    [InlineData("partial-final-usage", "failed", 1)]
    [InlineData("alternate-provider", "completed", 0)]
    public async Task A_judge_resume_checks_usage_and_model_before_spending_or_accepting_output(
        string scenario,
        string expectedStage,
        int expectedExit
    )
    {
        var legacy = scenario.StartsWith("legacy", StringComparison.Ordinal);
        var provider = legacy || scenario == "alternate-provider" ? "gpt-6.1-sol" : "gpt-6.2-sol";
        Directory.CreateDirectory(_files);
        var key = await SeedJudgeResumeAsync(scenario, provider);
        var unconfirmedFreeze = scenario.StartsWith("unconfirmed-freeze-", StringComparison.Ordinal);
        var servedModel =
            unconfirmedFreeze || scenario is "fallback" or "mismatched-pending-nudge" ? "gpt-6.2-astra" : provider;
        var usage = $$"""
            {"preferredCostMicros":120000,"completeness":"Complete","estimatedCostCompleteness":"Complete",
             "perModel":[{"modelId":"{{servedModel}}","attemptCount":1}]}
            """;
        var partial = usage.Replace(
            "\"estimatedCostCompleteness\":\"Complete\"",
            "\"estimatedCostCompleteness\":\"Partial\""
        );
        var host = JudgeResumeHost(scenario);

        JsonObject? savedBeforeFinalRead = null;
        if (scenario == "missing-usage")
        {
            var reads = 0;
            host.OnAsync(
                req =>
                    req.Method == HttpMethod.Get
                    && req.RequestUri!.AbsolutePath.EndsWith("/usage", StringComparison.Ordinal),
                async _ =>
                {
                    if (++reads == 2)
                        savedBeforeFinalRead = await ReceiptAsync(key);
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }
            );
        }
        else if (scenario == "partial-final-usage")
        {
            host.OnSequence(HttpMethod.Get, "/usage", (HttpStatusCode.OK, usage), (HttpStatusCode.OK, partial));
        }
        else
        {
            host.OnJson(HttpMethod.Get, "/usage", scenario is "pending-nudge" or "draft" ? partial : usage);
        }

        using var gateway = new ScriptedSandboxGateway
        {
            WorkspaceRelativePath = "workspace",
            Execute = (_, args) =>
                Task.FromResult(
                    args[3] switch
                    {
                        "judge-snapshot" => (0, """{"status":"ok","compare":"unchanged"}""", ""),
                        "judge-outputs" => (
                            0,
                            scenario == "draft"
                                ? """{"status":"ok","stage":"draft"}"""
                                : """{"status":"ok","stage":"final"}""",
                            ""
                        ),
                        _ => throw new InvalidOperationException($"Unexpected judge phase {args[3]}"),
                    }
                ),
        };
        using var config = HostConfig(_files, new HostAndGateway(host, gateway));
        var options = CliOptions.Parse([
            "judge",
            "run",
            "--repo",
            Repo.DisplayName,
            "--pr",
            "17",
            "--db",
            _database,
            "--session",
            "judge-session",
            "--workspace",
            "workspace",
            "--provider",
            legacy ? "gpt-6.2-sol" : provider,
            "--mode",
            "workspace-agent",
            "--execute",
            .. scenario == "legacy-rejudge" ? new[] { "--rejudge" } : [],
        ]);

        var exit = await JudgeRunner.RunAsync(options, config, default, leaseHeld: true);

        var saved = await ReceiptAsync(key);
        Logger.LogDebug("Judge scenario {Scenario} exit {Exit}: {Receipt}", scenario, exit, saved.ToJsonString());
        exit.Should().Be(expectedExit);
        saved["stage"]!.GetValue<string>().Should().Be(expectedStage);
        var posts = host.Requests.Where(r =>
            r.Method == HttpMethod.Post && r.Uri.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal)
        );
        if (scenario == "alternate-provider")
            JsonNode.Parse(posts.Should().ContainSingle().Subject.Body!)!["suppressSubAgentSpawning"]!
                .GetValue<bool>()
                .Should()
                .BeTrue();
        else if (scenario == "matching-pending-nudge")
            JsonNode.Parse(posts.Should().ContainSingle().Subject.Body!)!["idempotencyKey"]!
                .GetValue<string>()
                .Should()
                .Be("pending-nudge");
        else
            posts.Should().BeEmpty();
        if (scenario == "mismatched-pending-nudge")
        {
            host.CountRequests("/freeze").Should().Be(1);
            saved["frozenAt"].Should().NotBeNull();
            saved["error"]!.GetValue<string>().Should().Contain(servedModel);
        }
        if (unconfirmedFreeze)
        {
            host.CountRequests("/freeze").Should().Be(1);
            saved["frozenAt"].Should().BeNull();
            saved["finishedAt"].Should().BeNull();
        }
        if (legacy)
            host.CountRequests("/status?inputId=root-input").Should().BeGreaterThan(0);
        if (scenario == "fallback")
            saved["error"]!.GetValue<string>().Should().Contain(servedModel);
        if (scenario == "missing-usage")
        {
            savedBeforeFinalRead.Should().NotBeNull();
            savedBeforeFinalRead!["judgeUsage"]!["measured"]!.GetValue<bool>().Should().BeFalse();
        }
    }

    private async Task<string> SeedJudgeResumeAsync(string scenario, string provider)
    {
        const string artifact = "dddddddddddddddddddddddddddddddddddddddd";
        var stage = scenario switch
        {
            "pending-nudge"
            or "matching-pending-nudge"
            or "mismatched-pending-nudge"
            or "unconfirmed-freeze-false"
            or "unconfirmed-freeze-missing"
            or "configured-fallback-nudge"
            or "missing-fallback-capability-nudge" => "nudge-send-pending",
            "alternate-provider" => "thread-created",
            _ => "post-check",
        };
        var review = ArmReceipt([], artifact);
        review.Remove("shadow");
        await PutAsync($"reviewd/review-run/{Repo.NormalizedKey}/17", review);
        var identity = new JsonObject
        {
            ["reviewArtifactCommit"] = artifact,
            ["reviewedHeads"] = new JsonArray(JsonValue.Create(Head)),
            ["finalHeadSha"] = Head,
            ["promptTemplateSha256"] = scenario.StartsWith("legacy", StringComparison.Ordinal)
                ? new string('e', 64)
                : await JudgeRunner.EmbeddedPromptSha256Async(default),
            ["provider"] = provider,
            ["effort"] = "xhigh",
            ["mode"] = "workspace-agent",
        };
        if (!scenario.StartsWith("legacy", StringComparison.Ordinal))
            identity["sendContractVersion"] = 2;
        var key = $"reviewd/judge-run/{Repo.NormalizedKey}/17/1";
        await PutAsync(
            key,
            new JsonObject
            {
                ["stage"] = stage,
                ["identity"] = identity,
                ["repository"] = Repo.DisplayName,
                ["prId"] = "17",
                ["n"] = 1,
                ["startedAt"] = Now,
                ["judgeThreadId"] = "thread-judge",
                ["judgeInputId"] = stage == "thread-created" ? null : "root-input",
                ["judgmentDir"] = "PRs/nova-17/judgment-1",
                ["nudge"] = stage == "nudge-send-pending" ? new JsonObject { ["key"] = "pending-nudge" } : null,
                ["ado"] = new JsonObject
                {
                    ["projectId"] = "project",
                    ["repositoryId"] = "repo",
                    ["beforePath"] = "before.json",
                    ["beforeSha256"] = "before-sha",
                },
            }
        );
        return key;
    }

    private static FakeHttpMessageHandler JudgeResumeHost(string scenario)
    {
        var host = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "api/workspaces/workspace", """{"id":"workspace","directoryRelPath":"workspace"}""")
            .OnJson(HttpMethod.Get, "/status?", """{"status":"completed","runId":"run-1"}""")
            .OnJson(
                HttpMethod.Get,
                "/run-state",
                """{"isInProgress":false}""",
                scenario is "pending-nudge" or "draft" ? HttpStatusCode.NotFound : HttpStatusCode.OK
            )
            .OnJson(
                HttpMethod.Post,
                "/freeze",
                scenario switch
                {
                    "unconfirmed-freeze-false" => """{"frozen":false}""",
                    "unconfirmed-freeze-missing" => "{}",
                    _ => """{"frozen":true}""",
                }
            )
            .OnJson(HttpMethod.Put, "api/conversations/thread-judge/metadata", "{}")
            .OnJson(
                HttpMethod.Get,
                "/subagents?",
                scenario == "legacy-delegate"
                    ? """{"schemaVersion":1,"nodes":[{"agentId":"delegate","threadId":"thread-delegate","parentThreadId":"thread-judge","depth":1,"template":"blind-reviewer","status":"completed","effectiveModelId":"gpt-6.1-sol"}]}"""
                    : """{"schemaVersion":1,"nodes":[]}"""
            )
            .OnJson(
                HttpMethod.Post,
                "/messages",
                """{"inputId":"root-input","idempotencyKeyHonored":true,"spawningSuppressed":true}""",
                HttpStatusCode.Accepted
            );
        return scenario == "missing-capability"
                ? host.OnJson(
                    HttpMethod.Get,
                    "/capabilities",
                    """{"messageIdempotency":true,"rootReasoningEffort":true,"spawnSuppression":true}"""
                )
            : scenario == "configured-fallback-nudge"
                ? host.OnJson(
                    HttpMethod.Get,
                    "/capabilities",
                    """{"schemaVersion":1,"messageIdempotency":true,"spawnSuppression":true,"rootReasoningEffort":true,"responsesCacheWritePricing":true,"modelFallbackSourceProviders":["gpt-6.2-sol"]}"""
                )
            : scenario == "missing-fallback-capability-nudge"
                ? host.OnJson(
                    HttpMethod.Get,
                    "/capabilities",
                    """{"schemaVersion":1,"messageIdempotency":true,"spawnSuppression":true,"rootReasoningEffort":true,"responsesCacheWritePricing":true}"""
                )
            : host.OnCurrentReviewHostCapabilities();
    }

    // judge-inputs over a real store and scripted ADO GETs: ours is the would_post.json at the review's own artifact
    // commit, checked against its verified record, never a publication receipt on another review branch or on main; the
    // live reviewer's [Revobot] comment is context of its own class; and no input file names the arm.
    [SkippableFact]
    public async Task Judge_inputs_of_a_would_post_review_give_its_comments_and_never_name_the_arm()
    {
        var python = await ReviewdSummaryReplyTests.FindPythonAsync();
        Skip.If(python is null, "no python3 on PATH");
        var root = Path.Combine(_files, "store");
        var comments = new (string Kind, string Content, string? File, int? Line, int? Thread)[]
        {
            ("summary", "Two issues: see inline.", null, null, null),
            ("inline", "x can be null here.", "/src/A.cs", 12, null),
            ("reply", "Thanks, the fix covers it.", null, null, 102),
        };
        var record = new JsonObject
        {
            ["prId"] = 17,
            ["headSha"] = Head,
            ["mergeSha"] = new string('e', 40),
            ["comments"] = new JsonArray([
                .. comments.Select(c =>
                    (JsonNode)
                        new JsonObject
                        {
                            ["kind"] = c.Kind,
                            ["content"] = c.Content,
                            ["filePath"] = c.File,
                            ["line"] = c.Line,
                            ["threadId"] = c.Thread,
                        }
                ),
            ]),
        };
        JsonObject Verified(int tampered = 0) =>
            new()
            {
                ["file"] = "would_post.json",
                ["comments"] = new JsonArray([
                    .. comments.Select(
                        (c, i) =>
                            (JsonNode)
                                new JsonObject
                                {
                                    ["kind"] = c.Kind,
                                    ["filePath"] = c.File,
                                    ["line"] = c.Line,
                                    ["threadId"] = c.Thread,
                                    ["contentSha256"] = Convert.ToHexStringLower(
                                        SHA256.HashData(
                                            Encoding.UTF8.GetBytes(c.Content + (i + 1 == tampered ? "!" : ""))
                                        )
                                    ),
                                }
                    ),
                ]),
            };
        // main and a live round's branch hold publication receipts naming the live reviewer's PR comment 101/1; the
        // arm's own branch holds would_post.json.
        Directory.CreateDirectory(Path.Combine(root, "PRs", "nova-17"));
        await GitAsync(root, "init", "-q", "-b", "main");
        await GitAsync(root, "config", "user.email", "t@example.com");
        await GitAsync(root, "config", "user.name", "t");
        await GitAsync(root, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(root, "README.md"), "store\n");
        await GitAsync(root, "add", "-A");
        await GitAsync(root, "commit", "-q", "-m", "base");
        await GitAsync(root, "checkout", "-q", "-b", "review/nova-17");
        File.WriteAllText(Path.Combine(root, "PRs", "nova-17", "would_post.json"), record.ToJsonString());
        File.WriteAllText(Path.Combine(root, "PRs", "nova-17", "review.md"), "# Review\n");
        await GitAsync(root, "add", "-A");
        await GitAsync(root, "commit", "-q", "-m", "arm review");
        var artifact = await GitAsync(root, "rev-parse", "HEAD");
        await GitAsync(root, "checkout", "-q", "main");
        Directory.CreateDirectory(Path.Combine(root, "PRs", "nova-17"));
        var live =
            $$"""{"prId":17,"headSha":"{{Head}}","comments":[{"threadId":101,"commentId":1,"kind":"summary"}]}""";
        File.WriteAllText(Path.Combine(root, "PRs", "nova-17", "publication_receipts.json"), live);
        await GitAsync(root, "add", "-A");
        await GitAsync(root, "commit", "-q", "-m", "live review");
        await GitAsync(root, "branch", "review/nova-17-abcdef12");

        var receipt = ArmReceipt(Verified(), artifact);
        var usage = JsonNode.Parse(
            """
            [{"threadId":"thread-arm","foundVia":["receipt:latest"],"usage":{"preferredCostMicros":1230000,"totalTokens":1000,
              "completeness":2,"perModel":[{"modelId":"arm-provider-x-large","preferredCostMicros":1230000,"totalTokens":1000}]}}]
            """
        )!;
        JsonObject Args(int minN, string nonce, JsonObject verified) =>
            new()
            {
                ["org"] = "org",
                ["project"] = "project",
                ["repo"] = "Nova",
                ["pr"] = "17",
                ["minN"] = minN,
                ["nonce"] = nonce,
                ["dryRun"] = false,
                ["host"] = new JsonObject
                {
                    ["receiptKey"] = $"reviewd/review-run/{Repo.NormalizedKey}/17",
                    ["receipt"] = JudgeRunner.Blind(receipt),
                    ["attemptHistory"] = new JsonArray(),
                    ["threads"] = usage.DeepClone(),
                    ["priorJudgments"] = new JsonArray(),
                },
                ["wouldPost"] = verified,
            };
        var template =
            await ResourceAsync("merged-judge.md") + "\n\n" + await ResourceAsync("merged-judge-would-post.md");

        var built = await JudgeInputsAsync(python!, root, Args(1, new string('1', 32), Verified()), template);
        var tampered = await JudgeInputsAsync(python!, root, Args(2, new string('2', 32), Verified(2)), template);

        built.Exit.Should().Be(0, built.Error);
        var inputs = Path.Combine(root, "PRs", "nova-17", "judgment-1", "inputs");
        foreach (var file in Directory.EnumerateFiles(inputs, "*", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (var label in S_armLabels)
            {
                text.Should().NotContainEquivalentOf(label, $"{Path.GetFileName(file)} must not name the arm");
            }
        }
        var packet = JsonNode.Parse(File.ReadAllText(Path.Combine(inputs, "packet.json")))!;
        var threads = File.ReadAllText(Path.Combine(inputs, "threads.md")).ReplaceLineEndings("\n");
        Logger.LogDebug("packet ours {Ours}; threads.md {Threads}", packet["ours"]!.ToJsonString(), threads);
        packet["ours"]!["mode"]!.GetValue<string>().Should().Be("would-post");
        packet["ours"]!["posted"]!.AsArray().Should().BeEmpty("no comment on the PR is ours");
        packet["ours"]!["wouldPost"]!
            .AsArray()
            .Select(e => $"{e!["id"]} {e["kind"]}")
            .Should()
            .Equal("wp/1 summary", "wp/2 inline", "wp/3 reply");
        packet["review"]!["rounds"]!
            .AsArray()
            .Select(r => $"{r!["branch"]} {r["tip"]}")
            .Should()
            .Equal($"review/nova-17 {artifact}");
        packet["grading"]!["notApplicable"]!
            .AsObject()
            .Select(p => p.Key)
            .Should()
            .Equal("threadHelp", "fixRate", "developerBehaviors");
        packet["cost"]!["threads"]![0]!.AsObject().ContainsKey("perModel").Should().BeFalse();
        // The per-comment rows the judge queries say the same: the live reviewer's comment is not ours.
        packet["ado"]!["threads"]!
            .AsArray()
            .SelectMany(t =>
                t!["comments"]!
                    .AsArray()
                    .Select(c =>
                        $"{t["threadId"]}/{c!["commentId"]} {c["ours"]} {c["operatorAuthored"]?.GetValue<string>()}"
                    )
            )
            .Should()
            .Equal("101/1 false live-reviewer", "101/2 false operator-manual", "102/1 false ");
        threads
            .Split('\n')
            .Where(line =>
                line.StartsWith("wp/", StringComparison.Ordinal) || line.StartsWith("101/", StringComparison.Ordinal)
            )
            .Should()
            .Equal(
                "wp/1 | summary | PR | Two issues: see inline.",
                "wp/2 | inline | /src/A.cs:12 | x can be null here.",
                "wp/3 | reply | thread 102 | Thanks, the fix covers it.",
                "101/1 | live-reviewer | active | PR | iter - | 2026-10-07T19:00Z | Op | [Revobot] live summary",
                "101/2 | operator | active | PR | iter - | 2026-10-07T19:05Z | Op | manual note"
            );
        JsonNode.Parse(File.ReadAllText(Path.Combine(inputs, "would-post.json")))!["comments"]!
            .AsArray()
            .Select(c => c!["content"]!.GetValue<string>())
            .Should()
            .Equal(comments.Select(c => c.Content));
        tampered.Exit.Should().Be(2);
        tampered.Error.Should().Contain("comment 2 differs from the review's verified record");
        Directory.Exists(Path.Combine(root, "PRs", "nova-17", "judgment-2")).Should().BeFalse("nothing is written");
    }

    [SkippableFact]
    public async Task Judge_inputs_take_a_live_rounds_revision_from_the_attempt_that_archived_it()
    {
        var python = await ReviewdSummaryReplyTests.FindPythonAsync();
        Skip.If(python is null, "no Python interpreter on PATH, so the judge input script cannot run");
        const string directory = "PRs/nova-17";
        const string branch = "review/nova-17";
        var root = Path.Combine(_files, "live-store");
        Directory.CreateDirectory(Path.Combine(root, directory));
        var staleHead = new string('b', 40);
        var staleMerge = new string('c', 40);
        var reviewedMerge = new string('e', 40);
        var published = new JsonArray(
            new JsonObject
            {
                ["threadId"] = 101,
                ["commentId"] = 1,
                ["kind"] = "summary",
            }
        );
        await File.WriteAllTextAsync(
            Path.Combine(root, directory, "publication_receipts.json"),
            new JsonObject
            {
                ["prId"] = "17",
                ["headSha"] = staleHead,
                ["mergeSha"] = staleMerge,
                ["comments"] = published.DeepClone(),
            }.ToJsonString()
        );
        await GitAsync(root, "init", "-q", "-b", "main");
        await GitAsync(root, "config", "user.email", "t@example.com");
        await GitAsync(root, "config", "user.name", "t");
        await GitAsync(root, "add", directory);
        await GitAsync(root, "commit", "-q", "-m", "Archived live review");
        await GitAsync(root, "branch", branch);
        var commit = await GitAsync(root, "rev-parse", "HEAD");
        var artifact = new JsonObject
        {
            ["commit"] = commit,
            ["branch"] = branch,
            ["reviewDirectory"] = directory,
        };
        var receipt = new JsonObject
        {
            ["repository"] = Repo.DisplayName,
            ["stage"] = "completed-verified",
            ["startedAt"] = "2026-10-07T18:30:00Z",
            ["artifactCommit"] = artifact,
            ["verifiedPublication"] = new JsonObject { ["comments"] = published },
        };
        var result = await JudgeInputsAsync(
            python!,
            root,
            new JsonObject
            {
                ["pr"] = "17",
                ["minN"] = 1,
                ["nonce"] = new string('1', 32),
                ["dryRun"] = false,
                ["host"] = new JsonObject
                {
                    ["receiptKey"] = $"reviewd/review-run/{Repo.NormalizedKey}/17",
                    ["receipt"] = receipt,
                    ["attemptHistory"] = new JsonArray(
                        new JsonObject
                        {
                            ["key"] = "reviewd/attempt-history/matching",
                            ["artifactCommit"] = artifact.DeepClone(),
                            ["headSha"] = Head,
                            ["mergeSha"] = reviewedMerge,
                        }
                    ),
                    ["threads"] = new JsonArray(),
                    ["priorJudgments"] = new JsonArray(),
                },
            },
            await ResourceAsync("merged-judge.md")
        );

        result.Exit.Should().Be(0, result.Error);
        var packet = JsonNode.Parse(
            await File.ReadAllTextAsync(Path.Combine(root, directory, "judgment-1/inputs/packet.json"))
        )!;
        var round = packet["review"]!["rounds"]!.AsArray().Single()!;
        round["artifactCommit"]!.GetValue<string>().Should().Be(commit);
        round["mergeSha"]!.GetValue<string>().Should().Be(reviewedMerge);
        round["headSha"]!.GetValue<string>().Should().Be(Head);
        round["iteration"]!.GetValue<int>().Should().Be(1);
    }

    private static async Task<string> ResourceAsync(string name)
    {
        using var stream = typeof(JudgeRunner).Assembly.GetManifestResourceStream($"Reviewd.Sample.Prompts.{name}")!;
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private static async Task<(int Exit, string Output, string Error)> RunGitAsync(
        string directory,
        params string[] arguments
    )
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, (await output).Trim(), (await error).Trim());
    }

    private static async Task<string> GitAsync(string directory, params string[] arguments)
    {
        var (exit, output, error) = await RunGitAsync(directory, arguments);
        exit.Should().Be(0, error);
        return output;
    }

    /// <summary>
    ///     Runs the embedded JudgeSandbox.py's judge-inputs in <paramref name="root" /> with <paramref name="args" />,
    ///     its ADO GETs answered from <see cref="AdoFixture" />.
    /// </summary>
    private static async Task<(int Exit, string Output, string Error)> JudgeInputsAsync(
        string python,
        string root,
        JsonObject args,
        string template
    )
    {
        var script = Path.Combine(Path.GetTempPath(), $"judge-sandbox-{Guid.NewGuid():N}.py");
        using (var resource = typeof(JudgeRunner).Assembly.GetManifestResourceStream("Reviewd.Sample.JudgeSandbox.py")!)
        using (var file = File.Create(script))
        {
            await resource.CopyToAsync(file);
        }
        try
        {
            const string harness = """
                import importlib.util, json, sys
                from pathlib import Path
                spec = importlib.util.spec_from_file_location("judge_sandbox", sys.argv[1])
                sandbox = importlib.util.module_from_spec(spec)
                spec.loader.exec_module(sandbox)
                data = json.loads(sys.stdin.read())

                class Transport:
                    def request(self, method, path):
                        assert method == "GET", method
                        for key, body in data["ado"]:
                            if key in path:
                                return body
                        raise AssertionError("unscripted GET " + path)

                    def get_pages(self, path):
                        return self.request("GET", path)

                sandbox.ado = lambda: (None, Transport())
                sandbox.phase, sandbox.args, sandbox.template = "judge-inputs", data["args"], data["template"]
                sandbox.root = Path(sys.argv[2]).resolve()
                sandbox.judge_inputs()
                """;
            var start = new ProcessStartInfo(python)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                WorkingDirectory = root,
            };
            foreach (var argument in new[] { "-I", "-c", harness, script, root })
            {
                start.ArgumentList.Add(argument);
            }
            using var process = Process.Start(start)!;
            await process.StandardInput.WriteAsync(
                new JsonObject
                {
                    ["args"] = args,
                    ["template"] = template,
                    ["ado"] = AdoFixture(),
                }.ToJsonString()
            );
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, await output, error);
        }
        finally
        {
            File.Delete(script);
        }
    }

    /// <summary>
    ///     The ADO GETs of a completed Nova !17 reviewed at <see cref="Head" />, as (path fragment, response) pairs:
    ///     a live [Revobot] summary and an operator note in thread 101, an author's fix comment in 102.
    /// </summary>
    private static JsonArray AdoFixture() =>
        JsonNode
            .Parse(
                $$$"""
                [
                  ["pullRequests/17?includeWorkItemRefs", {"pullRequestId": 17, "status": "completed", "title": "Fix the filter",
                    "createdBy": {"displayName": "Ana", "uniqueName": "ana@example.com"},
                    "repository": {"id": "r-id", "name": "Nova", "project": {"id": "p-id", "name": "project"}},
                    "creationDate": "2026-10-07T18:00:00Z", "closedDate": "2026-10-08T09:00:00Z",
                    "sourceRefName": "refs/heads/fix", "targetRefName": "refs/heads/main",
                    "lastMergeSourceCommit": {"commitId": "{{{Head}}}"}, "lastMergeCommit": {"commitId": "ffffffffffffffffffffffffffffffffffffffff"},
                    "workItemRefs": []}],
                  ["/threads?", {"count": 2, "value": [
                    {"id": 101, "status": "active", "comments": [
                      {"id": 1, "author": {"id": "op-id", "displayName": "Op", "uniqueName": "op@example.com"},
                        "commentType": "text", "publishedDate": "2026-10-07T19:00:00Z", "content": "[Revobot] live summary"},
                      {"id": 2, "author": {"id": "op-id", "displayName": "Op", "uniqueName": "op@example.com"},
                        "commentType": "text", "publishedDate": "2026-10-07T19:05:00Z", "content": "manual note"}]},
                    {"id": 102, "status": "fixed", "threadContext": {"filePath": "/src/A.cs", "rightFileStart": {"line": 12}},
                      "comments": [{"id": 1, "author": {"id": "ana-id", "displayName": "Ana", "uniqueName": "ana@example.com"},
                        "commentType": "text", "publishedDate": "2026-10-07T21:30:00Z", "content": "Fixed the null check"}]}]}],
                  ["/iterations?", [{"id": 1, "sourceRefCommit": {"commitId": "{{{Head}}}"}, "createdDate": "2026-10-07T18:00:00Z",
                    "author": {"displayName": "Ana"}}]],
                  ["/commits?", []]
                ]
                """
            )!
            .AsArray();

    /// <summary>
    ///     A host config over <paramref name="store" />, with ADO (and, with <paramref name="github" />, GitHub) signed
    ///     in, as the CLI loads it. With a <paramref name="transport" />, its host and gateway clients send through it.
    /// </summary>
    private static ReviewdConfig HostConfig(string store, HttpMessageHandler? transport = null, bool github = false)
    {
        File.WriteAllText(Path.Combine(store, "msal-ado.bin"), "");
        var settings = new Dictionary<string, string?> { ["Auth:TokenStoreDir"] = store, ["Auth:Ado:ClientId"] = "c" };
        if (github)
        {
            settings["Auth:Github:ClientId"] = "g";
        }
        return new ReviewdConfig(new ConfigurationBuilder().AddInMemoryCollection(settings).Build())
        {
            Transport = transport,
        };
    }

    /// <summary>Sends gateway requests (<c>/api/v1/...</c>) to <paramref name="gateway" /> and the rest to <paramref name="host" />.</summary>
    private sealed class HostAndGateway(HttpMessageHandler host, HttpMessageHandler gateway) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _host = new(host, disposeHandler: false);
        private readonly HttpMessageInvoker _gateway = new(gateway, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) =>
            (
                request.RequestUri!.AbsolutePath.StartsWith("/api/v1/", StringComparison.Ordinal) ? _gateway : _host
            ).SendAsync(request, cancellationToken);
    }

    private static SandboxClient SandboxOver(HttpClient http) =>
        new(
            new SandboxClientOptions(
                new Uri("http://127.0.0.1:8080"),
                "app",
                "",
                TimeSpan.FromMinutes(10),
                TimeSpan.FromSeconds(30)
            ),
            http
        );

    /// <summary>Answers <c>POST /api/v1/sandboxes</c> with <paramref name="sessionId" /> and keeps the request body.</summary>
    private sealed class CreateGateway(string sessionId) : HttpMessageHandler
    {
        public string? CreateBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            if (request.Method != HttpMethod.Post || request.RequestUri!.AbsolutePath != "/api/v1/sandboxes")
            {
                return new HttpResponseMessage(HttpStatusCode.NotImplemented);
            }
            CreateBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    new JsonObject { ["session_id"] = sessionId, ["container_id"] = null }.ToJsonString(),
                    System.Text.Encoding.UTF8,
                    "application/json"
                ),
            };
        }
    }

    private static FakeHttpMessageHandler Host(
        string conversations = "[]",
        string liveTranscript = "[]",
        string status = """{"status":"Errored","runId":"run","response":null}"""
    ) =>
        new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/live/messages", liveTranscript)
            .OnJson(
                HttpMethod.Get,
                "/capabilities",
                """{"messageIdempotency":true,"spawnSuppression":true,"rootReasoningEffort":true,"conversationWorkingDirectory":true,"dedicatedSandbox":true}"""
            )
            .OnAsync(ReviewdRunnerTests.IsSandboxRelease, ReviewdRunnerTests.EchoSandboxReleaseAsync)
            .On(
                request =>
                    request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/conversations",
                _ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"threadId":"thread","readOnlyEgressAccepted":true,"readOnlyGitHubAccepted":true,"dedicatedSandboxAccepted":true}"""
                    ),
                }
            )
            .OnJson(HttpMethod.Get, "/api/conversations?", conversations)
            .OnJson(HttpMethod.Put, "/metadata", "{}")
            .OnJson(HttpMethod.Post, "/messages", """{"inputId":"input","idempotencyKeyHonored":true}""")
            .OnJson(HttpMethod.Get, "/status?", status)
            .OnJson(HttpMethod.Get, "/run-state", """{"isInProgress":false,"currentRunId":null}""")
            .OnJson(HttpMethod.Get, "/subagents?", """{"schemaVersion":1,"nodes":[]}""");

    // The commit the scripted review-repo clone has checked out: ArmManifest's pin unless a test moves it.
    private string _reviewRepoHead = new('2', 40);

    // The commit the scripted gateway resolves alias cr-b-007 to: ArmManifest's pin unless a test moves it.
    private string _aliasSha = new('3', 40);

    // How many alias lookups the scripted gateway leaves unanswered before it answers.
    private int _aliasTimeouts;

    // How many review-repo-head steps the scripted gateway leaves unanswered before it answers.
    private int _headTimeouts;

    // What an unanswered review-repo-head step or alias lookup raises instead of the default timeout, when set.
    private Func<Exception>? _pinReadFault;

    // How many times a run read the scripted gateway's marketplace aliases.
    private int _marketplaceReads;

    private async Task<int> RunAsync(
        FakeHttpMessageHandler handler,
        List<(string Phase, JsonObject Args)> phases,
        string[] extra,
        string postSession = "read-only",
        Action<string, string>? beforeStep = null,
        bool execute = true
    )
    {
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        // The scripted gateway lists alias cr-b-007 at _aliasSha, read when the run starts; the first _aliasTimeouts
        // lookups get no answer.
        var marketplaces = new JsonObject
        {
            ["marketplaces"] = new JsonArray(
                new JsonObject
                {
                    ["app_id"] = "codereview-daemon",
                    ["alias"] = "cr-b-007",
                    ["url"] = "https://github.com/gautam-achieveai/ClaudePlugins",
                    ["repo_key"] = "key",
                    ["ref"] = "main",
                    ["resolved_sha"] = _aliasSha,
                    ["created_at"] = "2026-11-18T00:00:00Z",
                    ["updated_at"] = "2026-11-18T00:00:00Z",
                    ["last_error"] = null,
                }
            ),
        }.ToJsonString();
        var gateway = new FakeHttpMessageHandler().On(
            request =>
                request.Method == HttpMethod.Get
                && request.RequestUri!.AbsolutePath.EndsWith("/apps/self/marketplaces", StringComparison.Ordinal),
            _ =>
            {
                _marketplaceReads++;
                return _aliasTimeouts-- > 0
                    ? throw _pinReadFault?.Invoke() ?? new HttpRequestException("No connection could be made.")
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(marketplaces) };
            }
        );
        using var config = new ReviewdConfig(new ConfigurationBuilder().Build()) { Transport = gateway };
        var options = CliOptions.Parse([
            "review",
            "run",
            "--repo",
            Repo.DisplayName,
            "--pr",
            "17",
            "--slot",
            "2",
            "--db",
            _database,
            "--session",
            "session",
            "--workspace",
            "workspace",
            "--provider",
            "test",
            "--max-age-hours",
            "72",
            .. execute ? new[] { "--execute" } : [],
            .. extra,
            .. extra.Contains("--shadow") ? new[] { "--post-session", postSession } : [],
        ]);
        Task<JsonObject> Sandbox(string phase, JsonObject args, string operation, CancellationToken token)
        {
            phases.Add((phase, args.DeepClone().AsObject()));
            beforeStep?.Invoke(phase, operation);
            if (phase == "review-repo-head" && _headTimeouts-- > 0)
            {
                throw _pinReadFault?.Invoke()
                    ?? new SandboxException(SandboxErrorKind.TransportTimeout, "The gateway did not answer in time.");
            }
            return Task.FromResult(
                phase switch
                {
                    "eligibility" => new JsonObject
                    {
                        ["prId"] = "17",
                        ["status"] = "active",
                        ["isDraft"] = false,
                        ["headSha"] = Head,
                        ["createdAt"] = Now.AddHours(-1).ToString("O"),
                    },
                    "prepare" => new JsonObject
                    {
                        ["mergeSha"] = new string('b', 40),
                        ["contextPath"] = "context",
                        ["reviewDirectory"] = "reviews",
                        ["baselineComments"] = new JsonArray(),
                    },
                    "review-repo-head" => new JsonObject
                    {
                        ["head"] = _reviewRepoHead,
                        ["knowledgeBaseTree"] = new string('5', 40),
                    },
                    "verify" => new JsonObject { ["file"] = "checked", ["comments"] = new JsonArray() },
                    "commit" => new JsonObject { ["status"] = "committed", ["commit"] = new string('c', 40) },
                    "cleanup" => new JsonObject
                    {
                        ["claimToken"] = args["claimToken"]!.DeepClone(),
                        ["cleanBaselineVerified"] = true,
                        ["evidencePreserved"] = true,
                    },
                    _ => [],
                }
            );
        }
        return await ReviewRunner.RunAsync(
            options,
            config,
            new LmStreamingS2SClient(http, null, null, null),
            Sandbox,
            CancellationToken.None,
            _ => Task.CompletedTask,
            () => Now
        );
    }

    // ArmManifest's mode file, as variant up provisioned it.
    private async Task WriteArmModeFileAsync()
    {
        Directory.CreateDirectory(_files);
        await File.WriteAllTextAsync(Path.Combine(_files, "mode.json"), """{"systemPrompt":"Review as b-007."}""");
    }

    private Task SnapshotAsync() =>
        PutAsync(
            $"reviewd/pr-snapshot/{Repo.NormalizedKey}",
            new JsonObject
            {
                ["17"] = new JsonObject
                {
                    ["Lifecycle"] = "Open",
                    ["IsDraft"] = false,
                    ["HeadSha"] = Head,
                },
            }
        );

    private async Task PutAsync(string key, JsonObject value)
    {
        using var connection = PrThreadBindings.Connection(_database);
        await new SqliteKvStore(connection).SetAsync(key, value);
    }

    private Task<JsonObject> AttemptAsync() => ReceiptAsync($"reviewd/review-attempt/{Repo.NormalizedKey}/17");

    private async Task<JsonObject> ReceiptAsync(string key)
    {
        using var connection = PrThreadBindings.Connection(_database);
        return (await new SqliteKvStore(connection).GetAsync<JsonObject>(key))!;
    }
}
