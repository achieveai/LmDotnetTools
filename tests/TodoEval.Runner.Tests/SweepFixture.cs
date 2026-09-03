using TodoEval.Runner.Metrics;
using TodoEval.Runner.Sweep;

namespace TodoEval.Runner.Tests;

/// <summary>
/// Builds archived sweep directories for the comparison tests.
/// </summary>
/// <remarks>
/// Every artifact is produced by the PRODUCTION writer that would produce it in a real sweep
/// (<see cref="SweepManifest.Write"/>, <see cref="ResultsWriter.WriteRunsJsonl"/>) rather than by
/// hand-written JSON. A hand-built archive can encode a state no writer can reach, and a test
/// standing on such a fixture passes for a reason the product never has.
/// </remarks>
internal sealed class SweepFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"todo-eval-compare-{Guid.NewGuid():N}");

    public const string CorpusHash = "aaaa000000000000000000000000000000000000000000000000000000000000";
    public const string SpecHash = "bbbb000000000000000000000000000000000000000000000000000000000000";
    public const string EvaluatorHash = "cccc000000000000000000000000000000000000000000000000000000000000";
    public const string SpecVersion = "todo-eval/metrics-spec@3";

    public static FingerprintSet Prints(
        string? corpus = null,
        string? spec = null,
        string? evaluator = null,
        string? version = null
    ) =>
        new()
        {
            TaskCorpusHash = corpus ?? CorpusHash,
            SpecHash = spec ?? SpecHash,
            EvaluatorHash = evaluator ?? EvaluatorHash,
            SpecVersion = version ?? SpecVersion,
        };

    /// <summary>Writes a sweep directory the way a real sweep writes one, and returns its path.</summary>
    public string Write(
        string name,
        IReadOnlyList<RunMetrics> runs,
        FingerprintSet? ranUnder = null,
        FingerprintSet? extractedUnder = null,
        bool withManifest = true,
        bool withRuns = true
    )
    {
        var dir = Path.Combine(Root, name);
        Directory.CreateDirectory(dir);
        if (withManifest)
        {
            new SweepManifest
            {
                GitSha = "0123456789abcdef0123456789abcdef01234567",
                RunnerVersion = "1.0.0-test",
                RanUnder = ranUnder ?? Prints(),
                ExtractedUnder = extractedUnder ?? Prints(),
                Models = ["model-a"],
                Seeds = runs.Count,
            }.Write(dir);
        }

        if (withRuns)
        {
            ResultsWriter.WriteRunsJsonl(Path.Combine(dir, ResultsWriter.RunsFileName), runs);
        }

        return dir;
    }

    /// <summary>Loads a written directory back through the production reader.</summary>
    public static SweepSnapshot Load(string directory) => SweepSnapshot.Load(directory);

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}

/// <summary>Score rows for the comparison tests, shaped like the extractor's own output.</summary>
/// <remarks>
/// The derived fields are DERIVED here for the same reason the extractor derives them: a row whose
/// <c>taskToolErrors</c> disagrees with its own <c>perTool</c> rows, or whose run-level
/// <c>errorCodes</c> is not the merge of those rows, is a state no extraction can produce — and a
/// gate tested against it would be tested against nothing.
/// </remarks>
internal static class TestRuns
{
    /// <summary>
    /// A run that completed the board cleanly. Every knob a gate reads is a parameter, so a test can
    /// move exactly one number and watch exactly one gate change.
    /// </summary>
    public static RunMetrics Run(
        string model = "model-a",
        int seed = 0,
        string status = RunOutcomes.Completed,
        bool valid = true,
        bool? completion = true,
        int addNoteCalls = 100,
        int addNoteErrors = 0,
        int coordinationCalls = 10,
        int coordinationErrors = 0,
        int otherToolCalls = 0,
        int retryStorms = 0,
        int boardIdVanished = 0,
        int openObligations = 0,
        int obligationResults = 0,
        int waitOk = 0,
        int waitUnknownAgent = 0,
        int turns = 10,
        long inputTokens = 0,
        int usageRecords = 0,
        int spawnCatalogBytes = 0,
        int spawns = 0,
        params (string Code, int Count)[] errorCodes
    )
    {
        var waits = new List<KeyValuePair<string, int>>();
        if (waitOk > 0)
        {
            waits.Add(new("ok", waitOk));
        }

        if (waitUnknownAgent > 0)
        {
            waits.Add(new(SweepAggregate.UnknownAgentOutcome, waitUnknownAgent));
        }

        // The supplied codes belong to whichever family actually failed; a failure count with no
        // code, or a code with no failure, is not a shape the extractor can emit.
        var codes = CountMap.From(errorCodes.Select(c => new KeyValuePair<string, int>(c.Code, c.Count)));

        // All 22 rows, zero rows included, exactly as the extractor emits them.
        var perTool = ToolFamilies.RowOrder.ToDictionary(
            tool => tool,
            tool =>
                tool switch
                {
                    SweepAggregate.AddNote => Row(addNoteCalls, addNoteErrors, tool, codes),
                    "WaitForAgents" => Row(coordinationCalls, coordinationErrors, tool, CountMap.Empty),
                    _ => Row(0, 0, tool, CountMap.Empty),
                },
            StringComparer.Ordinal
        );

        return new RunMetrics
        {
            RunKey = $"{model}/seed{seed}",
            Model = model,
            SeedIndex = seed,
            Topic = "t",
            Status = status,
            ThreadId = $"thread-{model}-{seed}",
            Threads = 1,
            Turns = turns,
            PrimaryTurns = turns,
            TotalToolCalls = addNoteCalls + coordinationCalls + otherToolCalls,
            TaskToolCalls = addNoteCalls,
            TaskToolErrors = addNoteErrors,
            CoordinationToolCalls = coordinationCalls,
            CoordinationToolErrors = coordinationErrors,
            PerTool = perTool,
            ErrorCodes = CountMap.Merge(perTool.Values.Select(r => r.ErrorCodes)),
            WaitOutcomes = CountMap.From(waits),
            OpenObligations = OpenObligationsReport.From(openObligations, obligationResults),
            Usage = new UsageReport
            {
                Totals = new UsageTotals { Records = usageRecords, InputTokens = inputTokens },
            },
            SpawnTimings =
            [
                .. Enumerable
                    .Range(0, spawns)
                    .Select(i => new SpawnTiming
                    {
                        AgentId = $"agent-{i + 1}",
                        Template = "general-purpose",
                        ToolCatalogBytes = spawnCatalogBytes,
                    }),
            ],
            RetryStormCount = retryStorms,
            RetryStorms =
            [
                .. Enumerable
                    .Range(0, retryStorms)
                    .Select(i => new RetryStorm
                    {
                        ThreadId = $"thread-{model}-{seed}",
                        Tool = SweepAggregate.AddNote,
                        Count = 3 + i,
                        Args = """{"__argsSha256":"deadbeef"}""",
                    }),
            ],
            BoardIdVanished = BoardIdVanishedReport.From([
                .. Enumerable
                    .Range(0, boardIdVanished)
                    .Select(i => new BoardIdVanish
                    {
                        ThreadId = $"thread-{model}-{seed}",
                        TaskId = $"1.{i}",
                        Tool = "get-task",
                    }),
            ]),
            Completion = completion,
            Validity = RunValidity.From(1, 1, valid ? [] : ["subagent-x"], []),
        };
    }

    /// <summary>
    /// One per-tool row. Failures always carry codes summing to the failure count — the spec's
    /// <c>unclassified</c> when the caller named none — because the extractor never emits a failure
    /// without one.
    /// </summary>
    private static PerToolScore Row(int calls, int errors, string tool, CountMap codes)
    {
        var named = codes.Values.Sum();
        var resolved =
            errors == 0 ? CountMap.Empty
            : named == errors ? codes
            : codes.Add("unclassified", errors - named);

        return new PerToolScore
        {
            Calls = calls,
            Errors = errors,
            ErrorRate = calls > 0 ? Math.Round((double)errors / calls, 4) : 0,
            Family = ToolFamilies.Name(ToolFamilies.Classify(tool)),
            ErrorCodes = resolved,
        };
    }
}
