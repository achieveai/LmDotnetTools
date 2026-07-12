using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests.SubAgents;

/// <summary>
/// Regression tests for the concurrency-gate release invariants around
/// <see cref="SubAgentManager.SpawnAsync"/>, <c>RestartRunAsync</c>, and
/// <see cref="SubAgentManager"/>'s per-sub-agent monitor loop:
/// <list type="bullet">
/// <item><description>F1/F2: a spawn/restart whose own <c>SendAsync</c> fails AFTER the monitor
/// has already started must release the concurrency slot exactly once - not zero times (a
/// stuck slot) and not twice (a corrupted semaphore count) - even when an earlier epoch's
/// monitor is still "in flight" (blocked in its subscription, as a real agent's stays after a
/// single run completes) at the moment of the failure. See <see cref="GateReleaseGuard"/>.</description></item>
/// <item><description>F3: a completed sub-agent's concurrency slot must be released BEFORE its
/// (possibly slow/backpressured) parent relay, so a blocked relay never holds up a fresh
/// spawn.</description></item>
/// <item><description>F5: if the monitor's subscription fails outright with a non-cancellation
/// exception, the sub-agent's completion latch must be faulted (not left to hang
/// forever).</description></item>
/// </list>
/// These scenarios are not organically reachable through the real <c>MultiTurnAgentLoop</c>
/// pipeline (every turn-execution exception it raises is already converted to a normal
/// <c>RunCompletedMessage(IsError: true)</c>), so they use
/// <see cref="SubAgentManager.TestAgentFactoryOverride"/> to substitute a
/// <see cref="FakeMultiTurnAgent"/> while still exercising the real Spawn/Restart/Monitor
/// plumbing (real gate acquisition, real monitor task).
/// </summary>
public class SubAgentManagerGateReleaseRegressionTests : IAsyncLifetime
{
    private readonly Mock<IMultiTurnAgent> _parentMock = new();
    private SubAgentManager? _manager;

    public Task InitializeAsync()
    {
        // Default parent mock: accept any SendAsync call immediately (individual tests
        // override this when they need to control relay timing, e.g. the F3 test).
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

    [Fact]
    public async Task SpawnAsync_SendAsyncThrowsAfterMonitorStarts_ReleasesGateExactlyOnceAndRollsBackRegistration()
    {
        // F1: SpawnAsync starts the monitor (which subscribes) BEFORE awaiting SendAsync, so a
        // SendAsync failure happens with the monitor already owning (and about to release) the
        // gate. The fix must release exactly once - verified indirectly, since a genuine
        // double-release would either throw SemaphoreFullException or silently let more than
        // MaxConcurrentSubAgents run concurrently - by filling capacity afterward.
        const int maxConcurrent = 2;
        var templates = new Dictionary<string, SubAgentTemplate>
        {
            ["throws-on-send"] = DummyTemplate("throws-on-send"),
            ["normal"] = DummyTemplate("normal"),
        };

        _manager = CreateManagerWithTemplates(maxConcurrent, templates);
        _manager.TestAgentFactoryOverride = (_, template) => new FakeMultiTurnAgent
        {
            SendImpl = template.Name == "throws-on-send"
                ? _ => ValueTask.FromException<SendReceipt>(new InvalidOperationException("send failed"))
                : null,
        };

        var act = () => _manager.SpawnAsync(
            "throws-on-send", "task", name: "failed-spawn", runInBackground: true);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("send failed");

        // The failed spawn's partial registration must be rolled back: its name no longer
        // resolves to anything.
        var sendToRolledBack = () => _manager.SendMessageAsync("failed-spawn", "x");
        await sendToRolledBack.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Unknown sub-agent*failed-spawn*");

        // Fill capacity with agents that never complete (holding their slots forever) to prove
        // exactly maxConcurrent slots are free - not fewer (stuck slot) and not more
        // (corrupted/over-released count would let this loop run past capacity too).
        for (var i = 0; i < maxConcurrent; i++)
        {
            var json = await _manager.SpawnAsync("normal", $"filler-{i}", runInBackground: true);
            using var doc = JsonDocument.Parse(json);
            doc.RootElement.GetProperty("status").GetString().Should().Be("spawned");
        }

        var overCapacity = () => _manager.SpawnAsync("normal", "one-too-many", runInBackground: true);
        await overCapacity.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Max concurrent sub-agents*");
    }

    [Fact]
    public async Task RestartRunAsync_SendAsyncThrowsAfterMonitorStarts_ReleasesGateExactlyOnceEvenWhilePriorEpochMonitorStillInFlight()
    {
        // F2: reproduces the exact race the GateReleaseGuard redesign fixes. Epoch 1 completes
        // (releasing its slot) but - like a real agent - its monitor's subscription stays open
        // and blocked afterward, not torn down. SendMessageAsync then restarts the agent
        // (epoch 2); RestartRunAsync's own SendAsync fails after epoch 2's monitor has already
        // started, while epoch 1's monitor is still in flight, blocked, waiting to be
        // cancelled+awaited. A shared/reset-in-place guard would let epoch 1's late release
        // (fired only once RestartRunAsync cancels+awaits it) spuriously consume epoch 2's
        // slot; independent per-epoch guards cannot be confused this way.
        const int maxConcurrent = 2;
        var templates = new Dictionary<string, SubAgentTemplate>
        {
            ["restartable"] = DummyTemplate("restartable"),
            ["normal"] = DummyTemplate("normal"),
        };

        _manager = CreateManagerWithTemplates(maxConcurrent, templates);

        var fake = new FakeMultiTurnAgent
        {
            SendImpl = callIndex => callIndex == 1
                ? new ValueTask<SendReceipt>(new SendReceipt("r1", null, DateTimeOffset.UtcNow))
                : ValueTask.FromException<SendReceipt>(new InvalidOperationException("restart send failed")),
            SubscribeImpl = (callIndex, ct) => callIndex == 1
                // Epoch 1: completes immediately, then - like a real agent - keeps its
                // subscription open/blocked until cancelled.
                ? FakeMultiTurnAgent.CompleteOnceThenWaitForeverStream("run-1", ct)
                // Epoch 2: never gets a chance to complete (SendAsync throws first); just
                // waits to be cancelled during the restart-failure cleanup.
                : FakeMultiTurnAgent.WaitForeverStream(ct),
        };

        _manager.TestAgentFactoryOverride = (_, template) => template.Name == "restartable"
            ? fake
            : new FakeMultiTurnAgent();

        var spawnJson = await _manager.SpawnAsync(
            "restartable", "initial task", runInBackground: true);
        using var spawnDoc = JsonDocument.Parse(spawnJson);
        var agentId = spawnDoc.RootElement.GetProperty("agent_id").GetString()!;

        // Wait for epoch 1 to settle "completed" - its gateGuard has already released its slot
        // by this point (F3 order: release happens before HandleRunCompletionAsync), while its
        // monitor is still blocked in CompleteOnceThenWaitForeverStream's tail wait.
        await WaitForConditionAsync(
            () =>
            {
                try { return _manager!.Peek(agentId).Contains("\"completed\""); }
                catch { return false; }
            },
            TimeSpan.FromSeconds(10));

        // Act: SendMessageAsync on the completed agent goes through RestartRunAsync, whose own
        // SendAsync (the fake's 2nd call) throws after epoch 2's monitor has already started.
        var act = () => _manager.SendMessageAsync(agentId, "continue", runInBackground: true);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("restart send failed");

        // Assert: the gate is back to exactly full capacity - one slot from epoch 1's
        // completion, one from epoch 2's restart-failure cleanup - not corrupted upward by a
        // spurious extra release from epoch 1's monitor exiting (during RestartRunAsync's
        // await) after epoch 2 already started.
        for (var i = 0; i < maxConcurrent; i++)
        {
            var json = await _manager.SpawnAsync("normal", $"filler-{i}", runInBackground: true);
            using var doc = JsonDocument.Parse(json);
            doc.RootElement.GetProperty("status").GetString().Should().Be("spawned");
        }

        var overCapacity = () => _manager.SpawnAsync("normal", "one-too-many", runInBackground: true);
        await overCapacity.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Max concurrent sub-agents*");
    }

    [Fact]
    public async Task MonitorSubAgentAsync_ReleasesGateBeforeParentRelay_SoBackpressuredRelayDoesNotBlockNewSpawns()
    {
        // F3: a completed sub-agent's parent relay (SendToParentAsync) is deliberately blocked;
        // a fresh spawn must still be able to acquire the (only) concurrency slot, proving the
        // slot is released BEFORE - not after - the relay.
        const int maxConcurrent = 1;
        var relayEntered = new TaskCompletionSource<bool>();
        var relayRelease = new TaskCompletionSource<bool>();

        _parentMock
            .Setup(p => p.SendAsync(
                It.IsAny<List<IMessage>>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns<List<IMessage>, string?, string?, CancellationToken>(async (_, _, _, ct) =>
            {
                _ = relayEntered.TrySetResult(true);
                await relayRelease.Task.WaitAsync(ct);
                return new SendReceipt("relayed", null, DateTimeOffset.UtcNow);
            });

        var templates = new Dictionary<string, SubAgentTemplate>
        {
            ["completing"] = DummyTemplate("completing"),
            ["normal"] = DummyTemplate("normal"),
        };

        _manager = CreateManagerWithTemplates(maxConcurrent, templates);
        _manager.TestAgentFactoryOverride = (_, template) => template.Name == "completing"
            ? new FakeMultiTurnAgent
            {
                SubscribeImpl = (_, ct) => FakeMultiTurnAgent.CompleteOnceThenWaitForeverStream("run-1", ct),
            }
            : new FakeMultiTurnAgent();

        await _manager.SpawnAsync("completing", "task", runInBackground: true);

        // Wait until the completed sub-agent's parent relay is in flight (blocked).
        (await relayEntered.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();

        // Act: a fresh spawn must succeed even while the first agent's relay is still blocked.
        var secondSpawnJson = await _manager
            .SpawnAsync("normal", "second task", runInBackground: true)
            .WaitAsync(TimeSpan.FromSeconds(10));

        using var doc = JsonDocument.Parse(secondSpawnJson);
        doc.RootElement.GetProperty("status").GetString().Should().Be("spawned");

        relayRelease.SetResult(true);
    }

    [Fact]
    public async Task MonitorSubAgentAsync_SubscribeThrowsNonCancellationException_FaultsCompletionLatch()
    {
        // F5: if SubscribeAsync fails outright with a non-cancellation exception, the monitor's
        // generic terminal catch must fault state.Completion, or ObserveCompletionAsync hangs
        // forever. Guarded by a timeout so a regression here hangs this one test, not the suite.
        var templates = new Dictionary<string, SubAgentTemplate>
        {
            ["broken"] = DummyTemplate("broken"),
        };

        _manager = CreateManagerWithTemplates(maxConcurrent: 5, templates);
        var thrown = new InvalidOperationException("subscribe blew up");
        _manager.TestAgentFactoryOverride = (_, _) => new FakeMultiTurnAgent
        {
            SubscribeImpl = (_, _) => FakeMultiTurnAgent.ThrowingStream(thrown),
        };

        var spawnJson = await _manager.SpawnAsync("broken", "task", runInBackground: true);
        using var spawnDoc = JsonDocument.Parse(spawnJson);
        var agentId = spawnDoc.RootElement.GetProperty("agent_id").GetString()!;

        var act = () => _manager!.ObserveCompletionAsync(agentId, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("subscribe blew up");
    }

    [Fact]
    public async Task HandleRunCompletion_PendingMessageCompletion_DefersOwnedProviderDisposalUntilTerminalCompletion()
    {
        // The HasPendingMessages branch (SubAgentManager.HandleRunCompletionAsync) must NOT treat a
        // pending (non-terminal) completion as terminal: it must leave the completion latch unresolved
        // and the owned provider undisposed, and only the following terminal completion disposes the
        // provider — exactly once. Without direct coverage this branch could regress while the rest of
        // the suite stays green.
        var templates = new Dictionary<string, SubAgentTemplate>
        {
            ["owned"] = DummyTemplate("owned"),
        };

        _manager = CreateManagerWithTemplates(maxConcurrent: 2, templates);

        var disposeCount = 0;
        var provider = new Mock<IStreamingAgent>();
        provider.As<IAsyncDisposable>()
            .Setup(d => d.DisposeAsync())
            .Returns(() =>
            {
                _ = Interlocked.Increment(ref disposeCount);
                return ValueTask.CompletedTask;
            });

        var pendingEmitted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTerminal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _manager.TestAgentFactoryOverride = (_, _) => new FakeMultiTurnAgent
        {
            SubscribeImpl = (_, ct) => FakeMultiTurnAgent.PendingThenTerminalStream(
                "run-1", pendingEmitted, releaseTerminal.Task, ct),
        };
        _manager.TestOwnedProviderOverride = (_, _) => provider.Object;

        var spawnJson = await _manager.SpawnAsync("owned", "task", runInBackground: true);
        using var spawnDoc = JsonDocument.Parse(spawnJson);
        var agentId = spawnDoc.RootElement.GetProperty("agent_id").GetString()!;

        // After the pending (non-terminal) completion: the owned provider must remain undisposed and
        // the sub-agent must still read as running.
        (await pendingEmitted.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
        await Task.Delay(150); // give HandleRunCompletionAsync time to (wrongly) dispose if it regressed
        Volatile.Read(ref disposeCount).Should()
            .Be(0, "a HasPendingMessages completion must not dispose the owned provider");
        _manager.Peek(agentId).Should().Contain("\"running\"");

        // The terminal completion disposes the owned provider exactly once.
        releaseTerminal.SetResult(true);
        await WaitForConditionAsync(() => Volatile.Read(ref disposeCount) == 1, TimeSpan.FromSeconds(10));
        Volatile.Read(ref disposeCount).Should().Be(1, "the terminal completion disposes the owned provider once");
    }

    #region Helpers

    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
    }

    /// <summary>
    /// A minimal template whose <see cref="SubAgentTemplate.AgentFactory"/> is never actually
    /// invoked (bypassed by <see cref="SubAgentManager.TestAgentFactoryOverride"/>); its
    /// <see cref="SubAgentTemplate.Name"/> lets a shared override delegate distinguish which
    /// template a given spawn used.
    /// </summary>
    private static SubAgentTemplate DummyTemplate(string name)
    {
        return new SubAgentTemplate
        {
            Name = name,
            SystemPrompt = "You are a test agent.",
            AgentFactory = () => throw new NotSupportedException(
                "Bypassed by TestAgentFactoryOverride; should never be invoked."),
        };
    }

    private SubAgentManager CreateManagerWithTemplates(
        int maxConcurrent,
        IReadOnlyDictionary<string, SubAgentTemplate> templates)
    {
        var options = new SubAgentOptions
        {
            Templates = templates,
            MaxConcurrentSubAgents = maxConcurrent,
        };

        return new SubAgentManager(
            parentAgent: _parentMock.Object,
            parentContracts: [],
            parentHandlers: new Dictionary<string, ToolHandler>(),
            options: options,
            source: new MutableSubAgentTemplateSource(options.Templates));
    }

    #endregion
}

/// <summary>
/// Minimal <see cref="IMultiTurnAgent"/> test double for exercising
/// <see cref="SubAgentManager"/>'s real Spawn/Restart/Monitor plumbing (real gate acquisition,
/// real monitor task) with fully controllable Send/Subscribe behavior per call - needed for
/// scenarios a real <c>MultiTurnAgentLoop</c> cannot organically reproduce (a background
/// <c>SendAsync</c> failing after the monitor already started; a monitor's
/// <c>SubscribeAsync</c> failing outright with a non-cancellation exception).
/// </summary>
internal sealed class FakeMultiTurnAgent : IMultiTurnAgent
{
    private int _sendCallCount;
    private int _subscribeCallCount;

    public string? CurrentRunId => null;

    public string ThreadId { get; init; } = "fake-thread";

    public bool IsRunning { get; private set; }

    /// <summary>
    /// Invoked for each <see cref="SendAsync"/> call with a 1-based call index, so a test can
    /// make e.g. only a restart's send (call #2) fail. Null (default) =&gt; every call
    /// succeeds with a fresh receipt.
    /// </summary>
    public Func<int, ValueTask<SendReceipt>>? SendImpl { get; set; }

    /// <summary>
    /// Invoked for each <see cref="SubscribeAsync"/> call with a 1-based call index, so a test
    /// can give a later restart's monitor different behavior than the original epoch's. Null
    /// (default) =&gt; <see cref="WaitForeverStream"/>.
    /// </summary>
    public Func<int, CancellationToken, IAsyncEnumerable<IMessage>>? SubscribeImpl { get; set; }

    public ValueTask<SendReceipt> SendAsync(
        List<IMessage> messages,
        string? inputId = null,
        string? parentRunId = null,
        CancellationToken ct = default)
    {
        var callIndex = Interlocked.Increment(ref _sendCallCount);
        return SendImpl != null
            ? SendImpl(callIndex)
            : new ValueTask<SendReceipt>(
                new SendReceipt(Guid.NewGuid().ToString("N"), inputId, DateTimeOffset.UtcNow));
    }

    public ValueTask<SendReceipt?> TrySendAsync(
        List<IMessage> messages,
        string? inputId = null,
        string? parentRunId = null,
        CancellationToken ct = default)
    {
        throw new NotSupportedException("Not used by SubAgentManager or these tests.");
    }

    public IAsyncEnumerable<IMessage> ExecuteRunAsync(
        UserInput userInput,
        CancellationToken ct = default)
    {
        throw new NotSupportedException("Not used by SubAgentManager or these tests.");
    }

    public IAsyncEnumerable<IMessage> SubscribeAsync(CancellationToken ct = default)
    {
        var callIndex = Interlocked.Increment(ref _subscribeCallCount);
        return SubscribeImpl != null
            ? SubscribeImpl(callIndex, ct)
            : WaitForeverStream(ct);
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        IsRunning = true;
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
        finally
        {
            IsRunning = false;
        }
    }

    public Task StopAsync(TimeSpan? timeout = null)
    {
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Stream that never produces a message and only ends when cancelled - mirrors a real
    /// sub-agent's subscription staying open/blocked after its background loop has nothing left
    /// to do (the loop and subscriber channels stay alive across individual run completions;
    /// only explicit cancellation/disposal closes them).
    /// </summary>
    internal static async IAsyncEnumerable<IMessage> WaitForeverStream(
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        yield break;
    }

    /// <summary>
    /// Stream that yields one <see cref="RunCompletedMessage"/> immediately, then keeps the
    /// subscription open/blocked exactly like a real agent's would after a single run finishes
    /// - needed to faithfully reproduce the "old monitor still in flight when a restart
    /// happens" timing the <see cref="GateReleaseGuard"/> fix targets.
    /// </summary>
    internal static async IAsyncEnumerable<IMessage> CompleteOnceThenWaitForeverStream(
        string completedRunId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new RunCompletedMessage { CompletedRunId = completedRunId };
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }

    /// <summary>
    /// Stream that yields a NON-terminal <see cref="RunCompletedMessage"/> (HasPendingMessages =
    /// true), signals <paramref name="pendingEmitted"/>, waits for <paramref name="releaseTerminal"/>,
    /// then yields the terminal completion and keeps the subscription open — lets a test verify that a
    /// pending completion neither resolves the latch nor disposes the owned provider, and that the
    /// following terminal completion disposes exactly once.
    /// </summary>
    internal static async IAsyncEnumerable<IMessage> PendingThenTerminalStream(
        string completedRunId,
        TaskCompletionSource<bool> pendingEmitted,
        Task releaseTerminal,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new RunCompletedMessage { CompletedRunId = completedRunId, HasPendingMessages = true };
        _ = pendingEmitted.TrySetResult(true);
        await releaseTerminal.WaitAsync(ct);
        yield return new RunCompletedMessage { CompletedRunId = completedRunId };
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }

    /// <summary>
    /// Stream that throws <paramref name="exception"/> the moment it's subscribed to, before
    /// producing any message - simulates a monitor's <c>SubscribeAsync</c> failing outright,
    /// the scenario the monitor's generic terminal catch must fault the completion latch for
    /// (F5).
    /// </summary>
    internal static IAsyncEnumerable<IMessage> ThrowingStream(Exception exception)
    {
        throw exception;
    }
}
