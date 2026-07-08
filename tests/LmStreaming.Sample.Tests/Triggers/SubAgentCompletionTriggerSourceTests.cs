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
