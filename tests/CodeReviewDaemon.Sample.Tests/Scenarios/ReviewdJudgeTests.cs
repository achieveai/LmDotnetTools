extern alias Reviewd;

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Reviewd::Reviewd.Sample;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

public sealed class ReviewdJudgeTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-22T13:17:58Z");

    // Recorded host state: judgment 6's root (thread-1792674410399-8995d601…) at its 13:17:58 freeze. Its
    // Agent call (13:14:09) got the spawn receipt only at 13:24:00, so the persisted tree was still empty.
    private static readonly (string Name, string Id)[] RecordedCalls =
    [
        ("Read", "call_kguSdeHbAe0LS2eU4wKYavDK"),
        ("Read", "call_vbeGQSPYFeg9WicZoDa6x6vS"),
        ("Bash", "call_92L1MH37s4Jnh1HDeblcfW7H"),
        ("Bash", "call_OVSoEanOjHGjpBt2wGCIkjK9"),
        ("Bash", "call_SrV7UhAb5S4ilFB4OW5WOIwK"),
        ("Bash", "call_lzqlRcEYMADjFYAxeZZIpIb0"),
        ("Bash", "call_zkpp7ORTe1zgxXQkBnowmM3X"),
        ("Skill", "call_klXGGhpo4FJh61cyLGE3VYGP"),
        ("Skill", "call_1yNREI0eAQijfAHIUp5w7KAG"),
        ("Bash", "call_BsNxtZYIjaDKa0Yxk5vlhezR"),
        ("Bash", "call_mkB17B8bSyPdr0QCd37ovvZt"),
        ("Bash", "call_EZ1AWIRP0GmjKADrjTfeHzz3"),
        ("Agent", "call_Xy3c3ylaK1olaszACcjbpmfa"),
    ];

    [Fact]
    public async Task A_frozen_judge_is_not_settled_while_a_spawn_has_no_result()
    {
        var atFreeze = await RootMessagesAsync(answered: RecordedCalls.Length - 1);
        JudgeRunner
            .StillRunning(rootActive: true, frozen: true, atFreeze, [], Now)
            .Should()
            .Contain("Agent call_Xy3c3ylaK1olaszACcjbpmfa");

        var spawned = await RootMessagesAsync(answered: RecordedCalls.Length);
        JudgeRunner
            .StillRunning(rootActive: true, frozen: true, spawned, [Child(ReviewSubAgentStatus.Running)], Now)
            .Should()
            .Contain("agent-1 Running");
        JudgeRunner
            .StillRunning(rootActive: true, frozen: true, spawned, [Child(ReviewSubAgentStatus.Completed)], Now)
            .Should()
            .BeNull();
    }

    [Fact]
    public async Task An_open_call_on_a_root_whose_run_ended_is_an_orphan_and_never_blocks()
    {
        // After a host restart the frozen loop is gone: no run, so the call can never get a result or spawn.
        var atFreeze = await RootMessagesAsync(answered: RecordedCalls.Length - 1);
        JudgeRunner.StillRunning(rootActive: false, frozen: true, atFreeze, [], Now).Should().BeNull();
        JudgeRunner.StillRunning(rootActive: true, frozen: false, null, [], Now).Should().NotBeNull();
    }

    // ---- judge run contracts

    [Fact]
    public async Task A_busy_sub_agent_tree_read_is_never_quiet()
    {
        var host = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/run-state", """{"isInProgress":false,"currentRunId":null}""")
            .OnJson(
                HttpMethod.Get,
                "/subagents?recursive=true",
                """{"code":"hierarchy_busy"}""",
                HttpStatusCode.ServiceUnavailable
            );

        var read = () => JudgeRunner.StillRunningAsync(Client(host), "thread-1", frozen: true, CancellationToken.None);

        await read.Should().ThrowAsync<LmStreamingS2SClient.HierarchyBusyException>();
    }

    // Recorded live (NovaClient !5790557): one host read past the 120 s HttpClient timeout ended judge run mid-wait
    // (rc 1) and left the judge running with no cap watcher. A host that does not answer now means "still running".
    private static readonly JudgeRunner.HostOutage FastOutage = new(
        TimeSpan.FromMilliseconds(1),
        TimeSpan.FromMilliseconds(4),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(10)
    );

    // Recorded live state of WeveNova 5788698's judge after the 23:53Z host restart: its ledger
    // row stays InProgress (the host marks it Interrupted only when that thread wakes, and nothing wakes it), the
    // host holds no run for it, and the sub-agent tree answers 503 hierarchy_busy. The wait never ended.
    [Fact]
    public async Task A_judge_run_a_host_restart_ended_is_noticed_and_the_wait_ends_orphaned()
    {
        var host = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/status", """{"status":"InProgress","runId":"3ebb3d75"}""")
            .OnJson(HttpMethod.Get, "/run-state", """{"isInProgress":false,"currentRunId":null}""")
            .OnJson(
                HttpMethod.Get,
                "/subagents?recursive=true",
                """{"code":"hierarchy_busy"}""",
                HttpStatusCode.ServiceUnavailable
            );

        var (outcome, status) = await WaitOnHost(
                Client(host),
                FastOutage with
                {
                    DeadRunAfter = TimeSpan.FromMilliseconds(50),
                }
            )
            .WaitAsync(TimeSpan.FromSeconds(5));

        outcome.Should().Be("orphaned");
        status!.Status.Should().Be("InProgress");
    }

    // The host accepted the judge input, then ran nothing for it on an idle, settled conversation (a restart between
    // accept and start). Past the never-started grace the wait ends "lost"; it does not tell the operator to rerun
    // into the same wait.
    [Fact]
    public async Task A_judge_input_the_host_never_starts_ends_the_wait_lost()
    {
        var host = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/status", """{"status":"NotStarted","runId":null}""")
            .OnJson(HttpMethod.Get, "/run-state", """{"isInProgress":false,"currentRunId":null}""")
            .OnJson(HttpMethod.Get, "/subagents?recursive=true", """{"schemaVersion":1,"nodes":[]}""");
        var now = DateTimeOffset.UtcNow;

        var (outcome, status) = await WaitOnHost(Client(host), FastOutage, clock: () => now += TimeSpan.FromMinutes(6))
            .WaitAsync(TimeSpan.FromSeconds(5));

        outcome.Should().Be("lost");
        status.Should().BeNull();
    }

    // A lost input never ran. No judge ran for a lost judge input, so it fails (no follow-up to a judge that never saw
    // its prompt); a lost follow-up ends, and post-check files what the judge already wrote. Once the input runs after
    // all, its own status decides.
    [Theory]
    [InlineData("NotStarted", false, "failed")]
    [InlineData("NotStarted", true, "ended")]
    [InlineData("InProgress", false, "running")]
    [InlineData("Completed", false, "completed")]
    public void A_lost_judge_input_fails_and_a_lost_follow_up_ends(string status, bool nudge, string expected) =>
        JudgeRunner.PostCheckState(status, orphaned: false, lost: true, nudge).Should().Be(expected);

    // The first send to a thread after a host restart can read Interrupted for a moment (the host's restart
    // pass marks the just-accepted input before its run starts; the run's own row then wins). The judge waits on.
    [Fact]
    public async Task An_input_that_reads_Interrupted_while_its_run_starts_is_waited_for_not_failed()
    {
        const string idle = """{"isInProgress":false,"currentRunId":null}""";
        var host = new FakeHttpMessageHandler()
            .OnSequence(
                HttpMethod.Get,
                "/status",
                (HttpStatusCode.OK, """{"status":"Interrupted","runId":"orphan-1"}"""),
                (HttpStatusCode.OK, """{"status":"Interrupted","runId":"orphan-1"}"""),
                (HttpStatusCode.OK, """{"status":"InProgress","runId":"run-2"}"""),
                (HttpStatusCode.OK, """{"status":"Completed","runId":"run-2","response":{"text":"final judgment"}}""")
            )
            .OnSequence(
                HttpMethod.Get,
                "/run-state",
                (HttpStatusCode.OK, idle),
                (HttpStatusCode.OK, idle),
                (HttpStatusCode.OK, """{"isInProgress":true,"currentRunId":"run-2"}"""),
                (HttpStatusCode.OK, idle)
            )
            .OnJson(HttpMethod.Get, "/subagents?recursive=true", """{"schemaVersion":1,"nodes":[]}""");

        var (outcome, status) = await WaitOnHost(Client(host), FastOutage);

        outcome.Should().Be("completed");
        status!.ResponseText.Should().Be("final judgment");
    }

    // The same moment of Interrupted, read after a host read that did not answer, keeps the judge waiting.
    [Fact]
    public async Task An_input_that_reads_Interrupted_after_a_dropped_host_read_is_waited_for_not_failed()
    {
        var host = new FakeHttpMessageHandler()
            .OnSequence(
                HttpMethod.Get,
                "/status",
                (HttpStatusCode.OK, """{"status":"Interrupted","runId":"orphan-1"}"""),
                (HttpStatusCode.OK, """{"status":"Interrupted","runId":"orphan-1"}"""),
                (HttpStatusCode.OK, """{"status":"InProgress","runId":"run-2"}"""),
                (HttpStatusCode.OK, """{"status":"Completed","runId":"run-2","response":{"text":"final judgment"}}""")
            )
            .OnSequence(
                HttpMethod.Get,
                "/run-state",
                (HttpStatusCode.ServiceUnavailable, "{}"),
                (HttpStatusCode.OK, """{"isInProgress":true,"currentRunId":"run-2"}"""),
                (HttpStatusCode.OK, """{"isInProgress":false,"currentRunId":null}""")
            )
            .OnJson(HttpMethod.Get, "/subagents?recursive=true", """{"schemaVersion":1,"nodes":[]}""");

        var (outcome, status) = await WaitOnHost(Client(host), FastOutage);

        outcome.Should().Be("completed");
        status!.ResponseText.Should().Be("final judgment");
    }

    // Quiet time alone never counts. A live root waits 45+ minutes on a sub-agent (seen 21:51-22:40) with its
    // run current; with no grace at all, the host holding the run still keeps the judge waiting.
    [Fact]
    public async Task A_quiet_root_whose_run_the_host_holds_is_never_taken_for_dead()
    {
        var host = new FakeHttpMessageHandler()
            .OnSequence(HttpMethod.Get, "/status", InProgress(reads: 30))
            .OnSequence(
                HttpMethod.Get,
                "/run-state",
                [
                    .. Enumerable.Repeat((HttpStatusCode.OK, """{"isInProgress":true,"currentRunId":"run-1"}"""), 30),
                    (HttpStatusCode.OK, """{"isInProgress":false,"currentRunId":null}"""),
                ]
            )
            .OnJson(HttpMethod.Get, "/subagents?recursive=true", """{"schemaVersion":1,"nodes":[]}""");

        var (outcome, status) = await WaitOnHost(Client(host), FastOutage with { DeadRunAfter = TimeSpan.Zero });

        outcome.Should().Be("completed");
        status!.ResponseText.Should().Be("final judgment");
    }

    // An accepted input that has not started yet has no run on a live host either (the
    // agent is still loading), so no run behind a NotStarted input proves nothing. Only a started run counts.
    [Fact]
    public async Task An_input_that_has_not_started_is_never_taken_for_dead()
    {
        const string idle = """{"isInProgress":false,"currentRunId":null}""";
        var host = new FakeHttpMessageHandler()
            .OnSequence(
                HttpMethod.Get,
                "/status",
                (HttpStatusCode.OK, """{"status":"NotStarted"}"""),
                (HttpStatusCode.OK, """{"status":"NotStarted"}"""),
                (HttpStatusCode.OK, """{"status":"InProgress","runId":"run-1"}"""),
                (HttpStatusCode.OK, """{"status":"Completed","runId":"run-1","response":{"text":"final judgment"}}""")
            )
            .OnSequence(
                HttpMethod.Get,
                "/run-state",
                (HttpStatusCode.OK, idle),
                (HttpStatusCode.OK, idle),
                (HttpStatusCode.OK, """{"isInProgress":true,"currentRunId":"run-1"}"""),
                (HttpStatusCode.OK, idle)
            )
            .OnJson(HttpMethod.Get, "/subagents?recursive=true", """{"schemaVersion":1,"nodes":[]}""");

        var (outcome, status) = await WaitOnHost(Client(host), FastOutage with { DeadRunAfter = TimeSpan.Zero });

        outcome.Should().Be("completed");
        status!.ResponseText.Should().Be("final judgment");
    }

    // The dead-run window counts only answered reads. A read the host did not answer starts it over, so an
    // outage between two "no run" reads is never taken as minutes of a dead run.
    [Fact]
    public async Task A_host_read_that_did_not_answer_starts_the_dead_run_window_over()
    {
        const string idle = """{"isInProgress":false,"currentRunId":null}""";
        var host = new FakeHttpMessageHandler()
            .OnSequence(HttpMethod.Get, "/status", InProgress(reads: 5))
            .OnSequence(
                HttpMethod.Get,
                "/run-state",
                (HttpStatusCode.OK, idle),
                (HttpStatusCode.ServiceUnavailable, "{}"),
                (HttpStatusCode.OK, idle),
                (HttpStatusCode.OK, """{"isInProgress":true,"currentRunId":"run-1"}"""),
                (HttpStatusCode.OK, idle)
            )
            .OnJson(HttpMethod.Get, "/subagents?recursive=true", """{"schemaVersion":1,"nodes":[]}""");

        var (outcome, status) = await WaitOnHost(
                Client(host),
                FastOutage with
                {
                    FirstBackoff = TimeSpan.FromMilliseconds(400),
                    DeadRunAfter = TimeSpan.FromMilliseconds(300),
                }
            )
            .WaitAsync(TimeSpan.FromSeconds(5));

        outcome.Should().Be("completed");
        status!.ResponseText.Should().Be("final judgment");
    }

    // A replay of Nova 5778641 (01:48-02:03Z): the host held the run through an 8-minute open Agent
    // call, did not answer while it restarted, then answered every read with no run behind the InProgress input. That
    // run was dead (it failed at 01:57:04 as the old host shut down), and the wait still ends orphaned.
    [Fact]
    public async Task A_run_the_host_held_then_lost_across_a_restart_is_noticed()
    {
        const string idle = """{"isInProgress":false,"currentRunId":null}""";
        const string held = """{"isInProgress":true,"currentRunId":"b9474739"}""";
        var host = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/status", """{"status":"InProgress","runId":"b9474739"}""")
            .OnSequence(
                HttpMethod.Get,
                "/run-state",
                (HttpStatusCode.OK, held),
                (HttpStatusCode.OK, held),
                (HttpStatusCode.ServiceUnavailable, "{}"),
                (HttpStatusCode.OK, idle)
            )
            .OnJson(HttpMethod.Get, "/subagents?recursive=true", """{"schemaVersion":1,"nodes":[]}""");

        var (outcome, status) = await WaitOnHost(
                Client(host),
                FastOutage with
                {
                    DeadRunAfter = TimeSpan.FromMilliseconds(50),
                }
            )
            .WaitAsync(TimeSpan.FromSeconds(5));

        outcome.Should().Be("orphaned");
        status!.RunId.Should().Be("b9474739");
    }

    // Post-check finishes a run a host restart ended from what the judge wrote, like a completed one: the
    // wait saw no run behind it (orphaned), or the host already marked it Interrupted (written only on a restart).
    [Theory]
    [InlineData("Completed", false, "completed")]
    [InlineData("InProgress", false, "running")]
    [InlineData("NotStarted", false, "running")]
    [InlineData("InProgress", true, "ended")]
    [InlineData("NotStarted", true, "ended")]
    [InlineData("Interrupted", false, "ended")]
    [InlineData("Errored", false, "failed")]
    [InlineData("Errored", true, "failed")]
    public void Post_check_finishes_a_run_a_host_restart_ended_from_what_it_wrote(
        string status,
        bool orphaned,
        string expected
    ) => JudgeRunner.PostCheckState(status, orphaned).Should().Be(expected);

    // The post-check's end. ADO first: a PR whose threads changed during the run, or no compare at all, is never filed
    // as a judgment; one follow-up at most.
    [Theory]
    [InlineData("completed", "changed", "final", false, "ado-changed", 3)]
    [InlineData("failed", "changed", null, false, "ado-changed", 3)]
    [InlineData("completed", "missing", "final", false, "ado-changed", 3)]
    [InlineData("completed", "unchanged", "final", false, "completed", 0)]
    [InlineData("ended", "unchanged", "final", true, "completed", 0)]
    [InlineData("failed", "unchanged", "final", false, "failed", 1)]
    [InlineData("completed", "unchanged", "draft", false, "nudge", 0)]
    [InlineData("completed", "unchanged", null, false, "nudge", 0)]
    [InlineData("completed", "unchanged", "draft", true, "completed-draft", 0)]
    [InlineData("completed", "unchanged", null, true, "failed-no-output", 1)]
    public void Post_check_files_a_judgment_only_when_ADO_did_not_change_and_nudges_once(
        string state,
        string compare,
        string? outputStage,
        bool nudged,
        string stage,
        int exit
    )
    {
        JsonNode? read = compare switch
        {
            "unchanged" => "unchanged",
            "changed" => new JsonObject
            {
                ["new"] = 1,
                ["changed"] = 0,
                ["sample"] = new JsonArray("NEW comment 7/3"),
            },
            _ => null,
        };

        JudgeRunner.PostCheckEnd(state, read, outputStage, nudged).Should().Be((stage, exit));
    }

    // compare() over two snapshots of the same threads: an edited comment and a deleted one both count as a change.
    [SkippableFact]
    public async Task Compare_sees_an_edited_and_a_deleted_comment_and_nothing_in_identical_threads()
    {
        var python = await ReviewdSummaryReplyTests.FindPythonAsync();
        Skip.If(python is null, "no python3 on PATH");
        var threads = JsonNode.Parse(
            """
            {"value": [{"id": 7, "status": "active", "lastUpdatedDate": "2026-10-01T10:00:00Z", "comments": [
              {"id": 1, "lastUpdatedDate": "2026-10-01T10:00:00Z"},
              {"id": 2, "lastUpdatedDate": "2026-10-01T11:00:00Z"}]}]}
            """
        )!;
        var edited = threads.DeepClone();
        edited["value"]![0]!["comments"]![0]!["lastUpdatedDate"] = "2026-10-01T12:00:00Z";
        var deleted = threads.DeepClone();
        deleted["value"]![0]!["comments"]![1]!["isDeleted"] = true;

        var compared = await SandboxAsync(
            python!,
            "[sandbox.compare(sandbox.id_snapshot(data[0]), sandbox.id_snapshot(t)) for t in data]",
            new JsonArray(threads, threads.DeepClone(), edited, deleted)
        );

        compared[1]!.GetValue<string>().Should().Be("unchanged");
        compared[2]!["changed"]!.GetValue<int>().Should().Be(1);
        compared[2]!["sample"]![0]!.GetValue<string>().Should().StartWith("comment 7/1 changed");
        compared[3]!["changed"]!.GetValue<int>().Should().Be(1);
        compared[3]!["sample"]![0]!.GetValue<string>().Should().StartWith("comment 7/2 changed");
    }

    [Fact]
    public async Task The_judge_thread_is_provisioned_headless_with_read_only_egress()
    {
        // The judge reads ADO and must never write to it; the lanes on the same workspace keep full egress.
        var host = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Post,
            "api/conversations",
            """{"threadId":"thread-judge","reasoningEffortAccepted":true,"readOnlyEgressAccepted":true}"""
        );
        var identity = new JsonObject
        {
            ["provider"] = "test",
            ["mode"] = "workspace-agent",
            ["effort"] = "xhigh",
        };

        var thread = await JudgeRunner.ProvisionJudgeThreadAsync(
            Client(host),
            "ws-1",
            identity,
            CancellationToken.None
        );

        thread.Should().Be("thread-judge");
        host.Requests.Should()
            .ContainSingle()
            .Which.Body.Should()
            .Contain("\"readOnlyEgress\":true")
            .And.Contain("\"disableClientTools\":true");
    }

    // Recorded live: Nova 5778641 at 00:14Z after the host restart. A host too slow to make the
    // judge thread is said plainly; any other refusal keeps the host's own words.
    [Fact]
    public void A_judge_thread_the_host_was_too_slow_to_make_says_nothing_was_spent()
    {
        var timeout = new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout of 120 seconds elapsing."
        );

        JudgeRunner
            .ProvisionFailure(timeout, CancellationToken.None)
            .Should()
            .Be(
                "the host was too slow: The request was canceled due to the configured HttpClient.Timeout of 120 "
                    + "seconds elapsing. No judge ran and nothing was spent; rerun later"
            );
        JudgeRunner
            .ProvisionFailure(new HttpRequestException("busy", null, HttpStatusCode.ServiceUnavailable), default)
            .Should()
            .StartWith("the host was too slow: busy");
        JudgeRunner
            .ProvisionFailure(new HttpRequestException("unknown mode", null, HttpStatusCode.NotFound), default)
            .Should()
            .Be("unknown mode");
    }

    // Recorded live: WeveNova 5788698's nudge after its host-restart recovery (01:16Z). The send's first try ran past
    // the 120 s HttpClient timeout; the same send with the same key must go again, not end the run.
    [Fact]
    public async Task A_send_the_host_does_not_answer_goes_again_with_the_same_key()
    {
        var host = new SlowSendHost(hangs: 2);

        var input = await JudgeRunner.SendWithKeyAsync(
            SendClient(host),
            "thread-1",
            "Stop investigating.",
            "reviewd-judge-nudge-1",
            FastOutage,
            "WeveNova #5788698",
            CancellationToken.None
        );

        input.Should().Be("idem:0:reviewd-judge-nudge-1");
        host.Keys.Should().Equal("reviewd-judge-nudge-1", "reviewd-judge-nudge-1", "reviewd-judge-nudge-1");
    }

    // The send gives up like the wait, after GiveUpAfter with no host answer, and says a rerun is safe.
    [Fact]
    public async Task A_send_the_host_never_answers_stops_with_the_rerun_text()
    {
        var send = () =>
            JudgeRunner
                .SendWithKeyAsync(
                    SendClient(new SlowSendHost(hangs: int.MaxValue)),
                    "thread-1",
                    "Stop investigating.",
                    "reviewd-judge-nudge-1",
                    FastOutage with
                    {
                        GiveUpAfter = TimeSpan.FromMilliseconds(150),
                    },
                    "WeveNova #5788698",
                    CancellationToken.None
                )
                .WaitAsync(TimeSpan.FromSeconds(5));

        (await send.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should()
            .Contain("has not answered")
            .And.Contain("Rerun to send again with the same key; nothing is sent twice.");
    }

    // A definitive answer (a 4xx) is not an outage; the send is not repeated.
    [Fact]
    public async Task A_send_the_host_refuses_is_not_repeated()
    {
        var host = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Post,
            "/messages",
            """{"error":"forbidden"}""",
            HttpStatusCode.Forbidden
        );

        var send = () =>
            JudgeRunner.SendWithKeyAsync(
                Client(host),
                "thread-1",
                "Stop investigating.",
                "reviewd-judge-nudge-1",
                FastOutage,
                "WeveNova #5788698",
                CancellationToken.None
            );

        (await send.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        host.CountRequests("/messages").Should().Be(1);
    }

    [Fact]
    public async Task A_host_read_timing_out_mid_wait_is_ridden_out_and_the_wait_ends_completed()
    {
        var (outcome, status) = await WaitOnHost(SlowClient(timeouts: 3), FastOutage);

        outcome.Should().Be("completed");
        status!.ResponseText.Should().Be("final judgment");
    }

    [Fact]
    public async Task A_host_outage_past_the_limit_stops_the_wait_with_the_resume_text()
    {
        var wait = () =>
            WaitOnHost(
                SlowClient(timeouts: int.MaxValue),
                FastOutage with
                {
                    GiveUpAfter = TimeSpan.FromMilliseconds(150),
                }
            );

        (await wait.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should()
            .Contain("has not answered")
            .And.Contain("Rerun to resume the wait; nothing is resent.");
    }

    // The status read alone is not an answer: a host whose run-state or sub-agent tree reads keep failing (a 5xx, or
    // a tree that stays hierarchy_busy) while the input reads InProgress gave the wait no end, and judge.lock stayed
    // held. Now such a wait stops after GiveUpAfter like any outage, and a rerun resumes it.
    [Theory]
    [InlineData("run-state")]
    [InlineData("tree")]
    public async Task A_wait_whose_run_state_or_tree_reads_keep_failing_gives_up_like_an_outage(string failing)
    {
        var host = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/status", """{"status":"InProgress","runId":"r1"}""")
            .OnJson(
                HttpMethod.Get,
                "/run-state",
                failing == "run-state" ? "{}" : """{"isInProgress":true,"currentRunId":"r1"}""",
                failing == "run-state" ? HttpStatusCode.BadGateway : HttpStatusCode.OK
            )
            .OnJson(
                HttpMethod.Get,
                "/subagents?recursive=true",
                failing == "tree" ? """{"code":"hierarchy_busy"}""" : """{"schemaVersion":1,"nodes":[]}""",
                failing == "tree" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK
            );

        var wait = () =>
            WaitOnHost(
                    Client(host),
                    FastOutage with
                    {
                        GiveUpAfter = TimeSpan.FromMilliseconds(100),
                    },
                    // Yields without waiting: 600 busy tree reads stay fast, and a wait with no end still times out.
                    async _ =>
                        await Task.Yield()
                )
                .WaitAsync(TimeSpan.FromSeconds(5));

        (await wait.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should()
            .Contain("Rerun to resume the wait; nothing is resent.");
    }

    [Fact]
    public async Task The_cost_cap_still_stops_the_wait_while_the_host_is_down()
    {
        using var capped = new CancellationTokenSource();
        var capReached = false;

        var (outcome, status) = await WaitOnHost(
            SlowClient(timeouts: int.MaxValue),
            FastOutage,
            async token =>
            {
                // The cap watcher's first poll during the outage finds the judge over its cap.
                capReached = true;
                await capped.CancelAsync();
                token.ThrowIfCancellationRequested();
            },
            capped.Token,
            () => capReached
        );

        outcome.Should().Be("capped");
        status.Should().BeNull();
    }

    // A host outage must not freeze a judge through the /usage-failure rule; a definitive failure still does.
    [Fact]
    public async Task Usage_reads_the_host_did_not_answer_do_not_freeze_a_judge_under_its_cap()
    {
        var host = CappedHost(
            InProgress(7),
            (HttpStatusCode.ServiceUnavailable, "{}"),
            (HttpStatusCode.GatewayTimeout, "{}"),
            (HttpStatusCode.ServiceUnavailable, "{}"),
            (HttpStatusCode.BadGateway, "{}"),
            (HttpStatusCode.ServiceUnavailable, "{}"),
            (HttpStatusCode.ServiceUnavailable, "{}"),
            (HttpStatusCode.OK, """{"preferredCostMicros":120000,"totalTokens":10,"completeness":0}""")
        );
        var (watch, receipt, capped) = Watch(host);

        var (outcome, _) = await WaitOnHost(
            Client(host),
            FastOutage,
            watch.DelayAsync,
            capped.Token,
            () => watch.Reached
        );

        outcome.Should().Be("completed");
        watch.Reached.Should().BeFalse();
        receipt["caps"]!["reason"].Should().BeNull();
        receipt["judgeUsage"]!["usd"]!.GetValue<decimal>().Should().Be(0.12m);
    }

    [Fact]
    public async Task Five_definitive_usage_read_failures_still_cap_the_judge_as_before()
    {
        var host = CappedHost(InProgress(int.MaxValue), (HttpStatusCode.Forbidden, "{}"));
        var (watch, receipt, capped) = Watch(host);

        var (outcome, _) = await WaitOnHost(
            Client(host),
            FastOutage,
            watch.DelayAsync,
            capped.Token,
            () => watch.Reached
        );

        outcome.Should().Be("capped");
        receipt["caps"]!["reason"]!
            .GetValue<string>()
            .Should()
            .StartWith("judge cost is not readable (5 failed /usage reads)");
        receipt["judgeUsage"]!["measured"]!.GetValue<bool>().Should().BeFalse();
    }

    // A /usage body that cannot be read as usage counts as a failed read too; it used to end the wait.
    [Theory]
    [InlineData("<html>proxy error</html>")]
    [InlineData("[]")]
    [InlineData("""{"preferredCostMicros":"lots"}""")]
    public async Task Five_unreadable_usage_bodies_cap_the_judge_like_a_definitive_failure(string body)
    {
        var host = CappedHost(InProgress(int.MaxValue), (HttpStatusCode.OK, body));
        var (watch, receipt, capped) = Watch(host);

        var (outcome, _) = await WaitOnHost(
            Client(host),
            FastOutage,
            watch.DelayAsync,
            capped.Token,
            () => watch.Reached
        );

        outcome.Should().Be("capped");
        receipt["caps"]!["reason"]!
            .GetValue<string>()
            .Should()
            .StartWith("judge cost is not readable (5 failed /usage reads)");
    }

    [Fact]
    public void Only_a_host_that_did_not_answer_is_an_outage_and_a_definitive_4xx_is_not()
    {
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();

        JudgeRunner
            .IsHostOutage(new TaskCanceledException("HttpClient.Timeout"), CancellationToken.None)
            .Should()
            .BeTrue();
        JudgeRunner.IsHostOutage(new HttpRequestException("connection refused"), default).Should().BeTrue();
        JudgeRunner.IsHostOutage(new LmStreamingS2SClient.HierarchyBusyException(), default).Should().BeTrue();
        JudgeRunner
            .IsHostOutage(new HttpRequestException("slow down", null, HttpStatusCode.TooManyRequests), default)
            .Should()
            .BeTrue();
        JudgeRunner.IsHostOutage(new TaskCanceledException(), stopped.Token).Should().BeFalse();
        JudgeRunner
            .IsHostOutage(new HttpRequestException("unknown thread", null, HttpStatusCode.NotFound), default)
            .Should()
            .BeFalse();
        JudgeRunner
            .IsHostOutage(new InvalidOperationException("Review input ended with Failed"), default)
            .Should()
            .BeFalse();
    }

    [Theory]
    [InlineData(720809, "0.220809")]
    [InlineData(500000, null)]
    [InlineData(400000, null)]
    public async Task The_cost_is_read_again_at_the_end_and_an_overrun_is_recorded_only_when_over(
        long micros,
        string? overrun
    )
    {
        var host = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            "thread-1/usage",
            $$"""{"preferredCostMicros":{{micros}},"completeness":2,"totalTokens":9}"""
        );
        var receipt = new JsonObject
        {
            ["caps"] = new JsonObject { ["maxUsd"] = 0.5m },
            ["judgeUsage"] = new JsonObject { ["usd"] = 0.1m },
        };

        var over = await JudgeRunner.FinalUsageAsync(Client(host), "thread-1", receipt, CancellationToken.None);

        host.CountRequests("thread-1/usage").Should().Be(1);
        receipt["judgeUsage"]!["usd"]!.GetValue<decimal>().Should().Be(micros / 1_000_000m);
        over.Should().Be(overrun is null ? null : decimal.Parse(overrun, CultureInfo.InvariantCulture));
        receipt["capOverrun"]?.GetValue<decimal>().Should().Be(over);
        receipt.ContainsKey("capOverrun").Should().Be(overrun is not null);
    }

    [Fact]
    public void The_resume_table_decides_from_the_newest_receipt_and_the_pending_marker()
    {
        var identity = Identity("gpt-6.1-sol");
        (JudgeRunner.Action, string) Decide(JsonObject? latest, bool rejudge = false, JsonObject? marker = null) =>
            JudgeRunner.Decide(
                latest is null ? [] : new SortedDictionary<int, JsonObject> { [1] = latest },
                marker,
                identity,
                rejudge,
                "the options"
            );

        Decide(Judged("completed", Identity("gpt-6.1-sol", finalHead: "abc")))
            .Item1.Should()
            .Be(JudgeRunner.Action.Skip);
        Decide(Judged("completed-draft", identity)).Item1.Should().Be(JudgeRunner.Action.Skip);
        var drift = Decide(Judged("completed", Identity("gpt-6-luna")));
        drift.Item1.Should().Be(JudgeRunner.Action.Refuse);
        drift.Item2.Should().Contain("identity changed").And.Contain("--rejudge");
        var again = Decide(Judged("completed", Identity("gpt-6-luna")), rejudge: true);
        again.Should().Be((JudgeRunner.Action.New, "start a new judgment (after judgment 1) with the options."));
        Decide(Judged("judge-running", identity)).Item1.Should().Be(JudgeRunner.Action.Resume);
        Decide(Judged("judge-running", identity), rejudge: true)
            .Item2.Should()
            .Be("judgment 1 stopped at judge-running; rerun without --rejudge to finish it first.");
        var capped = Judged("capped", identity);
        capped["descendantsSettled"] = true;
        capped["ado"] = new JsonObject { ["compare"] = "unchanged" };
        Decide(capped)
            .Should()
            .Be(
                (JudgeRunner.Action.Refuse, "judgment 1 ended capped; add --rejudge to judge again as a new judgment.")
            );
        capped["descendantsSettled"] = false;
        Decide(capped).Item1.Should().Be(JudgeRunner.Action.ResumeCapped);
        Decide(Judged("failed-precondition", identity)).Item1.Should().Be(JudgeRunner.Action.New);
        Decide(Judged("ado-changed", identity)).Item1.Should().Be(JudgeRunner.Action.Refuse);

        // T11: a marker whose nonce no receipt carries resumes the same dir; one under another identity is ignored.
        var marker = new JsonObject
        {
            ["nonce"] = "n-2",
            ["minN"] = 2,
            ["identity"] = identity.DeepClone(),
        };
        Decide(Judged("completed", identity), marker: marker).Item1.Should().Be(JudgeRunner.Action.ResumeMarker);
        marker["identity"] = Identity("gpt-6-luna");
        Decide(null, marker: marker)
            .Should()
            .Be(
                (
                    JudgeRunner.Action.New,
                    "start a new judgment with the options. A stale pending marker (another identity) is ignored."
                )
            );
    }

    // Sub-agents outlive a failed root input: the post-check that waited 20 minutes for them is repeated later, like a
    // capped one's, until they settle.
    [Fact]
    public void A_failed_judgment_whose_sub_agents_had_not_settled_is_post_checked_again()
    {
        var identity = Identity("gpt-6.1-sol");
        var failed = Judged("failed", identity);
        (JudgeRunner.Action, string) Decide() =>
            JudgeRunner.Decide(new() { [1] = failed }, null, identity, rejudge: false, "the options");

        Decide().Item1.Should().Be(JudgeRunner.Action.Refuse, "a failure whose sub-agents settled is final");
        failed["descendantsSettled"] = false;
        Decide()
            .Should()
            .Be(
                (
                    JudgeRunner.Action.ResumeCapped,
                    "judgment 1 failed while its sub-agents still ran; repeat the sub-agent settle and the post-check."
                )
            );
        failed["descendantsSettled"] = true;
        Decide().Item1.Should().Be(JudgeRunner.Action.Refuse);
    }

    [Fact]
    public async Task Preflight_refuses_what_the_host_cannot_run_before_anything_is_written()
    {
        var review = new JsonObject { ["artifactCommit"] = new JsonObject { ["reviewDirectory"] = "reviews/pr-1" } };
        async Task<string?> Refusal(string provider, string mode = "workspace-agent", string template = "Judge it.")
        {
            var host = new FakeHttpMessageHandler()
                .OnJson(
                    HttpMethod.Get,
                    "api/providers",
                    """{"providers":[{"id":"gpt-6.1-sol","available":true},{"id":"gpt-6-luna","available":false}]}"""
                )
                .OnJson(HttpMethod.Get, "api/chat-modes/workspace-agent", "{}")
                .OnJson(HttpMethod.Get, "api/chat-modes/", "{}", HttpStatusCode.NotFound)
                .OnCurrentReviewHostCapabilities();
            var identity = Identity(provider);
            identity["mode"] = mode;
            try
            {
                await JudgeRunner.PreflightAsync(
                    Client(host),
                    identity,
                    template,
                    review,
                    "org/project/Nova",
                    "1",
                    CancellationToken.None
                );
                return null;
            }
            catch (InvalidOperationException refused)
            {
                return refused.Message;
            }
        }

        (await Refusal("gpt-6.1-sol")).Should().BeNull();
        (await Refusal("no-such-provider")).Should().StartWith("--provider no-such-provider is not a provider");
        (await Refusal("gpt-6-luna")).Should().StartWith("--provider gpt-6-luna is unavailable");
        (await Refusal("gpt-6.1-sol", mode: "no-such-mode")).Should().StartWith("--mode no-such-mode is not a mode");
        (await Refusal("gpt-6.1-sol", template: " \n")).Should().StartWith("The judge prompt template is empty");
        (await Refusal("gpt-6.1-sol", template: new string('x', 100 * 1024)))
            .Should()
            .StartWith("The judge prompt is ")
            .And.Contain("(limit 102400)");
    }

    [Fact]
    public void Prior_judgments_are_the_finished_receipt_backed_judgments_of_other_prs()
    {
        const string Prefix = "reviewd/judge-run/azure-devops/org/project/nova/1/";
        static (string, string) Row(string pr, int n, string stage, string? dir) =>
            (
                $"reviewd/judge-run/azure-devops/org/project/nova/{pr}/{n}",
                new JsonObject { ["stage"] = stage, ["judgmentDir"] = dir }.ToJsonString()
            );

        JudgeRunner
            .PriorJudgments(
                [
                    Row("1", 1, "completed", "reviews/pr-1/judgment-1"),
                    Row("12", 1, "completed", "reviews/pr-12/judgment-1"),
                    Row("2", 1, "completed-draft", "reviews/pr-2/judgment-1"),
                    Row("2", 2, "completed", "reviews/pr-2/judgment-1"),
                    Row("3", 1, "capped", "reviews/pr-3/judgment-1"),
                    Row("4", 1, "judge-running", "reviews/pr-4/judgment-1"),
                    Row("5", 1, "completed", null),
                ],
                Prefix
            )
            .Select(dir => dir!.GetValue<string>())
            .Should()
            .Equal("reviews/pr-12/judgment-1", "reviews/pr-2/judgment-1");
    }

    [AchieveAi.LmDotnetTools.LmTestUtils.LinuxOnlyFact(
        "flock(1) is util-linux; the README's pre-deploy check runs it on the Linux host"
    )]
    public void The_judge_lock_admits_one_holder_and_flock_sees_it()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("judge-lock-").FullName, "judge.lock");
        try
        {
            using (var first = JudgeRunner.TryAcquireJudgeLock(path, out var none))
            {
                first.Should().NotBeNull();
                none.Should().BeNull();
                JudgeRunner.TryAcquireJudgeLock(path, out var held).Should().BeNull();
                held.Should().NotBeNullOrEmpty();
                Flock(path).Should().Be(1, "the README's pre-deploy check is flock -n judge.lock true");
            }
            using var again = JudgeRunner.TryAcquireJudgeLock(path, out _);
            again.Should().NotBeNull();
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }

        static int Flock(string path)
        {
            using var flock = System.Diagnostics.Process.Start("flock", ["-n", path, "true"])!;
            flock.WaitForExit();
            return flock.ExitCode;
        }
    }

    [SkippableFact]
    public async Task The_judge_inputs_digests_give_one_short_line_per_comment_and_a_small_summary_P2()
    {
        var python = await ReviewdSummaryReplyTests.FindPythonAsync();
        Skip.If(python is null, "no Python interpreter on PATH, so the sandbox's digests cannot be run");
        var limitations = new JsonArray([
            .. Enumerable.Range(1, 200).Select(i => (JsonNode)$"limitation {i}: {new string('x', 60)}"),
        ]);
        var fixture = JsonNode.Parse(
            $$$"""
            {
              "packet": {
                "identity": {"repository": "O365Exchange/Weve_DA/Nova", "prId": "7001", "title": "Fix the filter",
                  "author": "Ana Author", "status": "completed", "creationDate": "2026-10-01T09:00:00Z",
                  "closedDate": "2026-10-03T09:00:00Z", "sourceRef": "refs/heads/fix", "targetRef": "refs/heads/main",
                  "lastMergeSourceCommit": "aaaa", "lastMergeCommit": "bbbb", "workItemIds": ["42"],
                  "reviewDirectory": "PRs/nova-7001"},
                "review": {"rounds": [{"round": 1, "branch": "review/nova-7001", "headSha": "cccc", "iteration": 2,
                    "artifactCommit": "dddddddddddddddd", "artifactCommitTime": "2026-10-01T12:00:00Z",
                    "layout": {"reconciliation": ["PR_Reconciliation.md"], "dispositions": [], "jsonFunnel": ["findings.json"],
                      "prFindings": [], "prBrief": [], "reviewMd": ["review.md"]}}],
                  "firstReviewAt": "2026-10-01T11:00:00+00:00", "reviewedIterations": [2], "lastIteration": 3,
                  "iterationsAfterReview": [3]},
                "ours": {"posted": [{"threadId": 101, "commentId": 1, "kind": "finding"}],
                  "unreceiptedRevobot": [{"threadId": 102, "commentId": 1}], "operatorManual": [{"threadId": 102, "commentId": 2}]},
                "cost": {"label": "complete; PR lifetime, 1 round(s)", "totalUsd": 3.42, "totalTokens": 1000, "unknownThreads": [],
                  "threads": [{"threadId": "thread-r1", "status": "measured", "usd": 3.42, "totalTokens": 1000, "label": "complete"}]},
                "localAvailability": {"finalHead": {"sha": "aaaa", "inLocalSourceStore": false},
                  "finalMerge": {"sha": "bbbb", "inLocalSourceStore": true}},
                "limitations": {{{limitations.ToJsonString()}}}
              },
              "threads": {"count": 3, "value": [
                {"id": 101, "status": "fixed", "threadContext": {"filePath": "/src/A.cs", "rightFileStart": {"line": 12}},
                  "pullRequestThreadContext": {"iterationContext": {"secondComparingIteration": 2}}, "comments": [
                  {"id": 1, "author": {"displayName": "Op", "uniqueName": "op@x.com"}, "commentType": "text",
                    "publishedDate": "2026-10-01T12:30:45.123Z", "content": "Finding text"},
                  {"id": 2, "author": {"displayName": "Ana Author", "uniqueName": "ana@x.com"}, "commentType": "text",
                    "publishedDate": "2026-10-02T08:00:00Z",
                    "content": "<p>Fixed in <b>abc</b> &amp; tested</p>\n<!-- agency-review-finding: eyJ0 -->\r\nThanks"}]},
                {"id": 102, "status": "active", "comments": [
                  {"id": 1, "author": {"displayName": "Op", "uniqueName": "op@x.com"}, "commentType": "text",
                    "publishedDate": "2026-10-01T13:00:00Z", "content": "[Revobot] summary"},
                  {"id": 2, "author": {"displayName": "Op", "uniqueName": "op@x.com"}, "commentType": "text",
                    "publishedDate": "2026-10-01T14:00:00Z", "content": "manual note"},
                  {"id": 3, "author": {"displayName": "Microsoft.VisualStudio.Services.TFS"}, "commentType": "system",
                    "content": "voted 10"}]},
                {"id": 103, "status": "closed", "comments": [
                  {"id": 1, "author": {"displayName": "Azure Pipelines Test Service", "uniqueName": ""}, "commentType": "text",
                    "publishedDate": "2026-10-01T10:00:00Z", "content": "{{{new string('y', 700)}}}"},
                  {"id": 2, "author": {"displayName": "Bob", "uniqueName": "bob@x.com"}, "commentType": "text",
                    "isDeleted": true, "content": ""}]}]},
              "final": {"sha": "aaaa", "fetched": ["/src/A.cs"], "skipped": [{"path": "/img/logo.png", "reason": "binary"}]}
            }
            """
        )!;

        var (threads, summary) = await DigestsAsync(python!, fixture);

        threads
            .Split('\n')
            .Where(line => line.Length > 0 && char.IsAsciiDigit(line[0]))
            .Should()
            .Equal(
                "101/1 | ours | fixed | /src/A.cs:12 | iter 2 | 2026-10-01T12:30Z | Op | Finding text",
                "101/2 | human | fixed | /src/A.cs:12 | iter 2 | 2026-10-02T08:00Z | Ana Author | Fixed in abc & tested Thanks",
                "102/1 | ours-unreceipted | active | PR | iter - | 2026-10-01T13:00Z | Op | [Revobot] summary",
                "102/2 | operator | active | PR | iter - | 2026-10-01T14:00Z | Op | manual note",
                $"103/1 | bot | closed | PR | iter - | 2026-10-01T10:00Z | Azure Pipelines Test Service | {new string('y', 600)}…",
                "103/2 | human | closed | PR | iter - | - | Bob | (deleted)"
            );
        Encoding.UTF8.GetByteCount(summary).Should().BeLessThanOrEqualTo(8 * 1024);
        summary
            .Should()
            .Contain("## Ours: 1 posted")
            .And.Contain("101/1 (finding)")
            .And.Contain("- finalHead: aaaa NOT in the local source store")
            .And.Contain("- /img/logo.png: binary")
            .And.Contain("reconciliation: PR_Reconciliation.md")
            .And.EndWith("… (cut at 8 KB; the rest is in packet.json)\n");
    }

    private static async Task<(string Threads, string Summary)> DigestsAsync(string python, JsonNode fixture)
    {
        var digests = await SandboxAsync(
            python,
            """
            {"threads": sandbox.threads_md(data["packet"], data["threads"]),
             "summary": sandbox.packet_summary_md(data["packet"], data["final"])}
            """,
            fixture
        );
        return (digests["threads"]!.GetValue<string>(), digests["summary"]!.GetValue<string>());
    }

    // A review retried many times carries a long attempt history. Its judge-inputs arguments outgrew Linux's 128 KiB
    // limit for one argv string (IdeasWatchTower !5817047, 146,812 bytes), so that PR could never be judged.
    [SkippableFact]
    public async Task Judge_arguments_over_one_argv_string_go_in_pieces_the_script_joins()
    {
        var python = await ReviewdSummaryReplyTests.FindPythonAsync();
        Skip.If(python is null, "no python3 on PATH");
        Skip.If(OperatingSystem.IsWindows(), "the 128 KiB limit for one argument is Linux's, where the sandbox runs");
        var history = new JsonArray([
            .. Enumerable
                .Range(1, 30)
                .Select(i =>
                    (JsonNode)
                        new JsonObject
                        {
                            ["key"] = $"attempt-{i}",
                            ["reviewResponse"] = $"Revue {i}: é ✓ \U0001F600 " + new string('x', 10_000),
                        }
                ),
        ]);
        // Raw UTF-8 with a surrogate pair. Production JSON escapes non-ASCII, so rune-safe pieces are a backstop.
        var json = new JsonObject { ["host"] = new JsonObject { ["attemptHistory"] = history } }
            .ToJsonString(
                new System.Text.Json.JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }
            )
            .Replace("\\uD83D\\uDE00", "\U0001F600", StringComparison.Ordinal);

        var argv = JudgeRunner.ScriptArgv("pass", "judge-inputs", json, "the template");

        json.Should().Contain("\U0001F600");
        Encoding.UTF8.GetByteCount(json).Should().BeGreaterThan(2 * JudgeRunner.ArgvLimitBytes);
        argv.Take(5).Should().Equal("python3", "-B", "-c", "pass", "judge-inputs");
        argv[5].Should().Be($"--args-parts={argv.Count - 7}");
        argv.Should().OnlyContain(a => Encoding.UTF8.GetByteCount(a) <= JudgeRunner.ArgvLimitBytes);
        var read = await ReadArgvAsync(python!, argv.Skip(4));
        read["phase"]!.GetValue<string>().Should().Be("judge-inputs");
        read["template"]!.GetValue<string>().Should().Be("the template");
        JsonNode.DeepEquals(read["args"], JsonNode.Parse(json)).Should().BeTrue("the pieces join back exactly");
        // A 4-byte character that would cross the limit starts the next piece whole (3 bytes short: a loop that counts
        // each half of a surrogate pair as 3 bytes would split it here).
        var straddle = $"{{\"s\":\"{new string('x', JudgeRunner.ArgvLimitBytes - 9)}\U0001F600\"}}";
        var cut = JudgeRunner.ScriptArgv("pass", "judge-inputs", straddle, null);
        cut.Should().HaveCount(8);
        Encoding.UTF8.GetByteCount(cut[6]).Should().Be(JudgeRunner.ArgvLimitBytes - 3);
        cut[7].Should().Be("\U0001F600\"}");
        (await ReadArgvAsync(python!, cut.Skip(4)))["args"]!["s"]!.GetValue<string>().Should().EndWith("x\U0001F600");
        JudgeRunner
            .ScriptArgv("pass", "judge-snapshot", "{}", null)
            .Should()
            .Equal(["python3", "-B", "-c", "pass", "judge-snapshot", "{}"], "small arguments stay one argument");
    }

    [Fact]
    public void Judge_arguments_too_long_even_in_pieces_fail_before_anything_runs()
    {
        var total = () => JudgeRunner.ScriptArgv("pass", "judge-inputs", new string('x', 1024 * 1024), null);
        var one = () =>
            JudgeRunner.ScriptArgv("pass", "close-commit", "{}", new string('t', JudgeRunner.ArgvLimitBytes + 1));

        total
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Sandbox judge-inputs: the arguments are * bytes, over the 1048576-byte limit; nothing was run."
            );
        one.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Sandbox close-commit: an argument is 122881 bytes, over the 122880-byte limit; nothing was run."
            );
    }

    // Runs the embedded JudgeSandbox.py's read_argv over real process arguments (the OS limits apply).
    private static async Task<JsonNode> ReadArgvAsync(string python, IEnumerable<string> arguments)
    {
        var script = Path.Combine(Path.GetTempPath(), $"judge-sandbox-{Guid.NewGuid():N}.py");
        using (var resource = typeof(JudgeRunner).Assembly.GetManifestResourceStream("Reviewd.Sample.JudgeSandbox.py")!)
        using (var file = File.Create(script))
            await resource.CopyToAsync(file);
        try
        {
            const string harness = """
                import importlib.util, json, sys
                spec = importlib.util.spec_from_file_location("judge_sandbox", sys.argv[1])
                sandbox = importlib.util.module_from_spec(spec)
                spec.loader.exec_module(sandbox)
                phase, args, template = sandbox.read_argv(sys.argv[2:])
                print(json.dumps({"phase": phase, "args": args, "template": template}))
                """;
            var start = new System.Diagnostics.ProcessStartInfo(python)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in new[] { "-I", "-c", harness, script }.Concat(arguments))
                start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            process.ExitCode.Should().Be(0, error);
            return JsonNode.Parse(await output)!;
        }
        finally
        {
            File.Delete(script);
        }
    }

    // Imports the embedded JudgeSandbox.py as a module (its phase dispatch runs only as __main__) and returns the JSON
    // of a Python expression over it (`sandbox`) and the input (`data`).
    internal static async Task<JsonNode> SandboxAsync(string python, string expression, JsonNode data)
    {
        var script = Path.Combine(Path.GetTempPath(), $"judge-sandbox-{Guid.NewGuid():N}.py");
        using (var resource = typeof(JudgeRunner).Assembly.GetManifestResourceStream("Reviewd.Sample.JudgeSandbox.py")!)
        using (var file = File.Create(script))
            await resource.CopyToAsync(file);
        try
        {
            const string harness = """
                import importlib.util, json, sys
                spec = importlib.util.spec_from_file_location("judge_sandbox", sys.argv[1])
                sandbox = importlib.util.module_from_spec(spec)
                spec.loader.exec_module(sandbox)
                data = json.loads(sys.stdin.read())
                print(json.dumps(eval(sys.argv[2])))
                """;
            var start = new System.Diagnostics.ProcessStartInfo(python)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var argument in new[] { "-I", "-c", harness, script, expression })
                start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start)!;
            await process.StandardInput.WriteAsync(data.ToJsonString());
            process.StandardInput.Close();
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            process.ExitCode.Should().Be(0, error);
            return JsonNode.Parse(output)!;
        }
        finally
        {
            File.Delete(script);
        }
    }

    private static LmStreamingS2SClient Client(FakeHttpMessageHandler host) =>
        new(new HttpClient(host) { BaseAddress = new Uri("http://localhost/") }, null, null, null);

    // A judge that already finished, behind a host whose first reads hang past HttpClient.Timeout (a real timeout).
    private static LmStreamingS2SClient SlowClient(int timeouts) =>
        new(
            new HttpClient(new SlowHost(timeouts))
            {
                BaseAddress = new Uri("http://localhost/"),
                Timeout = TimeSpan.FromMilliseconds(30),
            },
            null,
            null,
            null
        );

    private static Task<(string Outcome, S2SStatusResult? Status)> WaitOnHost(
        LmStreamingS2SClient client,
        JudgeRunner.HostOutage outage,
        Func<CancellationToken, Task>? delay = null,
        CancellationToken capped = default,
        Func<bool>? capReached = null,
        Func<DateTimeOffset>? clock = null
    ) =>
        JudgeRunner.WaitForInputAsync(
            client,
            "thread-1",
            "input-1",
            delay ?? (token => Task.Delay(1, token)),
            capped,
            capReached ?? (() => false),
            outage,
            "NovaClient #5790557",
            CancellationToken.None,
            clock
        );

    // A judge whose status reads answer `statuses` in order and whose /usage reads answer `usage` in order.
    private static FakeHttpMessageHandler CappedHost(
        (HttpStatusCode, string)[] statuses,
        params (HttpStatusCode Status, string Json)[] usage
    ) =>
        new FakeHttpMessageHandler()
            .OnSequence(HttpMethod.Get, "/status", statuses)
            .OnJson(HttpMethod.Get, "/run-state", """{"isInProgress":false,"currentRunId":null}""")
            .OnJson(HttpMethod.Get, "/subagents?recursive=true", """{"schemaVersion":1,"nodes":[]}""")
            .OnSequence(HttpMethod.Get, "/usage", usage);

    private static (HttpStatusCode, string)[] InProgress(int reads) =>
        reads == int.MaxValue
            ? [(HttpStatusCode.OK, """{"status":"InProgress","runId":"run-1"}""")]
            :
            [
                .. Enumerable.Repeat((HttpStatusCode.OK, """{"status":"InProgress","runId":"run-1"}"""), reads),
                (HttpStatusCode.OK, """{"status":"Completed","runId":"run-1","response":{"text":"final judgment"}}"""),
            ];

    // The runner's cap watcher at $1 with no waits: every poll reads /usage.
    private static (JudgeRunner.CapWatch Watch, JsonObject Receipt, CancellationTokenSource Capped) Watch(
        FakeHttpMessageHandler host
    )
    {
        var receipt = new JsonObject { ["caps"] = new JsonObject { ["maxUsd"] = 1m } };
        var capped = new CancellationTokenSource();
        var watch = new JudgeRunner.CapWatch(
            Client(host),
            "thread-1",
            receipt,
            1m,
            _ => Task.CompletedTask,
            capped,
            "NovaClient #5790557"
        )
        {
            PollEvery = TimeSpan.Zero,
            ReadEvery = TimeSpan.Zero,
        };
        return (watch, receipt, capped);
    }

    private static LmStreamingS2SClient SendClient(SlowSendHost host) =>
        new(
            new HttpClient(host)
            {
                BaseAddress = new Uri("http://localhost/"),
                Timeout = TimeSpan.FromMilliseconds(30),
            },
            null,
            null,
            null
        );

    // A host whose first `hangs` sends run past HttpClient.Timeout; it then answers with the key's input id.
    private sealed class SlowSendHost(int hangs) : HttpMessageHandler
    {
        internal List<string> Keys { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            var key = body["idempotencyKey"]!.GetValue<string>();
            Keys.Add(key);
            if (Keys.Count <= hangs)
                await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    new JsonObject { ["inputId"] = "idem:0:" + key, ["idempotencyKeyHonored"] = true }.ToJsonString(),
                    Encoding.UTF8,
                    "application/json"
                ),
            };
        }
    }

    private sealed class SlowHost(int timeouts)
        : DelegatingHandler(
            new FakeHttpMessageHandler()
                .OnJson(
                    HttpMethod.Get,
                    "/status",
                    """{"status":"Completed","runId":"run-1","response":{"text":"final judgment"}}"""
                )
                .OnJson(HttpMethod.Get, "/run-state", """{"isInProgress":false,"currentRunId":null}""")
                .OnJson(HttpMethod.Get, "/subagents?recursive=true", """{"schemaVersion":1,"nodes":[]}""")
        )
    {
        private int _requests;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            if (Interlocked.Increment(ref _requests) <= timeouts)
                await Task.Delay(Timeout.Infinite, cancellationToken);
            return await base.SendAsync(request, cancellationToken);
        }
    }

    private static JsonObject Identity(string provider, string? finalHead = null) =>
        new()
        {
            ["reviewArtifactCommit"] = new string('a', 40),
            ["reviewedHeads"] = new JsonArray("h1"),
            ["finalHeadSha"] = finalHead,
            ["promptTemplateSha256"] = "sha",
            ["provider"] = provider,
            ["effort"] = "xhigh",
            ["mode"] = "workspace-agent",
        };

    private static JsonObject Judged(string stage, JsonObject identity) =>
        new()
        {
            ["n"] = 1,
            ["stage"] = stage,
            ["nonce"] = "n-1",
            ["identity"] = identity.DeepClone(),
        };

    // Read through the real client from rows shaped like the host's GET api/conversations/{id}/messages.
    private static async Task<IReadOnlyList<ReviewAgentTranscriptEntry>> RootMessagesAsync(int answered)
    {
        var rows = new JsonArray();
        for (var i = 0; i < RecordedCalls.Length; i++)
        {
            var (name, id) = RecordedCalls[i];
            rows.Add(
                Row(
                    "ToolCallMessage",
                    "Assistant",
                    new JsonObject
                    {
                        ["$type"] = "tool_call",
                        ["function_name"] = name,
                        ["function_args"] = "{}",
                        ["tool_call_id"] = id,
                        ["execution_target"] = "LocalFunction",
                    }
                )
            );
            if (i < answered)
                rows.Add(
                    Row(
                        "ToolCallResultMessage",
                        "User",
                        new JsonObject
                        {
                            ["$type"] = "tool_call_result",
                            ["tool_call_id"] = id,
                            ["tool_name"] = name,
                            ["result"] = "ok",
                            ["is_error"] = false,
                            ["is_deferred"] = false,
                            ["execution_target"] = "LocalFunction",
                        }
                    )
                );
        }
        rows.Add(Row("TextMessage", "User", new JsonObject { ["$type"] = "text", ["text"] = "elapsed-time notice" }));
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "/messages", rows.ToJsonString());
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        return await new LmStreamingS2SClient(http, null, null, null).GetConversationTranscriptAsync(
            "thread-1792674410399-8995d6010d324b2ab64fde6f460fa723",
            CancellationToken.None
        );
    }

    private static JsonObject Row(string type, string role, JsonObject message) =>
        new()
        {
            ["messageType"] = type,
            ["role"] = role,
            ["fromAgent"] = "GPT-6.1 Sol",
            ["timestamp"] = Now.ToUnixTimeMilliseconds(),
            ["messageJson"] = message.ToJsonString(),
        };

    private static ReviewSubAgentNode Child(ReviewSubAgentStatus status) =>
        new()
        {
            AgentId = "agent-1",
            ThreadId = "subagent-0c3feb307da9-agent-1",
            ParentThreadId = "thread-1792674410399-8995d6010d324b2ab64fde6f460fa723",
            Depth = 1,
            Status = status,
            Template = "code-reviewer:finding-verifier",
        };
}
