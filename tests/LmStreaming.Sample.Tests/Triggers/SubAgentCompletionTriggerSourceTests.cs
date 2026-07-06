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
    private SubAgentManager? _manager;

    public Task InitializeAsync()
    {
        _parentMock
            .Setup(p => p.SendAsync(
                It.IsAny<List<IMessage>>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
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

        // Relay was suppressed: the flag was flipped false at arm and never flipped back.
        manager.PeekNotifyParentOnCompletion(agentId).Should().BeFalse();
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
    public async Task Dispose_BeforeCompletion_RestoresRelayFlag()
    {
        var (manager, agentId) = await SpawnNeverCompletingSubAgentAsync();
        var src = new SubAgentCompletionTriggerSource(() => manager);
        var handle = await src.ArmAsync(
            ArmReq($$"""{"agentId":"{{agentId}}"}"""), NoopSink, CancellationToken.None);

        manager.PeekNotifyParentOnCompletion(agentId).Should().BeFalse("arming suppresses the relay");

        await handle.DisposeAsync(); // simulates CancelWait / timeout before completion

        manager.PeekNotifyParentOnCompletion(agentId).Should().BeTrue(); // restored — result not stranded
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

    /// <summary>
    /// Spawns a background sub-agent whose run never completes on its own (only cancellation, e.g.
    /// via the trigger's dispose cascading into the sub-agent's own token, unblocks it).
    /// </summary>
    private async Task<(SubAgentManager Manager, string AgentId)> SpawnNeverCompletingSubAgentAsync()
    {
        SetupNeverCompletingSubAgentResponse();

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

    private void SetupNeverCompletingSubAgentResponse()
    {
        _subAgentMock
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<IMessage> _, GenerateReplyOptions? _, CancellationToken ct) =>
                Task.FromResult(NeverCompletingAsync(ct)));
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

    /// <summary>
    /// Never yields on its own; only unblocks (via <see cref="OperationCanceledException"/>) when
    /// <paramref name="ct"/> fires, mirroring <c>NoopProcessExitObserver</c>'s pattern so disposal
    /// during teardown doesn't hang.
    /// </summary>
    private static async IAsyncEnumerable<IMessage> NeverCompletingAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = ct.Register(() => tcs.TrySetCanceled(ct));
        await tcs.Task;
        yield break; // unreachable — tcs.Task only completes (canceled) via the registration above.
    }

    #endregion
}
