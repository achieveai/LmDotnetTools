extern alias Reviewd;

using System.Net;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.Misc.Storage;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Reviewd::Reviewd.Sample;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
///     <c>review run --shadow</c> is an A/B experiment arm: the same review on a fresh read-only-egress conversation,
///     recorded in would_post.json instead of posted, and titled apart so the live runner never reuses it.
/// </summary>
public sealed class ReviewdShadowTests : IDisposable
{
    private static readonly RepoIdentity Repo = PrCommands.ParseRepo("org/project/Nova");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-07T22:00:00Z");
    private static readonly string Head = new('a', 40);
    private readonly string _database = Path.Combine(Path.GetTempPath(), $"reviewd-shadow-{Guid.NewGuid():N}.db");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_shadow_run_provisions_read_only_titles_apart_and_sends_the_shadow_prompt(bool shadow)
    {
        var handler = Host();
        var phases = new List<(string Phase, JsonObject Args)>();
        await SnapshotAsync();

        // The scripted host reports the review Errored after the send; this test only checks how it got there.
        var run = () => RunAsync(handler, phases, shadow ? ["--shadow", "--variant", "kb-h1"] : []);
        await run.Should().ThrowAsync<InvalidOperationException>();

        var provision = handler.Requests.Single(r =>
            r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/api/conversations"
        );
        var title = handler.Requests.Single(r => r.Uri.AbsolutePath.EndsWith("/metadata", StringComparison.Ordinal));
        var send = handler.Requests.First(r => r.Uri.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal));
        var attempt = await AttemptAsync();
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
            attempt["shadow"] = true;
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
    [InlineData(null, "--shadow", "This --db holds live reviews*")]
    [InlineData("--shadow", null, "This --db belongs to a shadow arm; the live reviewer needs*")]
    [InlineData("--variant kb-h1", "--variant kb-h2", "*shadow arm 'kb-h1'; shadow arm 'kb-h2' needs*")]
    [InlineData("--variant kb-h1", "", "*shadow arm 'kb-h1'; a shadow arm needs*")]
    public async Task A_database_belongs_to_one_arm(string? first, string? second, string refusal)
    {
        // The first run pins the database to its arm; a run from any other arm is refused before it touches the host.
        // null is a live run. A live first run pins nothing, so the live arm is pinned by holding a live attempt.
        static string[] Arm(string? arm) =>
            arm switch
            {
                null => [],
                "" or "--shadow" => ["--shadow"],
                _ => ["--shadow", .. arm.Split(' ')],
            };
        await SnapshotAsync();
        if (first is null)
            await PutAsync(
                $"reviewd/review-attempt/{Repo.NormalizedKey}/17",
                new JsonObject { ["repository"] = Repo.DisplayName, ["stage"] = "review-running" }
            );
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

    [Fact]
    public async Task Batch_shadow_arm_is_forwarded_to_fresh_and_resumed_runs_and_a_restart_cannot_change_it()
    {
        using var batch = new ReviewdRunnerTests.BatchFixture(0, 1)
        {
            Arm = ["--shadow", "--post-session", "read-only", "--variant", "kb-h1"],
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
                ["--shadow", "--post-session", "read-only", "--variant", "kb-h2"],
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
            .Parse(["sandbox", "create", "--workspace", "w", "--callback", "http://h", "--read-only-egress"])
            .Has("read-only-egress")
            .Should()
            .BeTrue();
    }

    [Fact]
    public async Task Every_step_but_prepare_runs_in_the_read_only_session_and_a_step_stop_follows_its_step()
    {
        // The shadow agent can edit the workspace these steps run in; only prepare needs full egress (its git fetch).
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
            _ = await route(phase, [], phase + "-op", CancellationToken.None);
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
                "prepare@full",
                "inspect@read-only",
                "verify@read-only",
                "commit@read-only",
                "cleanup@read-only",
                "step-stop@full",
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
            await ReadOnlySessions.MarkAsync(store, "session-1", "workspace", CancellationToken.None);
            unmarked.Should().NotThrow();
        }
        finally
        {
            Directory.Delete(store, recursive: true);
        }
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
                CliOptions.Parse([.. command, "--shadow", "--post-session", "read-only", "--variant", label]);
            if (valid)
                parse().Get("variant").Should().Be(label);
            else
                parse.Should().Throw<ArgumentException>().WithMessage("--variant*");
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

    public void Dispose()
    {
        ReviewdSqlitePools.Clear(_database);
        if (File.Exists(_database))
            File.Delete(_database);
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
                """{"messageIdempotency":true,"spawnSuppression":true,"rootReasoningEffort":true,"conversationWorkingDirectory":true}"""
            )
            .On(
                request =>
                    request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/conversations",
                _ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"threadId":"thread","readOnlyEgressAccepted":true}"""),
                }
            )
            .OnJson(HttpMethod.Get, "/api/conversations?", conversations)
            .OnJson(HttpMethod.Put, "/metadata", "{}")
            .OnJson(HttpMethod.Post, "/messages", """{"inputId":"input","idempotencyKeyHonored":true}""")
            .OnJson(HttpMethod.Get, "/status?", status)
            .OnJson(HttpMethod.Get, "/run-state", """{"isInProgress":false,"currentRunId":null}""")
            .OnJson(HttpMethod.Get, "/subagents?", """{"schemaVersion":1,"nodes":[]}""");

    private async Task<int> RunAsync(
        FakeHttpMessageHandler handler,
        List<(string Phase, JsonObject Args)> phases,
        string[] extra
    )
    {
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        using var config = new ReviewdConfig(new ConfigurationBuilder().Build());
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
            "--execute",
            .. extra,
            .. extra.Contains("--shadow") ? new[] { "--post-session", "read-only" } : [],
        ]);
        Task<JsonObject> Sandbox(string phase, JsonObject args, string operation, CancellationToken token)
        {
            phases.Add((phase, args.DeepClone().AsObject()));
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
