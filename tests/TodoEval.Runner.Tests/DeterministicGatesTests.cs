using TodoEval.Runner.Metrics;

namespace TodoEval.Runner.Tests;

/// <summary>
/// The gate table, at its boundaries: equal to the threshold passes, one unit worse fails, and a
/// gate with nothing to measure reports UNPROVEN rather than going quietly green.
/// </summary>
public class DeterministicGatesTests
{
    private static GateResult Gate(
        string gateId,
        IReadOnlyList<RunMetrics> baseline,
        IReadOnlyList<RunMetrics> candidate
    ) => Evaluate(baseline, candidate).Single(g => g.GateId == gateId);

    private static IReadOnlyList<GateResult> Evaluate(
        IReadOnlyList<RunMetrics> baseline,
        IReadOnlyList<RunMetrics> candidate
    ) => DeterministicGates.Evaluate(SweepAggregate.Of(baseline), SweepAggregate.Of(candidate));

    private static IReadOnlyList<RunMetrics> One(params RunMetrics[] runs) => runs;

    // --- baseline-derived gates -----------------------------------------------------------------

    [Fact]
    public void TaskToolErrorRate_EqualToTheBaseline_Passes()
    {
        var gate = Gate(
            "task-tool-error-rate",
            One(TestRuns.Run(addNoteCalls: 100, addNoteErrors: 20)),
            One(TestRuns.Run(addNoteCalls: 100, addNoteErrors: 20))
        );

        gate.Outcome.Should().Be(GateOutcome.Passed);
        gate.Threshold.Should().Be(0.2);
        gate.Actual.Should().Be(0.2);
    }

    [Fact]
    public void TaskToolErrorRate_OneErrorWorseThanTheBaseline_Fails()
    {
        var gate = Gate(
            "task-tool-error-rate",
            One(TestRuns.Run(addNoteCalls: 100, addNoteErrors: 20)),
            One(TestRuns.Run(addNoteCalls: 100, addNoteErrors: 21))
        );

        gate.Outcome.Should().Be(GateOutcome.Failed);
    }

    [Fact]
    public void CoordinationErrorRate_OneRefusalWorse_Fails()
    {
        var gate = Gate(
            "coordination-tool-error-rate",
            One(TestRuns.Run(coordinationCalls: 20, coordinationErrors: 2)),
            One(TestRuns.Run(coordinationCalls: 20, coordinationErrors: 3))
        );

        gate.Outcome.Should().Be(GateOutcome.Failed);
    }

    [Fact]
    public void BoardIdVanished_EqualToTheBaseline_Passes_AndOneWorseFails()
    {
        var baseline = One(TestRuns.Run(boardIdVanished: 1));

        Gate("board-id-vanished", baseline, One(TestRuns.Run(boardIdVanished: 1)))
            .Outcome.Should()
            .Be(GateOutcome.Passed);
        Gate("board-id-vanished", baseline, One(TestRuns.Run(boardIdVanished: 2)))
            .Outcome.Should()
            .Be(GateOutcome.Failed);
    }

    [Fact]
    public void CompletionRate_BelowTheBaseline_Fails_AndEqualPasses()
    {
        var baseline = One(TestRuns.Run(seed: 0), TestRuns.Run(seed: 1));

        Gate("completion-rate", baseline, One(TestRuns.Run(seed: 0), TestRuns.Run(seed: 1)))
            .Outcome.Should()
            .Be(GateOutcome.Passed);
        Gate("completion-rate", baseline, One(TestRuns.Run(seed: 0), TestRuns.Run(seed: 1, completion: false)))
            .Outcome.Should()
            .Be(GateOutcome.Failed);
    }

    [Fact]
    public void AverageTurns_OneTurnWorse_Fails()
    {
        Gate("average-turns", One(TestRuns.Run(turns: 10)), One(TestRuns.Run(turns: 10)))
            .Outcome.Should()
            .Be(GateOutcome.Passed);
        Gate("average-turns", One(TestRuns.Run(turns: 10)), One(TestRuns.Run(turns: 11)))
            .Outcome.Should()
            .Be(GateOutcome.Failed);
    }

    // --- absolute #621 targets ------------------------------------------------------------------

    [Fact]
    public void AddNoteErrorRate_AtTheCeiling_Passes_AndOneErrorAboveItFails()
    {
        Gate("add-note-error-rate", One(TestRuns.Run()), One(TestRuns.Run(addNoteCalls: 100, addNoteErrors: 5)))
            .Outcome.Should()
            .Be(GateOutcome.Passed);
        Gate("add-note-error-rate", One(TestRuns.Run()), One(TestRuns.Run(addNoteCalls: 100, addNoteErrors: 6)))
            .Outcome.Should()
            .Be(GateOutcome.Failed);
    }

    [Fact]
    public void AddNoteErrorRate_IsAbsolute_SoAWorseBaselineDoesNotRaiseTheCeiling()
    {
        var gate = Gate(
            "add-note-error-rate",
            One(TestRuns.Run(addNoteCalls: 100, addNoteErrors: 65)),
            One(TestRuns.Run(addNoteCalls: 100, addNoteErrors: 10))
        );

        gate.Threshold.Should().Be(DeterministicGates.AddNoteErrorRateCeiling);
        gate.Outcome.Should().Be(GateOutcome.Failed, "10% still misses the 5% target however bad the baseline was");
    }

    [Fact]
    public void RetryStorms_MustBeZero_HoweverManyTheBaselineHad()
    {
        var baseline = One(TestRuns.Run(retryStorms: 4));

        Gate("retry-storms", baseline, One(TestRuns.Run(retryStorms: 0))).Outcome.Should().Be(GateOutcome.Passed);
        Gate("retry-storms", baseline, One(TestRuns.Run(retryStorms: 1))).Outcome.Should().Be(GateOutcome.Failed);
    }

    // --- criterion 3: equivalent successful runs ------------------------------------------------

    [Fact]
    public void ToolCallsPerSuccessfulRun_AboveTheBaseline_Fails()
    {
        Gate(
            "tool-calls-per-successful-run",
            One(TestRuns.Run(otherToolCalls: 0)),
            One(TestRuns.Run(otherToolCalls: 1))
        )
            .Outcome.Should()
            .Be(GateOutcome.Failed);
    }

    /// <summary>
    /// Criterion 3 says "equivalent successful runs". A run that failed or was invalid did less work
    /// by failing earlier, and counting it would let a sweep look cheaper by getting worse.
    /// </summary>
    [Fact]
    public void ToolCallsPerSuccessfulRun_IgnoresFailedAndInvalidRuns()
    {
        var gate = Gate(
            "tool-calls-per-successful-run",
            One(TestRuns.Run(otherToolCalls: 0)),
            One(
                TestRuns.Run(seed: 0, otherToolCalls: 0),
                TestRuns.Run(seed: 1, completion: false, otherToolCalls: 900),
                TestRuns.Run(seed: 2, valid: false, otherToolCalls: 900)
            )
        );

        gate.Outcome.Should().Be(GateOutcome.Passed);
        gate.Actual.Should().Be(110, "only the one valid completed run counts");
    }

    [Fact]
    public void InputTokensPerSuccessfulRun_AboveTheBaseline_Fails()
    {
        Gate(
            "input-tokens-per-successful-run",
            One(TestRuns.Run(usageRecords: 3, inputTokens: 1000)),
            One(TestRuns.Run(usageRecords: 3, inputTokens: 1001))
        )
            .Outcome.Should()
            .Be(GateOutcome.Failed);
    }

    [Fact]
    public void ToolCatalogBytesPerSpawn_AboveTheBaseline_Fails()
    {
        Gate(
            "tool-catalog-bytes-per-spawn",
            One(TestRuns.Run(spawns: 2, spawnCatalogBytes: 8000)),
            One(TestRuns.Run(spawns: 2, spawnCatalogBytes: 8001))
        )
            .Outcome.Should()
            .Be(GateOutcome.Failed);
    }

    // --- anti-vacuity: nothing to measure is never a pass ---------------------------------------

    [Theory]
    [InlineData("add-note-error-rate")]
    [InlineData("task-tool-error-rate")]
    public void ARateOverZeroCalls_IsNotMeasurable_RatherThanZeroPercent(string gateId)
    {
        var gate = Gate(gateId, One(TestRuns.Run()), One(TestRuns.Run(addNoteCalls: 0)));

        gate.Outcome.Should().Be(GateOutcome.NotMeasurable);
        gate.Note.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void CoordinationErrorRate_WithNoCoordinationCall_IsNotMeasurable()
    {
        Gate("coordination-tool-error-rate", One(TestRuns.Run()), One(TestRuns.Run(coordinationCalls: 0)))
            .Outcome.Should()
            .Be(GateOutcome.NotMeasurable);
    }

    [Fact]
    public void OpenObligations_WhenNoResultCarriedTheField_IsNotMeasurable()
    {
        // #673 has not landed the field yet: the zero means NOT REPORTED, and a gate that read it as
        // "none were open" would publish a pass for a signal nothing emits.
        var gate = Gate("open-obligations", One(TestRuns.Run()), One(TestRuns.Run(obligationResults: 0)));

        gate.Outcome.Should().Be(GateOutcome.NotMeasurable);
        gate.Note.Should().Contain("NOT REPORTED");
    }

    [Fact]
    public void OpenObligations_WhenReported_MustBeZero()
    {
        Gate(
            "open-obligations",
            One(TestRuns.Run(obligationResults: 4)),
            One(TestRuns.Run(obligationResults: 4, openObligations: 0))
        )
            .Outcome.Should()
            .Be(GateOutcome.Passed);

        Gate(
            "open-obligations",
            One(TestRuns.Run(obligationResults: 4)),
            One(TestRuns.Run(obligationResults: 4, openObligations: 1))
        )
            .Outcome.Should()
            .Be(GateOutcome.Failed);
    }

    [Fact]
    public void UnknownAgentWaits_WhenTheBaselineNeverWaited_IsNotMeasurable()
    {
        Gate("unknown-agent-waits", One(TestRuns.Run()), One(TestRuns.Run(waitOk: 3)))
            .Outcome.Should()
            .Be(GateOutcome.NotMeasurable);
    }

    [Fact]
    public void UnknownAgentWaits_WorseThanTheBaseline_Fails()
    {
        var baseline = One(TestRuns.Run(waitOk: 2, waitUnknownAgent: 1));

        Gate("unknown-agent-waits", baseline, One(TestRuns.Run(waitOk: 3, waitUnknownAgent: 1)))
            .Outcome.Should()
            .Be(GateOutcome.Passed);
        Gate("unknown-agent-waits", baseline, One(TestRuns.Run(waitOk: 3, waitUnknownAgent: 2)))
            .Outcome.Should()
            .Be(GateOutcome.Failed);
    }

    [Fact]
    public void InputTokens_WithNoUsageRecordsPersisted_IsNotMeasurable()
    {
        // The writer already calls this ABSENT data, not zero consumption; the gate must agree.
        Gate("input-tokens-per-successful-run", One(TestRuns.Run()), One(TestRuns.Run()))
            .Outcome.Should()
            .Be(GateOutcome.NotMeasurable);
    }

    [Fact]
    public void ToolCatalogBytes_WithNoSpawnStamped_IsNotMeasurable()
    {
        Gate("tool-catalog-bytes-per-spawn", One(TestRuns.Run()), One(TestRuns.Run()))
            .Outcome.Should()
            .Be(GateOutcome.NotMeasurable);
    }

    [Fact]
    public void CompletionRate_WithNoExpectedBoard_IsNotMeasurable()
    {
        Gate("completion-rate", One(TestRuns.Run(completion: null)), One(TestRuns.Run(completion: null)))
            .Outcome.Should()
            .Be(GateOutcome.NotMeasurable);
    }

    // --- table-level invariants -----------------------------------------------------------------

    [Fact]
    public void EveryGateIdIsUniqueAndEveryGateReportsAnOutcome()
    {
        var gates = Evaluate(One(TestRuns.Run()), One(TestRuns.Run()));

        gates.Should().NotBeEmpty();
        gates.Select(g => g.GateId).Should().OnlyHaveUniqueItems();
        gates.Should().OnlyContain(g => !string.IsNullOrWhiteSpace(g.Description));
    }

    /// <summary>
    /// A pass that only just cleared a baseline-derived threshold is still a pass, but it is weak
    /// evidence and has to reach "Contrary evidence" rather than read as an improvement.
    /// </summary>
    [Fact]
    public void WithinMargin_FlagsABaselineGateThatOnlyJustCleared()
    {
        var justCleared = Gate("average-turns", One(TestRuns.Run(turns: 100)), One(TestRuns.Run(turns: 99)));
        var clearedWell = Gate("average-turns", One(TestRuns.Run(turns: 100)), One(TestRuns.Run(turns: 50)));

        justCleared.Outcome.Should().Be(GateOutcome.Passed);
        justCleared.WithinMargin.Should().BeTrue();
        clearedWell.WithinMargin.Should().BeFalse();
    }

    [Fact]
    public void WithinMargin_IsNeverSetOnAnAbsoluteTarget()
    {
        // "Passed by a hair" is only meaningful against a moving baseline; meeting a stated target
        // is meeting it.
        Gate("retry-storms", One(TestRuns.Run(retryStorms: 3)), One(TestRuns.Run(retryStorms: 0)))
            .WithinMargin.Should()
            .BeFalse();
    }
}
