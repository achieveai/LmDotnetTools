using System.Reflection;
using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;
using AchieveAi.LmDotnetTools.LmStreaming.Sample.Triggers;

namespace LmStreaming.Sample.Tests.Triggers;

/// <summary>
/// Unit tests for <see cref="SubAgentCompletionTriggerSource"/>: fires when a specific background
/// sub-agent completes (suppressing the manager's automatic parent relay for the duration of the
/// wait) and restores the relay flag if the wait is disposed before the sub-agent completes (so a
/// cancel/timeout never strands the eventual result). Spawns a real <see cref="SubAgentManager"/>
/// with a mocked <see cref="IStreamingAgent"/>, mirroring the scaffold in
/// <c>SubAgentManagerObserveCompletionTests</c> (tests/LmMultiTurn.Tests/SubAgents).
/// </summary>
public class SubAgentCompletionTriggerSourceTests : IAsyncLifetime
{
    private readonly Mock<IMultiTurnAgent> _parentMock = new();
    private readonly Mock<IStreamingAgent> _subAgentMock = new();

    // Signals when the manager relays a sub-agent result to the parent, so a test can await the
    // relay deterministically instead of racing it.
    private readonly TaskCompletionSource _parentRelayed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SubAgentManager? _manager;

    public Task InitializeAsync()
    {
        _parentMock
            .Setup(p => p.SendAsync(
                It.IsAny<List<IMessage>>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => _parentRelayed.TrySetResult())
            .ReturnsAsync(new SendReceipt("receipt-1", null, DateTimeOffset.UtcNow));

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_manager != null)
        {
            await _manager.DisposeAsync();
        }
    }

    private static TriggerArmRequest ArmReq(string argsJson) =>
        new()
        {
            WaitId = "tc-" + Guid.NewGuid().ToString("N"),
            Kind = SubAgentCompletionTriggerSource.KindName,
            ArgsJson = argsJson,
            ArmedAt = DateTimeOffset.UtcNow,
            Deadline = DateTimeOffset.UtcNow.AddMinutes(10),
        };

    private sealed class NoopSinkImpl : ITriggerEventSink
    {
        public ValueTask FireAsync(TriggerFireEvent fire, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private static readonly NoopSinkImpl NoopSink = new();

    private sealed class SignalingSink(TaskCompletionSource<TriggerFireEvent> tcs) : ITriggerEventSink
    {
        public ValueTask FireAsync(TriggerFireEvent fire, CancellationToken cancellationToken)
        {
            tcs.TrySetResult(fire);
            return ValueTask.CompletedTask;
        }
    }

    private static SignalingSink SinkThatCompletes(TaskCompletionSource<TriggerFireEvent> tcs) => new(tcs);

    /// <summary>Signals that delivery was attempted, then always fails — simulates a sink whose
    /// FireAsync throws/is cancelled mid-delivery (e.g. the runtime's fire channel rejecting the
    /// event during shutdown).</summary>
    private sealed class ThrowingSink(TaskCompletionSource attempted) : ITriggerEventSink
    {
        public ValueTask FireAsync(TriggerFireEvent fire, CancellationToken cancellationToken)
        {
            attempted.TrySetResult();
            throw new InvalidOperationException("simulated delivery failure");
        }
    }

    [Fact]
    public async Task Fire_WhenSubAgentCompletes_AndSuppressesRelay()
    {
        // The mocked sub-agent's response is gated so it cannot complete before this test arms the
        // trigger — otherwise HandleRunCompletionAsync would read NotifyParentOnCompletion=true
        // (still unset) and relay before ArmAsync gets a chance to flip it.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (manager, agentId) = await SpawnGatedSubAgentAsync(result: "sub-done", gate);

        var src = new SubAgentCompletionTriggerSource(() => manager);
        var fired = new TaskCompletionSource<TriggerFireEvent>();

        await using var handle = await src.ArmAsync(
            ArmReq($$"""{"agentId":"{{agentId}}"}"""), SinkThatCompletes(fired), CancellationToken.None);

        // Now armed (relay flag flipped false) — let the sub-agent's run proceed to completion.
        gate.SetResult();

        var evt = await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        evt.Payload.Should().Contain("sub-done");

        // Relay was suppressed behaviorally: the trigger delivered the result, so the manager must
        // not also relay it to the parent.
        _parentMock.Verify(
            p => p.SendAsync(
                It.IsAny<List<IMessage>>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Never(),
            "the trigger delivered the result — the manager must not also relay it to the parent");
    }

    [Fact]
    public async Task Dispose_BeforeCompletion_LeavesSubAgentRunning_AndRelayResumes()
    {
        // Gated so the sub-agent is still running (blocked at the gate) when the wait is disposed.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (manager, agentId) = await SpawnGatedSubAgentAsync(result: "sub-done", gate);

        var src = new SubAgentCompletionTriggerSource(() => manager);
        var handle = await src.ArmAsync(
            ArmReq($$"""{"agentId":"{{agentId}}"}"""), NoopSink, CancellationToken.None);

        // Cancel/timeout the wait BEFORE the sub-agent completes.
        await handle.DisposeAsync();

        // The wait-cancel must NOT have killed the sub-agent: let it finish now.
        gate.SetResult();

        // Its automatic relay resumed — arm flipped NotifyParentOnCompletion=false, dispose restored
        // it to true because the sub-agent hadn't completed — so the result reaches the parent
        // exactly once. This proves BOTH that the sub-agent survived the wait-cancel and that the
        // flag-restore is meaningful (a killed sub-agent would never relay).
        await _parentRelayed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _parentMock.Verify(
            p => p.SendAsync(
                It.IsAny<List<IMessage>>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Once(),
            "wait-cancel must leave the sub-agent running; its result relays once the flag is restored");
    }

    [Fact]
    public async Task Dispose_AfterFailedDelivery_RestoresRelayFlag()
    {
        // Regression for SubAgentArmedTrigger.RunAsync: `_completed` used to be set to 1 BEFORE
        // the fire attempt and never reset if sink.FireAsync then threw/was cancelled, so
        // DisposeAsync's flag-restore branch (gated on `_completed == 0`) was skipped forever —
        // permanently stranding NotifyParentOnCompletion at the arm-time "suppressed" (false)
        // value.
        //
        // Note on what this test can and cannot observe: by the time a fire is attempted, the
        // sub-agent's own HandleRunCompletionAsync has ALREADY made its one-shot relay decision
        // for this run — TryCompleteWithResult/TryCompleteWithException runs synchronously and
        // checks NotifyParentOnCompletion in the same call frame, with no await in between, so
        // that check always completes before this trigger's own continuation (awaiting
        // ObserveCompletionAsync, whose TaskCompletionSource uses
        // RunContinuationsAsynchronously) gets scheduled to resume. A post-hoc restore therefore
        // cannot retroactively relay THIS run's result — its only observable effect is on the
        // SubAgentState's own flag, restored for any future interaction with this sub-agent.
        // There is no public accessor for it (SubAgentManager is sealed and
        // SubAgentCompletionTriggerSource depends on the concrete type, not an interface), so
        // this test reads it via reflection rather than asserting a parent relay that the
        // architecture makes provably impossible to trigger for the same completion.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (manager, agentId) = await SpawnGatedSubAgentAsync(result: "sub-done", gate);

        var src = new SubAgentCompletionTriggerSource(() => manager);
        var fireAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var handle = await src.ArmAsync(
            ArmReq($$"""{"agentId":"{{agentId}}"}"""), new ThrowingSink(fireAttempted), CancellationToken.None);

        gate.SetResult();

        // Wait for the (failing) delivery attempt to actually happen before disposing, so
        // dispose deterministically observes the post-catch state instead of racing ahead of it.
        await fireAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Must not throw/hang despite the in-flight fire having faulted.
        await handle.DisposeAsync();

        GetNotifyParentOnCompletion(manager, agentId).Should().BeTrue(
            "a failed delivery must reset _completed so dispose still restores automatic relay " +
            "instead of permanently stranding it");
    }

    /// <summary>Reads the internal NotifyParentOnCompletion flag via reflection — see the comment
    /// on <see cref="Dispose_AfterFailedDelivery_RestoresRelayFlag"/> for why no public seam
    /// exists to observe this.</summary>
    private static bool GetNotifyParentOnCompletion(SubAgentManager manager, string agentId)
    {
        var agentsField = typeof(SubAgentManager).GetField("_agents", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("SubAgentManager._agents field not found.");
        var agents = (System.Collections.IDictionary)agentsField.GetValue(manager)!;
        var state = agents[agentId]
            ?? throw new InvalidOperationException($"No sub-agent state for '{agentId}'.");
        var flagProperty = state.GetType().GetProperty("NotifyParentOnCompletion")
            ?? throw new InvalidOperationException("SubAgentState.NotifyParentOnCompletion property not found.");
        return (bool)flagProperty.GetValue(state)!;
    }

    [Fact]
    public async Task ArmAsync_Throws_ForUnknownAgentId()
    {
        var manager = BuildEmptyManager();
        var src = new SubAgentCompletionTriggerSource(() => manager);

        var act = () => src.ArmAsync(ArmReq("""{"agentId":"does-not-exist"}"""), NoopSink, CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<ArgumentException>();
    }

    /// <summary>
    /// The deterministic instance of the arm-window race (#161, PR #158 F8): a background sub-agent
    /// is spawned with <c>NotifyParentOnCompletion = true</c>, so if it reaches
    /// <c>HandleRunCompletionAsync</c> before a trigger arms, the automatic relay ALREADY delivered
    /// the result to the parent. Arming afterwards used to succeed — the completion latch is
    /// resolved, so <c>ObserveCompletionAsync</c> returns immediately and the trigger fired too,
    /// putting the same result in front of the model twice. Arming must be rejected instead.
    /// </summary>
    [Fact]
    public async Task ArmAsync_AfterTheRelayAlreadyFired_IsRejected_SoTheResultIsNotDeliveredTwice()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (manager, agentId) = await SpawnGatedSubAgentAsync(result: "sub-done", gate);

        // Let the run complete FIRST — no trigger armed, so the manager's automatic relay fires and
        // the parent already has the result.
        gate.SetResult();
        await _parentRelayed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var src = new SubAgentCompletionTriggerSource(() => manager);
        var fired = new TaskCompletionSource<TriggerFireEvent>();

        var act = () => src
            .ArmAsync(ArmReq($$"""{"agentId":"{{agentId}}"}"""), SinkThatCompletes(fired), CancellationToken.None)
            .AsTask();

        // Rejected specifically because the relay already happened — NOT because the agent id went
        // unknown. Asserting the reason keeps this from passing for the wrong reason if the manager
        // ever starts evicting completed sub-agents.
        (await act.Should().ThrowAsync<ArgumentException>())
            .WithMessage("*already*relayed*");

        // Nothing fired, and the parent saw the result exactly once.
        fired.Task.IsCompleted.Should().BeFalse("a rejected arm must not deliver a second copy");
        _parentMock.Verify(
            p => p.SendAsync(
                It.IsAny<List<IMessage>>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Once(),
            "the automatic relay delivered the result once; nothing may deliver it again");
    }

    /// <summary>
    /// The counterpart to <see cref="ArmAsync_AfterTheRelayAlreadyFired_IsRejected_SoTheResultIsNotDeliveredTwice"/>,
    /// and the case that distinguishes "this run already relayed" from "this sub-agent ever relayed".
    /// A <c>SendMessage</c> continuation opens a NEW run with a fresh completion latch
    /// (<c>SubAgentState.ResetCompletionIfFinished</c>), so nothing has been relayed for it and a wait
    /// on it is legitimate. The dispatched-relay flag is per-run for exactly that reason; a flag that
    /// only ever latched would reject every wait on every continued sub-agent for the rest of the
    /// conversation, permanently — the first background relay would make the sub-agent un-waitable.
    /// </summary>
    [Fact]
    public async Task ArmAsync_OnAContinuationRun_IsAccepted_BecauseNothingWasRelayedForThatRun()
    {
        var firstRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (manager, agentId) = await SpawnGatedSubAgentAsync(result: "first-run-done", firstRun);

        // Run 1 completes with no wait armed, so the automatic relay delivers it to the parent and
        // records the dispatch.
        firstRun.SetResult();
        await _parentRelayed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Run 2: a continuation. Gated so it is still in flight when the wait arms.
        var secondRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SetupGatedSubAgentResponse(
            [new TextMessage { Text = "second-run-done", Role = Role.Assistant }],
            secondRun);
        _ = await manager.SendMessageAsync(agentId, "follow up", runInBackground: true);

        var src = new SubAgentCompletionTriggerSource(() => manager);
        var fired = new TaskCompletionSource<TriggerFireEvent>();

        // Must NOT throw: run 2 is in flight behind a fresh latch and nothing has been relayed for it.
        await using var handle = await src.ArmAsync(
            ArmReq($$"""{"agentId":"{{agentId}}"}"""), SinkThatCompletes(fired), CancellationToken.None);

        secondRun.SetResult();

        var evt = await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        evt.Payload.Should().Contain("second-run-done");

        // And the suppression still held for run 2: the trigger delivered it, so the parent saw only
        // run 1's automatic relay.
        _parentMock.Verify(
            p => p.SendAsync(
                It.IsAny<List<IMessage>>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Once(),
            "run 1 relayed automatically; run 2 was delivered by the trigger, not relayed again");
    }

    [Fact]
    public async Task ArmAsync_Throws_WhenManagerAccessorReturnsNull()
    {
        var src = new SubAgentCompletionTriggerSource(() => null);

        var act = () => src.ArmAsync(ArmReq("""{"agentId":"whatever"}"""), NoopSink, CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<ArgumentException>();
    }

    #region Helpers

    /// <summary>
    /// Spawns a background sub-agent whose run completes with <paramref name="result"/> only after
    /// <paramref name="gate"/> is set — lets the test arm the trigger before completion races ahead.
    /// </summary>
    private async Task<(SubAgentManager Manager, string AgentId)> SpawnGatedSubAgentAsync(
        string result,
        TaskCompletionSource gate)
    {
        SetupGatedSubAgentResponse(
            [new TextMessage { Text = result, Role = Role.Assistant }],
            gate);

        var manager = CreateManager();
        _manager = manager;

        var spawnJson = await manager.SpawnAsync("test-agent", "Do some work", runInBackground: true);
        var agentId = ParseAgentId(spawnJson);

        return (manager, agentId);
    }

    private SubAgentManager BuildEmptyManager()
    {
        var manager = CreateManager();
        _manager = manager;
        return manager;
    }

    private SubAgentManager CreateManager(int maxConcurrent = 5)
    {
        var options = CreateOptions(maxConcurrent);
        return new SubAgentManager(
            parentAgent: _parentMock.Object,
            parentContracts: [],
            parentHandlers: new Dictionary<string, ToolHandler>(),
            options: options,
            source: new MutableSubAgentTemplateSource(options.Templates));
    }

    private SubAgentOptions CreateOptions(int maxConcurrent = 5)
    {
        var template = new SubAgentTemplate
        {
            SystemPrompt = "You are a test agent.",
            AgentFactory = () => _subAgentMock.Object,
        };

        return new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["test-agent"] = template,
            },
            MaxConcurrentSubAgents = maxConcurrent,
        };
    }

    private static string ParseAgentId(string spawnJson)
    {
        using var doc = JsonDocument.Parse(spawnJson);
        return doc.RootElement.GetProperty("agent_id").GetString()!;
    }

    private void SetupGatedSubAgentResponse(List<IMessage> messages, TaskCompletionSource gate)
    {
        _subAgentMock
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<IMessage> _, GenerateReplyOptions? _, CancellationToken ct) =>
                Task.FromResult(ToGatedAsyncEnumerable(messages, gate, ct)));
    }

    /// <summary>
    /// Yields <paramref name="messages"/> only after <paramref name="gate"/> completes, so a test
    /// can control exactly when the sub-agent's run finishes relative to other test actions.
    /// </summary>
    private static async IAsyncEnumerable<IMessage> ToGatedAsyncEnumerable(
        List<IMessage> messages,
        TaskCompletionSource gate,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await gate.Task.WaitAsync(ct);
        foreach (var msg in messages)
        {
            ct.ThrowIfCancellationRequested();
            yield return msg;
            await Task.Yield();
        }
    }

    #endregion
}
