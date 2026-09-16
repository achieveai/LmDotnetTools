using TodoEval.Runner.Sweep;

namespace TodoEval.Runner.Tests;

/// <summary>
/// The task axis as it reaches disk: which <c>task.md</c> a task id resolves to, what a missing
/// <c>expected-board.json</c> means, and the promise that configuring no tasks keeps the original
/// single-file layout untouched.
/// </summary>
public class EvalAssetsTaskTests : IDisposable
{
    private readonly string _evalDir = Path.Combine(Path.GetTempPath(), $"todo-eval-assets-{Guid.NewGuid():N}");

    public EvalAssetsTaskTests()
    {
        Directory.CreateDirectory(_evalDir);
        File.WriteAllText(Path.Combine(_evalDir, "mode.json"), """{"name":"probe-eval","systemPrompt":"be helpful"}""");
    }

    public void Dispose()
    {
        if (Directory.Exists(_evalDir))
        {
            Directory.Delete(_evalDir, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private void WriteTask(string? id, string body, string? expectedBoard = null)
    {
        var dir = id is null ? _evalDir : Path.Combine(_evalDir, EvalAssets.TasksDirName, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.md"), body);
        if (expectedBoard is not null)
        {
            File.WriteAllText(Path.Combine(dir, "expected-board.json"), expectedBoard);
        }
    }

    private const string MinimalBoard = """{"minTopLevel":1}""";

    // ── the single-task layout ───────────────────────────────────────────────────────────────────

    [Fact]
    public void NoTaskIds_ReadsTheEvalDirsOwnTaskMarkdown()
    {
        WriteTask(id: null, "header\n---\nPlan {TOPIC}.", MinimalBoard);

        var assets = EvalAssets.Load(_evalDir, "probe-eval");

        var task = assets.Tasks.Should().ContainSingle().Subject;
        task.Id.Should().BeNull("the unnamed task contributes no segment to a run key");
        task.Template.Should().Be("Plan {TOPIC}.", "only the part below the '---' marker is sent");
        task.ExpectedBoard.Should().NotBeNull();
    }

    [Fact]
    public void NoTaskIds_MissingTaskMarkdown_Throws()
    {
        var act = () => EvalAssets.Load(_evalDir, "probe-eval");

        act.Should().Throw<FileNotFoundException>().WithMessage("*task.md*");
    }

    // ── the tasks/ layout ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TaskIds_ResolveUnderTheTasksDirectory_InTheConfiguredOrder()
    {
        WriteTask("c1", "Do C1 about {TOPIC}.", MinimalBoard);
        WriteTask("d1", "Do D1 about {TOPIC}.");

        var assets = EvalAssets.Load(_evalDir, "probe-eval", ["c1", "d1"]);

        assets.Tasks.Select(t => t.Id).Should().Equal("c1", "d1");
        assets.Tasks[0].Template.Should().Be("Do C1 about {TOPIC}.");
    }

    [Fact]
    public void ATaskWithoutAnExpectedBoard_HasNoBoardGate()
    {
        // Not an error and not a failure: the completion criterion is simply unproven for its runs,
        // which the gate table reports as not measurable.
        WriteTask("d1", "Do D1.");

        var assets = EvalAssets.Load(_evalDir, "probe-eval", ["d1"]);

        assets.Tasks.Single().ExpectedBoard.Should().BeNull();
    }

    [Fact]
    public void AnUnknownTaskId_ThrowsNamingThePathItLookedIn()
    {
        // A typo'd id must not silently sweep nothing; the message has to say where to put the file.
        WriteTask("c1", "Do C1.");

        var act = () => EvalAssets.Load(_evalDir, "probe-eval", ["c1", "typo"]);

        act.Should().Throw<FileNotFoundException>().WithMessage("*typo*");
    }

    [Fact]
    public void TaskIds_DoNotReadTheEvalDirsOwnTaskMarkdown()
    {
        // The two layouts are exclusive: a stale top-level task.md must not leak into a tasks sweep.
        WriteTask(id: null, "the WRONG task");
        WriteTask("c1", "the right task");

        var assets = EvalAssets.Load(_evalDir, "probe-eval", ["c1"]);

        assets.Tasks.Should().ContainSingle().Which.Template.Should().Be("the right task");
    }

    // ── the sweep plan the task axis produces ────────────────────────────────────────────────────

    [Fact]
    public async Task ASweep_RunsEveryTaskModelSeedCellAgainstOneHost()
    {
        var config = new EvalRunnerConfig
        {
            Models = ["m1", "m2"],
            Topics = ["a topic"],
            Seeds = 2,
            PerRunTimeoutMinutes = 1,
        };
        using var http = new HttpClient(new AlwaysCompletesHandler()) { BaseAddress = new Uri("http://127.0.0.1:9/") };
        var runner = new SweepRunner(
            new EvalHostClient(http),
            config,
            "ws-1",
            "mode-1",
            new VariantConfig { Name = "compact-v0" },
            [TaskAsset("c1"), TaskAsset("d1")],
            TextWriter.Null
        );
        var manifestPath = Path.Combine(_evalDir, "runs-manifest.jsonl");

        var entries = await runner.RunSweepAsync(manifestPath, CancellationToken.None);

        entries.Should().HaveCount(8, "2 tasks x 2 models x 2 seeds");
        entries.Select(e => e.RunKey).Should().OnlyHaveUniqueItems();
        entries.Should().OnlyContain(e => e.Variant == "compact-v0");
        entries.Select(e => e.Task).Distinct().Should().BeEquivalentTo(["c1", "d1"]);
        entries
            .Should()
            .Contain(e => e.RunKey == "compact-v0/c1/m1/seed0", "every axis that distinguishes a cell is in its key");
        File.ReadAllLines(manifestPath).Should().HaveCount(8);
    }

    [Fact]
    public async Task ADefaultVariantSingleTaskSweep_WritesThePreAxisRunKeys()
    {
        // The legacy promise, end to end: the rows an unchanged sweep produces still carry the keys
        // every archived baseline has, so the two remain diffable.
        var config = new EvalRunnerConfig
        {
            Models = ["m1"],
            Topics = ["a topic"],
            Seeds = 2,
            PerRunTimeoutMinutes = 1,
        };
        using var http = new HttpClient(new AlwaysCompletesHandler()) { BaseAddress = new Uri("http://127.0.0.1:9/") };
        var runner = new SweepRunner(
            new EvalHostClient(http),
            config,
            "ws-1",
            "mode-1",
            VariantConfig.Default,
            [TaskAsset(id: null)],
            TextWriter.Null
        );

        var entries = await runner.RunSweepAsync(
            Path.Combine(_evalDir, "legacy-manifest.jsonl"),
            CancellationToken.None
        );

        entries.Select(e => e.RunKey).Should().Equal("m1/seed0", "m1/seed1");
        entries.Should().OnlyContain(e => e.Task == null);
    }

    private static EvalTaskAsset TaskAsset(string? id) =>
        new()
        {
            Id = id,
            Template = "Do {TOPIC}.",
            ExpectedBoard = null,
        };

    /// <summary>A host that provisions, accepts and completes every conversation on the first poll.</summary>
    private sealed class AlwaysCompletesHandler : HttpMessageHandler
    {
        private int _threads;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body =
                request.Method == HttpMethod.Post && path == "/api/conversations"
                    ? $$"""{"threadId":"t-{{Interlocked.Increment(ref _threads)}}"}"""
                : path.EndsWith("/messages", StringComparison.Ordinal) ? """{"inputId":"i-1"}"""
                : """{"status":"Completed","runId":"r-1"}""";

            return Task.FromResult(
                new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
                }
            );
        }
    }
}
