using TodoEval.Runner.Metrics;
using TodoEval.Runner.Sweep;

namespace TodoEval.Runner.Tests;

/// <summary>
/// The acceptance pins for #670's coordination dimension, taken over the committed
/// <c>fixtures/coordination-run</c> store that both scorers read.
/// </summary>
public class CoordinationMetricsTests
{
    private static ConversationStoreReader.ThreadData Primary() =>
        ConversationStoreReader.LoadThread(
            Path.Combine(RepoPaths.FixtureConversations("coordination-run"), "thread-fixture-coord")
        );

    private static RunMetrics Run()
    {
        var metrics = MetricsExtractor.Extract(
            RepoPaths.FixtureConversations("coordination-run"),
            [
                new RunManifestEntry
                {
                    RunKey = "fixture/seed0",
                    Model = "fixture-model",
                    SeedIndex = 0,
                    Topic = "coordination",
                    Status = RunOutcomes.Completed,
                    ThreadId = "thread-fixture-coord",
                },
            ],
            expectedBoard: null
        );

        return metrics.Runs.Should().ContainSingle().Subject;
    }

    [Fact]
    public void EveryCoordinationTool_GetsItsOwnPerToolRow()
    {
        var run = Run();

        run.PerTool.Should().HaveCount(22);
        foreach (var tool in CoordinationTools.All)
        {
            run.PerTool.Should().ContainKey(tool);
            run.PerTool[tool].Family.Should().Be("coordination");
        }

        foreach (var tool in TaskTools.All)
        {
            run.PerTool[tool].Family.Should().Be("task");
        }
    }

    [Fact]
    public void CoordinationCallsAndRefusals_AreCountedSeparatelyFromBoardWork()
    {
        var run = Run();

        run.TotalToolCalls.Should().Be(16);
        run.CoordinationToolCalls.Should().Be(15);
        run.CoordinationToolErrors.Should().Be(5);
        run.TaskToolCalls.Should().Be(1, "the sub-agent made one add-task call");
        run.TaskToolErrors.Should().Be(0);
    }

    [Fact]
    public void ACoordinationRefusal_IsAnError_EvenThoughItsTextIsNotErrorPrefixed()
    {
        // The whole point of the family split: "No agent named 'agent-9' is registered." would be a
        // SUCCESS under the task family's text-prefix rule. is_error + error_code is what marks it.
        var thread = Primary();

        thread.PerTool["WaitForAgents"].Calls.Should().Be(3);
        thread.PerTool["WaitForAgents"].Errors.Should().Be(3);
        thread
            .PerTool["WaitForAgents"]
            .ErrorCodes.Should()
            .Equal(new Dictionary<string, int> { ["unknown_agent"] = 3 });
    }

    [Fact]
    public void ErrorCode_FallsBackToTheResultsCodeProperty_ThenToUnclassified()
    {
        var thread = Primary();

        // (b) no error_code on the message, but the result parses to an object carrying `code`.
        thread.PerTool["SendMessage"].ErrorCodes.Should().Equal(new Dictionary<string, int> { ["depth_limit"] = 1 });

        // (c) neither: reported as unclassified, never omitted.
        thread.PerTool["CheckAgent"].ErrorCodes.Should().Equal(new Dictionary<string, int> { ["unclassified"] = 1 });
    }

    [Fact]
    public void ThreeIdenticalRefusals_AreOneStorm()
    {
        var run = Run();

        var storm = run.RetryStorms.Should().ContainSingle().Subject;
        storm.Tool.Should().Be("WaitForAgents");
        storm.Count.Should().Be(3);
        storm.ThreadId.Should().Be("thread-fixture-coord");
    }

    [Fact]
    public void FiveIdenticalSuccessfulPolls_AreNeverAStorm()
    {
        // The polling exemption, asserted on the SAME run that produces the storm above, so a zero
        // here cannot be an unreached code path.
        var run = Run();

        run.PerTool["CheckAgents"].Calls.Should().Be(5);
        run.PerTool["CheckAgents"].Errors.Should().Be(0);
        run.RetryStorms.Should().NotContain(s => s.Tool == "CheckAgents");
    }

    [Fact]
    public void WaitOutcomes_CountBothRefusalCodesAndNonErrorStatuses()
    {
        var run = Run();

        run.WaitOutcomes.Should().Equal(new Dictionary<string, int> { ["unknown_agent"] = 3, ["timeout"] = 1 });
    }

    [Fact]
    public void OpenObligations_ReportsNotYetEmitted_RatherThanABareZero()
    {
        var run = Run();

        run.OpenObligations.ResultsCarryingField.Should().Be(0);
        run.OpenObligations.LastObserved.Should().Be(0);
        run.OpenObligations.Note.Should().Be(OpenObligationsReport.NotYetEmittedNote);
    }

    [Fact]
    public void Usage_IsDedupedAcrossBags_AndRolledUpByKindAndAgent()
    {
        var run = Run();

        // The primary attempt appears in BOTH bags; counting it twice would double the run.
        run.Usage.DuplicateAttemptIds.Should().Be(1);
        run.Usage.Totals.Records.Should().Be(3);
        run.Usage.Totals.TotalTokens.Should().Be(1500 + 500 + 70);

        run.Usage.ByExecutionKind.Should().ContainKeys("Primary", "SubAgent");
        run.Usage.ByExecutionKind["SubAgent"].TotalTokens.Should().Be(500);
        run.Usage.ByAgent["subagent-fixture-coord-0001"].TotalTokens.Should().Be(500);
    }

    [Fact]
    public void TurnJoin_CountsASyntheticAttemptKeyAsUnattributed()
    {
        var run = Run();

        run.Usage.AttributedTurnTokens.Should().Be(2000);
        run.Usage.UnattributedTurnTokens.Should().Be(70, "derived:8f21 can never match a generation id");
        run.Usage.KindsNotEmitted.Should().Contain("Continuation");
    }

    [Fact]
    public void SpawnTimingsAndStartupWork_AreReadBackFromThePersistedStamps()
    {
        var run = Run();

        var timing = run.SpawnTimings[0];
        timing.AgentId.Should().Be("agent-1");
        timing.ToolRegistryMs.Should().Be(37);
        timing.ToolCatalogBytes.Should().Be(18432);
        timing.Reconstructed.Should().BeFalse("a fresh spawn must be tellable from a rebuilt one");
        run.SpawnTimings.Should().HaveCount(3).And.OnlyHaveUniqueItems(t => t.AgentId);

        run.StartupWork.Should().NotBeNull();
        run.StartupWork!.TemplateCatalogBuilds.Should().Be(11);
        run.StartupWork.Reconstructions.Should().Be(1);
        run.StartupWork.TemplateCatalogBytes.Should().Be(47300);
        run.StartupWork.DirectoryListingBytes.Should().Be(412);
    }

    [Fact]
    public void SharedSinkStampedOnEveryThread_IsCountedOnce_NotOncePerThread()
    {
        // The fixture is the shape production actually produces: ONE cumulative sink, shared down the
        // hierarchy by SubAgentOptions.ForChildLoop, stamped by each collaborating loop onto its OWN
        // thread - so the sub-agent thread carries an earlier PREFIX of the very same series (1 spawn,
        // 4 catalog builds) that the root thread carries in full (3 spawns, 11 builds).
        //
        // Concatenating those stamps reported 4 spawns for a run that did 3, and first-wins reported the
        // sub-agent's mid-run roll-up because `subagent-` sorts before `thread-`. The two errors point in
        // OPPOSITE directions, so neither number contradicts the other and nothing looks wrong. The eval
        // dispatches one sub-agent per workstream, so the archived baseline would have carried roughly
        // one multiplied spawn cost per thread, and every later "the wave shrank spawn cost" claim would
        // have been measured against it.
        var run = Run();

        run.SpawnTimings.Should()
            .HaveCount(3, "the run spawned three agents; a fourth entry would be the child's repeat of agent-1");
        run.SpawnTimings.Sum(t => t.TotalMs)
            .Should()
            .Be(174, "the total must be the run's, not the run's multiplied by the thread count");
        run.StartupWork!.Spawns.Should().Be(3, "not the 1 the sub-agent's earlier stamp recorded");
        run.StartupWork.TemplateCatalogBytes.Should().Be(47300, "not the 8600 of that partial stamp");

        // The two artifacts must come from the SAME stamp, or the report describes two different moments.
        run.SpawnTimings.Count.Should().Be(run.StartupWork.Spawns);
    }

    [Fact]
    public void RunStaysValid_BecauseTheSubAgentTouchedTheBoard()
    {
        Run().Validity.Valid.Should().BeTrue();
    }
}
