using System.Net;
using System.Text.Json;
using TodoEval.Runner.Metrics;
using TodoEval.Runner.Sweep;

namespace TodoEval.Runner.Tests;

/// <summary>
/// One compaction-eval task driven end to end against a scripted host: a fresh workspace filled from
/// the task's fixtures, the mid-run correction, the checker's verdict, and a <c>runs.jsonl</c> row
/// carrying both judging layers. No real host, no model, no pwsh — every seam is a fake, so what this
/// proves is the WIRING between them.
/// </summary>
public class SweepEndToEndTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"todo-eval-e2e-{Guid.NewGuid():N}");

    public SweepEndToEndTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>A task directory with the tasks/README.md contract: fixtures, hidden material, meta.</summary>
    private EvalTaskAsset WriteTask(string? steer = null, int? steerAfterSeconds = 0, SteerTrigger? steerAfter = null)
    {
        var dir = Path.Combine(_root, "tasks", "c1");
        Directory.CreateDirectory(Path.Combine(dir, "fixtures", "data"));
        Directory.CreateDirectory(Path.Combine(dir, "hidden"));
        File.WriteAllText(Path.Combine(dir, "fixtures", "data", "sample.csv"), "a,b\n1,2\n");
        File.WriteAllText(Path.Combine(dir, "hidden", "expected.json"), """{"answer":42}""");

        return new EvalTaskAsset
        {
            Id = "c1",
            Dir = dir,
            Template = "Write csvagg.py. Code name {SEED}.",
            Steer = steer,
            Meta = new TaskMeta
            {
                Seeds = ["aurora", "basalt"],
                MinCompactions = 1,
                SteerAfterSeconds = steerAfterSeconds,
                SteerAfter = steerAfter,
            },
            ExpectedBoard = null,
        };
    }

    private EvalRunnerConfig Config() =>
        new()
        {
            Models = ["m1"],
            Topics = ["a topic"],
            Seeds = 1,
            PerRunTimeoutMinutes = 1,
            WorkspacesRoot = Path.Combine(_root, "workspaces"),
        };

    private async Task<IReadOnlyList<RunManifestEntry>> SweepAsync(
        EvalTaskAsset task,
        ScriptedHost host,
        ITaskChecker? checker = null,
        VariantConfig? variant = null
    )
    {
        using var http = new HttpClient(host) { BaseAddress = new Uri("http://127.0.0.1:9/") };
        var runner = new SweepRunner(
            new EvalHostClient(http),
            Config(),
            "shared-ws",
            "mode-1",
            variant ?? new VariantConfig { Name = "compact-v0", Compacts = true },
            [task],
            TextWriter.Null,
            checker,
            Path.Combine(_root, "scores")
        );

        return await runner.RunSweepAsync(Path.Combine(_root, "runs-manifest.jsonl"), CancellationToken.None);
    }

    // ── the run ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ARunGetsItsOwnWorkspace_FilledFromFixturesAndFreeOfHiddenMaterial()
    {
        var task = WriteTask();

        var entry = (await SweepAsync(task, new ScriptedHost())).Single();

        entry.WorkspacePath.Should().NotBeNull();
        File.Exists(Path.Combine(entry.WorkspacePath!, "data", "sample.csv")).Should().BeTrue();
        Directory.Exists(Path.Combine(entry.WorkspacePath!, "hidden")).Should().BeFalse();
        entry.WorkspacePath.Should().Contain(RunWorkspace.LeafFor(entry.RunKey));
    }

    [Fact]
    public async Task TheSeedWordIsSubstitutedIntoTheMessageTheHostReceives()
    {
        var host = new ScriptedHost();

        _ = await SweepAsync(WriteTask(), host);

        host.Messages.Should().ContainSingle().Which.Should().Contain("Code name aurora");
    }

    [Fact]
    public async Task ATaskWithASteer_SendsItAsASecondMessageAndRecordsWhen()
    {
        // The host stays busy until the correction arrives, which is the situation the steer family
        // exists to measure: the cut has to survive a goal change mid-run.
        var host = new ScriptedHost { CompleteAfterMessages = 2 };

        var entry = (await SweepAsync(WriteTask(steer: "Correction: 15 rows."), host)).Single();

        host.Messages.Should().HaveCount(2);
        host.Messages[1].Should().Be("Correction: 15 rows.");
        entry.SteerSentAt.Should().NotBeNull();
        entry.SteerMidRun.Should().BeTrue("the run was still going when the correction landed");
        entry.Status.Should().Be(RunOutcomes.Completed);
    }

    [Fact]
    public async Task ARunThatFinishesBeforeTheCorrectionIsDue_StillReceivesItAsAFollowUp()
    {
        // A fast model answers before the user changes their mind; the correction still has to be
        // honoured, it just lands as the next turn instead of mid-run. The row records which.
        var host = new ScriptedHost { CompleteAfterMessages = 1 };

        var entry = (await SweepAsync(WriteTask(steer: "Correction: 15 rows.", steerAfterSeconds: 60), host)).Single();

        host.Messages.Should().HaveCount(2);
        host.Messages[1].Should().Be("Correction: 15 rows.");
        entry.SteerSentAt.Should().NotBeNull();
        entry.SteerMidRun.Should().BeFalse("the first answer was already terminal when it was sent");
        entry.Status.Should().Be(RunOutcomes.Completed);
    }

    [Fact]
    public async Task ASteerTriggeredOnTheFirstAnswer_IsNeverSentWhileTheFirstAnswerIsStillRunning()
    {
        // The defect this replaces: the two-wave tasks release their second wave on a 240-second clock,
        // so a run slower than that had "now read pages 06-10" injected while it was still reading pages
        // 01-05. The premise the task is built on — wave one is DONE and only its summary carries it —
        // was silently false for exactly the arms that slow a run down.
        var host = new ScriptedHost { CompleteAfterMessages = 1, StatusPollsBeforeTerminal = 3 };

        var entry = (
            await SweepAsync(
                WriteTask(
                    steer: "Thanks. Now read pages 06-10.",
                    steerAfterSeconds: 0,
                    steerAfter: new SteerTrigger { Kind = SteerTriggerKinds.FirstAnswer }
                ),
                host
            )
        ).Single();

        host.Messages.Should().HaveCount(2);
        host.MessagesWhileRunning.Should()
            .Be(1, "only the first message may arrive before the first answer is terminal");
        entry.SteerMidRun.Should().BeFalse("the correction waited for the answer rather than for a clock");
        entry.Status.Should().Be(RunOutcomes.Completed);
    }

    [Fact]
    public async Task ASteerTriggeredOnToolCalls_WaitsForTheCountNotTheClock()
    {
        // steerAfterSeconds is 0, so the clock would fire the correction immediately. The trigger holds
        // it until the agent has actually made three Read calls.
        var host = new ScriptedHost { CompleteAfterMessages = 2, ToolCallsPerPoll = 1 };

        var entry = (
            await SweepAsync(
                WriteTask(
                    steer: "Correction: 15 rows.",
                    steerAfterSeconds: 0,
                    steerAfter: new SteerTrigger
                    {
                        Kind = SteerTriggerKinds.ToolCalls,
                        Tool = "Read",
                        Count = 3,
                    }
                ),
                host
            )
        ).Single();

        host.Messages.Should().HaveCount(2);
        host.ReadCountWhenSteerArrived.Should().BeGreaterThanOrEqualTo(3, "the count is what released it");
        entry.SteerMidRun.Should().BeTrue("the run was still going when the count was reached");
        entry.Status.Should().Be(RunOutcomes.Completed);
    }

    [Fact]
    public async Task ASteerTriggeredOnToolCallsTheRunNeverReaches_StillLandsAsAFollowUp()
    {
        // The first answer stays the backstop: a run that finishes without ever making the calls still
        // owes the second half of its task, exactly as under the clock.
        var host = new ScriptedHost { CompleteAfterMessages = 1, ToolCallsPerPoll = 0 };

        var entry = (
            await SweepAsync(
                WriteTask(
                    steer: "Correction: 15 rows.",
                    steerAfterSeconds: 0,
                    steerAfter: new SteerTrigger
                    {
                        Kind = SteerTriggerKinds.ToolCalls,
                        Tool = "Read",
                        Count = 99,
                    }
                ),
                host
            )
        ).Single();

        host.Messages.Should().HaveCount(2);
        entry.SteerMidRun.Should().BeFalse("nothing but the first answer could release it");
        entry.Status.Should().Be(RunOutcomes.Completed);
    }

    [Fact]
    public async Task TheCheckerJudgesTheWorkspaceTheRunLeftBehind()
    {
        var checker = new FakeChecker(
            new J1Result
            {
                Outcome = "partial",
                Score = 0.5,
                FailedChecks = ["test_two"],
            }
        );

        var entry = (await SweepAsync(WriteTask(), new ScriptedHost(), checker)).Single();

        checker.JudgedWorkspace.Should().Be(entry.WorkspacePath);
        entry.J1!.Outcome.Should().Be("partial");
        entry.J1.FailedChecks.Should().Equal("test_two");
    }

    [Fact]
    public async Task ARunThatEndedBadlyIsStillJudged_AndCarriesTheFloorThatWillInvalidateIt()
    {
        var checker = new FakeChecker(new J1Result { Outcome = "fail", Score = 0 });

        var entry = (await SweepAsync(WriteTask(), new ScriptedHost { TerminalStatus = "Errored" }, checker)).Single();

        entry.Status.Should().Be(RunOutcomes.Errored);
        entry.J1.Should().NotBeNull("the workspace is still evidence worth scoring");
        entry.MinCompactions.Should().Be(1);
        entry.VariantCompacts.Should().BeTrue();
    }

    // ── the row it produces ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheRunRowCarriesBothJudgingLayers()
    {
        var checker = new FakeChecker(new J1Result { Outcome = "pass", Score = 1.0 });
        var entries = await SweepAsync(WriteTask(), new ScriptedHost(), checker);

        // No conversation store: the extractor still produces the row, which is what an archived
        // sweep re-extracts from months later.
        var metrics = MetricsExtractor.Extract(Path.Combine(_root, "no-conversations"), entries, expectedBoard: null);
        var runsPath = Path.Combine(_root, "runs.jsonl");
        ResultsWriter.WriteRunsJsonl(runsPath, metrics.Runs);

        var row = JsonDocument.Parse(File.ReadAllLines(runsPath).Single()).RootElement;
        row.GetProperty("j1").GetProperty("outcome").GetString().Should().Be("pass");
        row.GetProperty("j1").GetProperty("score").GetDouble().Should().Be(1.0);
        row.GetProperty("valid").GetBoolean().Should().BeFalse("no store means no compactions, below the floor");
        row.GetProperty("variant").GetString().Should().Be("compact-v0");
        row.GetProperty("task").GetString().Should().Be("c1");
    }

    [Fact]
    public async Task ATaskWithNoFixturesKeepsTheSweepsSharedWorkspaceAndHasNoJ1()
    {
        // The legacy single-task layout, unchanged: no per-run directory, nothing to check.
        var task = new EvalTaskAsset
        {
            Id = null,
            Dir = _root,
            Template = "Do {TOPIC}.",
            ExpectedBoard = null,
        };

        var entry = (await SweepAsync(task, new ScriptedHost(), new FakeChecker(null))).Single();

        entry.WorkspacePath.Should().BeNull();
        entry.J1.Should().BeNull();
        entry.RunKey.Should().Be("compact-v0/m1/seed0");
    }

    private sealed class FakeChecker(J1Result? verdict) : ITaskChecker
    {
        public string? JudgedWorkspace { get; private set; }

        public Task<J1Result?> JudgeAsync(
            EvalTaskAsset task,
            string workspacePath,
            string outputDir,
            CancellationToken ct
        )
        {
            JudgedWorkspace = workspacePath;
            return Task.FromResult(verdict);
        }
    }

    /// <summary>
    /// A host that provisions and accepts messages, and reports the run complete only once it has
    /// received <see cref="CompleteAfterMessages"/> of them — so a test can hold a run "active" until
    /// the correction lands.
    /// </summary>
    private sealed class ScriptedHost : HttpMessageHandler
    {
        public int CompleteAfterMessages { get; init; } = 1;

        /// <summary>The status reported once enough messages have arrived.</summary>
        public string TerminalStatus { get; init; } = "Completed";

        /// <summary>Status polls answered "Running" before the terminal status is reported at all.</summary>
        public int StatusPollsBeforeTerminal { get; init; }

        /// <summary>Read tool calls the transcript gains on every status poll, so work accrues over time.</summary>
        public int ToolCallsPerPoll { get; init; }

        public List<string> Messages { get; } = [];

        /// <summary>How many messages arrived while the first answer was still Running.</summary>
        public int MessagesWhileRunning { get; private set; }

        /// <summary>The Read count the transcript showed when the correction was posted.</summary>
        public int ReadCountWhenSteerArrived { get; private set; } = -1;

        private int _statusPolls;
        private int _reads;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path.EndsWith("/messages", StringComparison.Ordinal))
            {
                lock (Messages)
                {
                    var rows = string.Join(
                        ",",
                        Enumerable
                            .Range(0, _reads)
                            .Select(_ =>
                                """{"messageType":"ToolCallMessage","messageJson":"{\"function_name\":\"Read\"}"}"""
                            )
                    );
                    return Json($"[{rows}]");
                }
            }

            if (request.Method == HttpMethod.Post && path == "/api/workspaces")
            {
                return Json("""{"id":"ws-run"}""");
            }

            if (request.Method == HttpMethod.Get && path == "/api/workspaces")
            {
                return Json("""{"workspaces":[]}""");
            }

            if (request.Method == HttpMethod.Post && path == "/api/conversations")
            {
                return Json("""{"threadId":"t-1"}""");
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/messages", StringComparison.Ordinal))
            {
                var body = await request.Content!.ReadAsStringAsync(ct);
                var text = JsonDocument.Parse(body).RootElement.GetProperty("text").GetString()!;
                lock (Messages)
                {
                    if (Messages.Count >= 1 && ReadCountWhenSteerArrived < 0)
                    {
                        ReadCountWhenSteerArrived = _reads;
                    }

                    var firstAnswerIsTerminal =
                        Messages.Count >= CompleteAfterMessages && _statusPolls > StatusPollsBeforeTerminal;
                    if (!firstAnswerIsTerminal)
                    {
                        MessagesWhileRunning++;
                    }

                    Messages.Add(text);
                }

                return Json($$"""{"inputId":"i-{{Messages.Count}}"}""");
            }

            lock (Messages)
            {
                _statusPolls++;
                _reads += ToolCallsPerPoll;
            }

            var done = Messages.Count >= CompleteAfterMessages && _statusPolls > StatusPollsBeforeTerminal;
            return Json(done ? $$"""{"status":"{{TerminalStatus}}","runId":"r-1"}""" : """{"status":"Running"}""");
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
    }
}
