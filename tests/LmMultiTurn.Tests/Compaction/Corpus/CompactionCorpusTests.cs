using System.Collections.Concurrent;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;
using FluentAssertions;
using LmMultiTurn.Tests.Persistence;
using Xunit;

namespace LmMultiTurn.Tests.Compaction.Corpus;

/// <summary>
/// Collects every scenario×mode row the class produced and writes the report (JSON + markdown) once the
/// class is done (D3). A partial run (filtered tests) writes a partial report; the committed copy is
/// regenerated from the full matrix.
/// </summary>
public sealed class CorpusReportFixture : IDisposable
{
    private readonly ConcurrentBag<ScenarioModeResult> _results = [];

    public void Add(ScenarioModeResult result) => _results.Add(result);

    public void Dispose()
    {
        if (_results.IsEmpty)
        {
            return;
        }

        var report = new CorpusReport
        {
            EvaluatorVersion = CorpusEvaluator.Version,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Fingerprints = CorpusScenarios.All.ToDictionary(s => s.Id, s => s.Fingerprint(), StringComparer.Ordinal),
            Results =
            [
                .. _results
                    .OrderBy(r => r.ScenarioId, StringComparer.Ordinal)
                    .ThenBy(r => r.Mode, StringComparer.Ordinal),
            ],
        };
        var dir = CorpusPaths.ReportDirectory;
        _ = Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "compaction-corpus-results.json"),
            JsonSerializer.Serialize(report, CorpusReport.Json)
        );
        File.WriteAllText(Path.Combine(dir, "compaction-corpus-results.md"), report.ToMarkdown());
    }
}

/// <summary>
/// The corpus (#686, spec 679 §12.4) run in Off, Shadow and Compact against fingerprint-pinned inputs.
/// Every row asserts the four zero-tolerance invariants (AC 4) and the mode's contract; the numbers go
/// to the report (AC 3) and are never asserted here except where the scenario names an expectation.
/// </summary>
[Collection("CompactionCorpus")]
public sealed class CompactionCorpusTests(CorpusReportFixture report)
    : IClassFixture<CorpusReportFixture>,
        IAsyncLifetime
{
    private readonly ConversationStoreHarness _harness = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _harness.DisposeAsync();

    public static TheoryData<string, CompactionMode> Matrix
    {
        get
        {
            var data = new TheoryData<string, CompactionMode>();
            foreach (var scenario in CorpusScenarios.All)
            {
                foreach (var mode in new[] { CompactionMode.Off, CompactionMode.Shadow, CompactionMode.Compact })
                {
                    data.Add(scenario.Id, mode);
                }
            }

            return data;
        }
    }

    private sealed record FingerprintManifest(string EvaluatorVersion, IReadOnlyDictionary<string, string> Scenarios);

    [Fact]
    public void Fingerprints_MatchTheCommittedManifest()
    {
        // D2: an edited scenario changes its fingerprint; the corpus refuses to run against inputs the
        // manifest does not name until the manifest is deliberately regenerated (copy the actual file
        // the failure names).
        var actual = new FingerprintManifest(
            CorpusEvaluator.Version,
            CorpusScenarios.All.ToDictionary(s => s.Id, s => s.Fingerprint(), StringComparer.Ordinal)
        );
        var options = new JsonSerializerOptions { WriteIndented = true };
        var actualJson = JsonSerializer.Serialize(actual, options);
        _ = Directory.CreateDirectory(CorpusPaths.ReportDirectory);
        var actualPath = Path.Combine(CorpusPaths.ReportDirectory, "corpus.fingerprints.json");
        File.WriteAllText(actualPath, actualJson);

        File.Exists(CorpusPaths.FingerprintsFile)
            .Should()
            .BeTrue($"the committed manifest is missing; copy {actualPath} to {CorpusPaths.FingerprintsFile}");
        var committed = JsonSerializer.Deserialize<FingerprintManifest>(
            File.ReadAllText(CorpusPaths.FingerprintsFile),
            options
        )!;
        committed.EvaluatorVersion.Should().Be(CorpusEvaluator.Version);
        committed
            .Scenarios.Should()
            .Equal(
                actual.Scenarios,
                $"a scenario changed; if that was deliberate copy {actualPath} over the committed manifest"
            );
        CorpusScenarios
            .All.Select(s => s.Id)
            .Should()
            .Equal("a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m");
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Corpus_HoldsEveryInvariant_InEveryMode(string scenarioId, CompactionMode mode)
    {
        var scenario = CorpusScenarios.ById(scenarioId);
        await using var runner = new CorpusRunner(scenario, mode, _harness);

        var (result, data) = await runner.RunAsync();
        report.Add(result);

        // AC 4: zero invalid tool pairs, zero protected-state loss, zero cross-thread reads, zero raw-history loss.
        result.Invariants.InvalidToolPairs.Should().Be(0);
        result.Invariants.ProtectedStateLoss.Should().BeEmpty();
        result.Invariants.CrossThreadReads.Should().Be(0, "{0}", string.Join("; ", data.CrossThread));
        result.Invariants.RawHistoryLoss.Should().BeEmpty();

        // Task outcome: what the scenario says this mode should achieve.
        result
            .TaskSuccess.Should()
            .Be(
                result.ExpectedSuccess,
                "runs: {0}",
                string.Join(
                    "; ",
                    data.Runs.Select(r => $"{r.Input[..Math.Min(20, r.Input.Length)]}={r.IsError}/{r.Error}")
                )
            );

        switch (mode)
        {
            case CompactionMode.Off:
                data.RootMessages.Should().NotContain(m => m is CompactionCheckpointMessage);
                data.RootState.Should().BeNull("Off writes no compaction state");
                data.Decided.Should().BeEmpty("Off runs no policy");
                data.Root.Requests.Should().OnlyContain(r => !CorpusEvaluator.HasEnvelope(r));
                break;
            case CompactionMode.Shadow:
                result.CheckpointsActivated.Should().Be(0, "Shadow never rewrites the provider input");
                result.ChildCheckpointsActivated.Should().Be(0);
                data.Root.Requests.Should().OnlyContain(r => !CorpusEvaluator.HasEnvelope(r));
                if (scenario.ExpectRootCompaction)
                {
                    result
                        .ShadowCheckpoints.Should()
                        .BeGreaterThan(0, "Shadow records the checkpoint it would have applied");
                }

                break;
            case CompactionMode.Compact:
                if (scenario.ExpectRootCompaction)
                {
                    result.CheckpointsActivated.Should().BeGreaterThan(0);
                    data.Root.Requests.Should().Contain(r => CorpusEvaluator.HasEnvelope(r));
                }
                else
                {
                    result.CheckpointsActivated.Should().Be(0);
                }

                if (scenario.ExpectChildCompaction)
                {
                    result.ChildCheckpointsActivated.Should().BeGreaterThan(0, "the child compacts its own thread (d)");
                }

                break;
            case CompactionMode.Warn:
            default:
                throw new NotSupportedException($"the corpus runs Off, Shadow and Compact, not {mode}");
        }

        if (scenario.MustSkipWith is { } reason && mode != CompactionMode.Off)
        {
            result.Reasons.Keys.Should().Contain(reason);
        }

        // Cost completeness follows the price list (l, m), never silently "complete".
        var expectedCompleteness = scenario.Pricing switch
        {
            CorpusPricing.None => CostCompleteness.Unavailable,
            CorpusPricing.NoCacheRates => result.TotalCachedTokens > 0
                ? CostCompleteness.Partial
                : CostCompleteness.Complete,
            _ => CostCompleteness.Complete,
        };
        result.CostCompleteness.Should().Be(expectedCompleteness.ToString());

        if (scenario.ParksAtCall is { } parksAt)
        {
            AssertParkedRunBuiltNoRequest(scenario, parksAt, data);
        }

        AssertScenarioSpecifics(scenario, mode, result, data);
    }

    /// <summary>
    /// D7 / R6: the reply at <see cref="CorpusScenario.ParksAtCall"/> parks the run; the next step arrives
    /// while it is parked. The park is enforced one seam earlier than the cut selector: no provider request
    /// is built for the parked call, and the boundary-splitting check in the evaluator proves no cut landed
    /// between the parked call row and its result row in any mode.
    /// </summary>
    private static void AssertParkedRunBuiltNoRequest(CorpusScenario scenario, int parksAt, CorpusRunData data)
    {
        data.CallsAtStep.Should().HaveCountGreaterThanOrEqualTo(2);
        data.CallsAtStep[0].Should().Be(parksAt, "the first step ends parked on the reply at call {0}", parksAt);
        // A run arriving during the park is refused before any request is built (the loop's own guard).
        scenario.Steps[1].ExpectError.Should().BeTrue("the corpus pins the refusal as the expected outcome");
        data.CallsAtStep[1].Should().Be(parksAt, "the refused run must not have reached the provider");
        data.Runs[1].IsError.Should().BeTrue();
        data.Runs[1].Error.Should().Contain("still deferred");
        // The resolution (an answer, the timer) resumes the run and the provider is called again.
        data.Root.CallCount.Should().BeGreaterThan(parksAt, "the resumption reaches the provider");

        data.Root.Requests.Should().HaveCount(data.Root.CallCount);
        var parked = ScriptedProvider.Expand(data.Root.Requests[parksAt - 1]);
        parked
            .Should()
            .NotContain(
                m =>
                    m is ToolCallMessage
                    && ((ToolCallMessage)m).ToolCallId == scenario.Root.Replies[parksAt - 1].ToolCallId,
                "the parked call is not yet in the request that produced it"
            );
        foreach (var later in data.Root.Requests.Skip(parksAt))
        {
            CorpusEvaluator
                .InvalidPairs(ScriptedProvider.Expand(later))
                .Should()
                .Be(0, "every request after the park carries the parked call with its result");
        }
    }

    private static void AssertScenarioSpecifics(
        CorpusScenario scenario,
        CompactionMode mode,
        ScenarioModeResult result,
        CorpusRunData data
    )
    {
        switch (scenario.Id)
        {
            case "c" when mode == CompactionMode.Compact:
                // One agent was still running at the cut: its roster entry is carried as non-terminal.
                var checkpoints = CorpusEvaluator.ActivatedCheckpoints(data.RootMessages, data.RootState);
                checkpoints
                    .Should()
                    .Contain(
                        cp => cp.Manifest.Agents.Any(a => a.Status != "Completed"),
                        "agent-3 was non-terminal at the cut"
                    );
                checkpoints.Should().OnlyContain(cp => cp.Manifest.Agents.Count == 3);
                break;
            case "i" when mode == CompactionMode.Compact:
                // The loop that came up after the restart adopted the checkpoint instead of rebuilding it.
                var afterRestart = data.Root.Requests.Skip(scenario.Root.Replies.Count).ToList();
                afterRestart.Should().NotBeEmpty().And.OnlyContain(r => CorpusEvaluator.HasEnvelope(r));
                break;
            case "j" when mode == CompactionMode.Compact:
                result.Notes.Should().Contain("recall returned the compacted instruction verbatim");
                break;
            case "k":
                data.RootRows.Should().OnlyContain(r => r.Seq != null, "the first append backfilled every legacy row");
                data.RootRows.Take(scenario.LegacyRows.Count)
                    .Select(r => r.Id)
                    .Should()
                    .Equal(scenario.LegacyRows.Select((_, i) => $"legacy-{i + 1}"));
                break;
            case "l":
                result.CheckpointsActivated.Should().Be(0);
                result.CostMicros.Should().BeNull();
                break;
            default:
                break;
        }
    }
}
