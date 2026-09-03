using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmLifecycle;
using AchieveAi.LmDotnetTools.LmLifecycle.Payloads;
using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using LmMultiTurn.Tests.Compaction.Corpus;
using LmMultiTurn.Tests.Persistence;
using Xunit;

namespace LmMultiTurn.Tests.Compaction;

/// <summary>
/// Rollout controls and failure cases for #686 (AC 6-8) on the corpus runner: per-route canary, both kill
/// switches, rollback with the feature off, a concurrent append during the summary, a newer state schema,
/// an unpriced model, a failing summariser and rows without a sequence number. Every case runs a real
/// <see cref="AchieveAi.LmDotnetTools.LmMultiTurn.MultiTurnAgentLoop"/> against a scripted provider.
/// </summary>
public sealed class CompactionRolloutTests : IAsyncLifetime
{
    private const string Instruction = "Do the work.";

    private readonly ConversationStoreHarness _harness = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _harness.DisposeAsync();

    /// <summary>
    /// Seven tool turns against a 2500-token window: the eighth request (~2400 tokens) is in the hard band,
    /// so every case attempts a compaction, and it still fits the usable window, so a skipped or failed
    /// attempt is a raw request that goes out rather than a refusal.
    /// </summary>
    private static CorpusScenario Scenario(
        string id,
        CorpusPricing pricing = CorpusPricing.Full,
        int echoes = 7,
        long window = 2_500
    ) =>
        new()
        {
            Id = id,
            Item = "rollout",
            Title = "rollout control",
            Steps = [CorpusStep.Say(Instruction)],
            Root = CorpusScript.EchoThenDone(echoes, idPrefix: id),
            WindowTokens = window,
            Pricing = pricing,
        };

    private static string Runs(CorpusRunData data) =>
        string.Join("; ", data.Runs.Select(r => $"{r.Input}: {r.Error ?? "ok"}"));

    private static async Task<IReadOnlyList<PersistedMessage>> RowsAsync(CorpusRunner runner) =>
        await runner.Inner.LoadMessagesAsync(CorpusRunner.RootThread);

    private static IEnumerable<string> ResultIds(IEnumerable<IMessage> messages) =>
        messages.SelectMany<IMessage, string>(m =>
            m switch
            {
                ToolCallResultMessage { ToolCallId: { } id } => [id],
                ToolsCallResultMessage multi => multi.ToolCallResults.Select(r => r.ToolCallId).OfType<string>(),
                _ => [],
            }
        );

    private static int CheckpointRows(IReadOnlyList<PersistedMessage> rows) =>
        rows.Count(r => r.MessageType == nameof(CompactionCheckpointMessage));

    public static TheoryData<Dictionary<string, CompactionMode>, CompactionMode, bool> Routes =>
        new()
        {
            // The exact "{providerId}/{modelId}" key enables the canary route.
            {
                new() { ["corpus/corpus-model"] = CompactionMode.Compact },
                CompactionMode.Off,
                true
            },
            // The model id alone enables it on every provider.
            {
                new() { ["corpus-model"] = CompactionMode.Compact },
                CompactionMode.Off,
                true
            },
            // Another route's entry leaves this one at the global mode.
            {
                new() { ["corpus/other-model"] = CompactionMode.Compact },
                CompactionMode.Off,
                false
            },
            {
                new() { ["other-model"] = CompactionMode.Compact },
                CompactionMode.Off,
                false
            },
            // The exact route wins over the model id, and can lower a global Compact to Off.
            {
                new() { ["corpus/corpus-model"] = CompactionMode.Off, ["corpus-model"] = CompactionMode.Compact },
                CompactionMode.Compact,
                false
            },
        };

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Canary_ModeByRoute_EnablesCompactionOnTheNamedRouteOnly(
        Dictionary<string, CompactionMode> modeByRoute,
        CompactionMode globalMode,
        bool expectCompaction
    )
    {
        var options = CorpusRunner.DefaultOptions(globalMode) with { ModeByRoute = modeByRoute };
        await using var runner = new CorpusRunner(Scenario("canary"), globalMode, _harness, options);

        var (result, data) = await runner.RunAsync();

        result.Invariants.AllZero.Should().BeTrue();
        result.TaskSuccess.Should().BeTrue(Runs(data));
        if (expectCompaction)
        {
            result.CheckpointsActivated.Should().BeGreaterThan(0, "the route is enabled");
            data.Root.Requests.Should().Contain(r => CorpusEvaluator.HasEnvelope(r));
        }
        else
        {
            result.CheckpointsActivated.Should().Be(0, "the route stays at Off");
            data.RootState.Should().BeNull("Off writes no compaction state");
            data.Decided.Should().BeEmpty("Off runs no policy");
            data.Root.Requests.Should().OnlyContain(r => !CorpusEvaluator.HasEnvelope(r));
        }
    }

    public static TheoryData<string, bool, string?> KillSwitches =>
        new()
        {
            { "config KillSwitch", true, null },
            { "env LMMULTITURN_COMPACTION_DISABLED=1", false, "1" },
            { "env LMMULTITURN_COMPACTION_DISABLED=true", false, "true" },
        };

    [Theory]
    [MemberData(nameof(KillSwitches))]
    public async Task KillSwitch_SkipsEveryDecisionDisabled_AndWritesNoCheckpoint(
        string label,
        bool configFlag,
        string? env
    )
    {
        var options = CorpusRunner.DefaultOptions(CompactionMode.Compact) with { KillSwitch = configFlag };
        await using var runner = new CorpusRunner(Scenario("kill"), CompactionMode.Compact, _harness, options)
        {
            KillSwitchEnv = env,
        };

        var (result, data) = await runner.RunAsync();

        result.Invariants.AllZero.Should().BeTrue(label);
        result.TaskSuccess.Should().BeTrue("{0}: {1}", label, Runs(data));
        data.Decided.Should().NotBeEmpty("{0}: the policy still runs and records the kill", label);
        data.Decided.Should()
            .OnlyContain(
                d => d.Decision == CompactionDecisionKinds.Skipped && d.Reason == CompactionSkipReasons.Disabled,
                label
            );
        result.CheckpointsActivated.Should().Be(0, label);
        CheckpointRows(data.RootRows).Should().Be(0, "{0}: no checkpoint row is ever appended", label);
        data.RootState?.History.Should().BeEmpty(label);
        data.Root.Requests.Should().OnlyContain(r => !CorpusEvaluator.HasEnvelope(r), label);
    }

    [Fact]
    public async Task Rollback_FeatureOff_SendsTheRawHistory_AndKeepsTheCheckpointAndAuditRows()
    {
        // Nine turns against a 3200-token window: the tenth request (~3100 tokens) is in the hard band and
        // compacts; with the feature off the same raw history (~3150 tokens) still fits, which is what
        // makes the rollback observable rather than an overflow.
        await using var runner = new CorpusRunner(
            Scenario("rollback", echoes: 9, window: 3_200),
            CompactionMode.Compact,
            _harness
        );

        // Phase 1: Compact activates a checkpoint.
        (await runner.SayAsync(Instruction))
            .IsError.Should()
            .BeFalse();
        var stateBefore = await CompactionStateProjection.LoadAsync(runner.Inner, CorpusRunner.RootThread);
        stateBefore!.ActiveCheckpointId.Should().NotBeNull();
        var rowsBefore = await RowsAsync(runner);
        CheckpointRows(rowsBefore).Should().Be(1);
        var requestsBefore = runner.Root.Requests.Count;
        runner.Root.Requests[^1].Should().Match(r => CorpusEvaluator.HasEnvelope((IReadOnlyList<IMessage>)r));

        // Phase 2: the feature is switched off (no CompactionSetup at all) and the loop comes back up.
        await runner.RestartWithAsync(_ => null);
        var continued = await runner.SayAsync("Continue.");
        continued
            .IsError.Should()
            .BeFalse(
                "{0} (last request {1} tokens, window {2})",
                continued.Error,
                runner.Root.Calls[^1].RequestTokens,
                3_200
            );

        var afterOff = runner.Root.Requests.Skip(requestsBefore).ToList();
        afterOff.Should().NotBeEmpty();
        afterOff
            .Should()
            .OnlyContain(r => !CorpusEvaluator.HasEnvelope(r), "request construction reads the raw history again");
        foreach (var request in afterOff)
        {
            var expanded = ScriptedProvider.Expand(request);
            expanded
                .Should()
                .NotContain(
                    m => m is CompactionCheckpointMessage,
                    "a checkpoint row is an audit row, never a provider message (the OpenAI converter rejects unknown message types)"
                );
            CorpusEvaluator.InvalidPairs(expanded).Should().Be(0);
            // Every raw row the checkpoint had covered is back in the request.
            ResultIds(expanded).Should().Contain(["rollback-1", "rollback-9"]);
        }

        var rowsAfter = await RowsAsync(runner);
        rowsAfter
            .Take(rowsBefore.Count)
            .Select(r => r.Id)
            .Should()
            .Equal(rowsBefore.Select(r => r.Id), "no row was rewritten or dropped");
        CheckpointRows(rowsAfter).Should().Be(1, "the checkpoint row is retained");
        var stateAfter = await CompactionStateProjection.LoadAsync(runner.Inner, CorpusRunner.RootThread);
        stateAfter
            .Should()
            .BeEquivalentTo(stateBefore, "the audit state is retained untouched while the feature is off");
        (await ContextObservationProjection.LoadLatestAsync(runner.Inner, CorpusRunner.RootThread))
            .Should()
            .NotBeNull();

        // Phase 3: the feature comes back and the loop adopts the retained checkpoint instead of rebuilding it.
        var requestsBeforeOn = runner.Root.Requests.Count;
        await runner.RestartWithAsync(setup => setup);
        (await runner.SayAsync("Again.")).IsError.Should().BeFalse();
        runner
            .Root.Requests.Skip(requestsBeforeOn)
            .Should()
            .NotBeEmpty()
            .And.OnlyContain(r => CorpusEvaluator.HasEnvelope(r));
        var stateOn = await CompactionStateProjection.LoadAsync(runner.Inner, CorpusRunner.RootThread);
        stateOn!.ActiveCheckpointId.Should().Be(stateBefore.ActiveCheckpointId, "adopted, not rebuilt");

        var data = await runner.CollectAsync(0);
        CorpusEvaluator.Evaluate(data).Invariants.AllZero.Should().BeTrue();
    }

    [Fact]
    public async Task ConcurrentAppend_DuringTheSummary_RejectsThatCheckpoint_AndTheLoopContinuesRaw()
    {
        await using var runner = new CorpusRunner(Scenario("race"), CompactionMode.Compact, _harness);
        var injected = 0;
        runner.Summarizer.WhileSummarizing = async _ =>
        {
            if (Interlocked.Exchange(ref injected, 1) == 1)
            {
                return;
            }

            // Another writer (a webhook, a sibling process) appends a row while the summary is in flight.
            var foreign = MessagePersistenceConverter.ToPersistedMessage(
                new TextMessage
                {
                    Text = "Out-of-band note appended while the summary ran.",
                    Role = Role.User,
                    RunId = "external",
                    ThreadId = CorpusRunner.RootThread,
                },
                CorpusRunner.RootThread,
                "external"
            );
            await runner.Inner.AppendMessagesAsync(CorpusRunner.RootThread, [foreign]);
        };

        (await runner.SayAsync(Instruction)).IsError.Should().BeFalse("the raw request still fits the window");
        injected.Should().Be(1, "the summary ran once");

        var state = await CompactionStateProjection.LoadAsync(runner.Inner, CorpusRunner.RootThread);
        state.Should().NotBeNull();
        state!
            .History.Should()
            .Contain(
                e =>
                    e.Status == CheckpointStatus.Rejected
                    && (e.Reason == CheckpointReasons.StaleWatermark || e.Reason == CompactionReasons.WatermarkDrift),
                "V2: the boundary moved under the summary"
            );
        state.ActiveCheckpointId.Should().BeNull("nothing built on a stale watermark is ever activated");
        runner.Root.Requests.Should().OnlyContain(r => !CorpusEvaluator.HasEnvelope(r));

        // The next request is over the hard band again; with no writer racing it the checkpoint lands.
        (await runner.SayAsync("More."))
            .IsError.Should()
            .BeFalse();
        var data = await runner.CollectAsync(0);
        var result = CorpusEvaluator.Evaluate(data);
        result.Invariants.AllZero.Should().BeTrue();
        result.CheckpointsActivated.Should().Be(1);
        data.RootRows.Select(r => r.Seq).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task NewerStateSchema_IsNeverTouched_AndNoCheckpointIsWritten()
    {
        await using var runner = new CorpusRunner(Scenario("schema"), CompactionMode.Compact, _harness);
        const string future =
            """{"schema_version":2,"active_checkpoint_id":"cp-future","history":[],"a_new_field":true}""";
        await runner.Inner.UpdateMetadataAsync(
            CorpusRunner.RootThread,
            existing =>
                (existing ?? new ThreadMetadata { ThreadId = CorpusRunner.RootThread, LastUpdated = 0 }) with
                {
                    Properties = (existing?.Properties ?? ImmutableDictionary<string, object>.Empty).SetItem(
                        CompactionStateProjection.PropertyKey,
                        future
                    ),
                }
        );

        var (result, data) = await runner.RunAsync();

        result.Invariants.AllZero.Should().BeTrue();
        result.TaskSuccess.Should().BeTrue(Runs(data));
        result.CheckpointsActivated.Should().Be(0);
        CheckpointRows(data.RootRows).Should().Be(0, "a checkpoint this build cannot record must not be appended");
        data.Root.Requests.Should().OnlyContain(r => !CorpusEvaluator.HasEnvelope(r));
        var raw = (await runner.Inner.LoadMetadataAsync(CorpusRunner.RootThread))!.Properties![
            CompactionStateProjection.PropertyKey
        ];
        (raw is string s ? s : raw.ToString()).Should().Be(future, "the newer schema is preserved byte for byte");
    }

    [Fact]
    public async Task UnpricedModel_StillCompactsAtTheHardBand_AndReportsCostUnavailable()
    {
        await using var runner = new CorpusRunner(
            Scenario("unpriced", CorpusPricing.None),
            CompactionMode.Compact,
            _harness
        );

        var (result, data) = await runner.RunAsync();

        result.Invariants.AllZero.Should().BeTrue();
        result.TaskSuccess.Should().BeTrue(Runs(data));
        result.CheckpointsActivated.Should().BeGreaterThan(0, "the hard band ignores economics");
        result.CostCompleteness.Should().Be(nameof(CostCompleteness.Unavailable));
        result.CostMicros.Should().BeNull();
    }

    [Fact]
    public async Task FailedSummary_IsRecordedFailed_AndTheRawRequestStillGoesOut()
    {
        await using var runner = new CorpusRunner(Scenario("failed"), CompactionMode.Compact, _harness);
        runner.Summarizer.Fail = _ => new InvalidOperationException("summary model down");

        var (result, data) = await runner.RunAsync();

        result.Invariants.AllZero.Should().BeTrue();
        result
            .TaskSuccess.Should()
            .BeTrue("the raw request fits the window, so the run completes without the checkpoint: {0}", Runs(data));
        var failed = runner.Publisher.Payloads<CompactionPayload>(LifecycleEventTypes.CompactionFailed);
        failed.Should().NotBeEmpty();
        failed
            .Should()
            .OnlyContain(p =>
                p.Decision == CompactionDecisionKinds.Failed && p.Reason == CompactionReasons.SummaryCallFailed
            );
        result.CheckpointsActivated.Should().Be(0);
        CheckpointRows(data.RootRows).Should().Be(0);
        data.RootState?.ActiveCheckpointId.Should().BeNull();
        data.Root.Requests.Should().OnlyContain(r => !CorpusEvaluator.HasEnvelope(r));
    }

    [Fact]
    public async Task RowsWithoutSeq_SkipUnsafeState_AndNeverCut()
    {
        // §8.3: a store that hands back rows without a sequence number (an older binary's rows before the
        // backfill, or a reader that lost it) cannot be cut safely; the loop keeps sending the raw history.
        await using var runner = new CorpusRunner(
            Scenario("noseq"),
            CompactionMode.Compact,
            _harness,
            stripSeqOnLoad: true
        );

        var (result, data) = await runner.RunAsync();

        result.Invariants.InvalidToolPairs.Should().Be(0);
        result.Invariants.CrossThreadReads.Should().Be(0);
        result.TaskSuccess.Should().BeTrue(Runs(data));
        result.Reasons.Keys.Should().Contain(CompactionSkipReasons.UnsafeState);
        result.CheckpointsActivated.Should().Be(0);
        CheckpointRows(data.RootRows).Should().Be(0);
        data.Root.Requests.Should().OnlyContain(r => !CorpusEvaluator.HasEnvelope(r));
    }
}
