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
using Xunit.Abstractions;

namespace LmMultiTurn.Tests.SubAgents;

/// <summary>
/// Unit coverage for the presentation-only observation seam added for WI #194 (Task 9):
/// <see cref="SubAgentManager.SubscribeToAgentAcrossRestartsAsync"/> plus the
/// <c>SubAgentState.SignalAgentReplaced</c>/<c>AgentReplacedTask</c> pair it relies on. These prove a
/// focused observer keeps receiving frames when a FINISHED owned-provider child is relayed a follow-up
/// (which disposes the old loop and swaps in a fresh one), and that manager teardown ends the observer
/// cleanly. No change to spawn/send/monitor/restart execution is exercised — only the read seam.
/// </summary>
public class SubAgentManagerSubscribeAcrossRestartsTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly Mock<IMultiTurnAgent> _parentMock = new();
    private SubAgentManager? _manager;

    public SubAgentManagerSubscribeAcrossRestartsTests(ITestOutputHelper output)
    {
        _output = output;
    }

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

    [Fact]
    public async Task SubscribeAcrossRestarts_SpansOwnedProviderRestart_YieldsSecondRunMessages()
    {
        // A finished owned-provider child, when relayed a follow-up, is restarted with a FRESH loop
        // instance (the old one is disposed). An observer bound to the old instance must not end when
        // that instance's stream closes on dispose — it must follow the swap and stream the SECOND run.
        const string firstText = "first-run-answer";
        const string secondText = "second-run-answer";

        var createdAgents = new List<ObservableFakeAgent>();
        var agentCallCount = 0;

        var manager = CreateManager(new Dictionary<string, SubAgentTemplate>
        {
            ["owned"] = DummyTemplate("owned"),
        });

        manager.TestAgentFactoryOverride = (agentId, _) =>
        {
            var idx = Interlocked.Increment(ref agentCallCount);
            var agent = new ObservableFakeAgent
            {
                ThreadId = $"subagent-{agentId}",
                // Run 1 emits its text + a terminal completion; run 2 (the restart) emits a DISTINCT
                // text + completion. Each instance blocks its subscriptions open after its messages
                // until the instance is disposed (mirrors MultiTurnAgentBase completing subscriber
                // channels on DisposeAsync).
                RunMessages = idx == 1
                    ?
                    [
                        new TextMessage { Text = firstText, Role = Role.Assistant },
                        new RunCompletedMessage { CompletedRunId = "run-1" },
                    ]
                    :
                    [
                        new TextMessage { Text = secondText, Role = Role.Assistant },
                        new RunCompletedMessage { CompletedRunId = "run-2" },
                    ],
            };
            lock (createdAgents)
            {
                createdAgents.Add(agent);
            }

            return agent;
        };

        // Owned provider so completion disposes it, forcing the follow-up to take the restart path.
        manager.TestOwnedProviderOverride = (_, _) => new Mock<IStreamingAgent>().Object;

        var spawnJson = await manager.SpawnAsync("owned", "task", runInBackground: true);
        var agentId = ParseAgentId(spawnJson);

        // Deterministically wait for the first run to reach terminal completion (owned provider disposed).
        await WaitForConditionAsync(
            () =>
            {
                try { return manager.Peek(agentId).Contains("\"completed\"", StringComparison.Ordinal); }
                catch { return false; }
            },
            TimeSpan.FromSeconds(10));

        using var observeCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var collected = new List<IMessage>();
        var sawFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sawSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Start the restart-spanning observer BEFORE the follow-up. It subscribes to the finished
        // instance (getting run 1), stays attached, and must transparently pick up the replacement.
        var observerTask = Task.Run(async () =>
        {
            await foreach (var msg in manager.SubscribeToAgentAcrossRestartsAsync(agentId, observeCts.Token))
            {
                lock (collected)
                {
                    collected.Add(msg);
                }

                if (msg is TextMessage tm && tm.Text == firstText)
                {
                    _ = sawFirst.TrySetResult();
                }

                if (msg is TextMessage tm2 && tm2.Text == secondText)
                {
                    _ = sawSecond.TrySetResult();
                    return; // seen the restarted turn spanning the swap — done.
                }
            }
        }, observeCts.Token);

        // Gate the follow-up on the observer having attached to and drained run 1, so the swap is
        // genuinely observed mid-subscription (not a post-hoc re-resolve).
        await sawFirst.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Act: relay a follow-up to the finished child -> owned-provider restart -> fresh instance.
        _ = await manager.SendMessageAsync(agentId, "continue", runInBackground: true);

        lock (createdAgents)
        {
            createdAgents.Should().HaveCount(2, "the restart must have created a replacement instance");
        }

        // The observer, without ending between runs, must yield the SECOND run's text (it spanned the
        // owned-provider instance swap).
        await sawSecond.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await observerTask.WaitAsync(TimeSpan.FromSeconds(10));

        List<IMessage> snapshot;
        lock (collected)
        {
            snapshot = [.. collected];
        }

        foreach (var m in snapshot)
        {
            _output.WriteLine($"observed {m.GetType().Name} {(m as TextMessage)?.Text}");
        }

        snapshot.OfType<TextMessage>().Select(t => t.Text).Should().Contain(firstText);
        snapshot.OfType<TextMessage>().Select(t => t.Text).Should().Contain(
            secondText,
            "the observer must follow the child across the owned-provider restart swap and stream run 2");

        // The run-1 text must precede the run-2 text in a single uninterrupted enumeration (no early end).
        var firstIndex = snapshot.FindIndex(m => m is TextMessage t && t.Text == firstText);
        var secondIndex = snapshot.FindIndex(m => m is TextMessage t && t.Text == secondText);
        secondIndex.Should().BeGreaterThan(
            firstIndex, "the second run's text arrives after the first on the same continuous stream");
    }

    [Fact]
    public async Task SubscribeAcrossRestarts_WhenManagerDisposes_EndsCleanly()
    {
        // Teardown path: DisposeAsync signals every state's replacement awaitable with null, so an
        // observer blocked on a still-running child unblocks and the enumeration ends (rather than
        // hanging forever waiting for a swap that will never come).
        const string attachedSentinel = "attached-sentinel";

        var manager = CreateManager(new Dictionary<string, SubAgentTemplate>
        {
            ["owned"] = DummyTemplate("owned"),
        });

        manager.TestAgentFactoryOverride = (agentId, _) => new ObservableFakeAgent
        {
            ThreadId = $"subagent-{agentId}",
            // One non-terminal sentinel, then the subscription blocks open until teardown. The sentinel
            // lets the observer prove (deterministically, no sleep) it has captured the replacement
            // awaitable and is actively subscribed before we dispose.
            RunMessages = [new TextMessage { Text = attachedSentinel, Role = Role.Assistant }],
        };

        var spawnJson = await manager.SpawnAsync("owned", "task", runInBackground: true);
        var agentId = ParseAgentId(spawnJson);

        using var observeCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var attached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var observerTask = Task.Run(async () =>
        {
            await foreach (var msg in manager.SubscribeToAgentAcrossRestartsAsync(agentId, observeCts.Token))
            {
                if (msg is TextMessage tm && tm.Text == attachedSentinel)
                {
                    // The observer has captured the replacement awaitable and is subscribed to the
                    // running child — safe to tear down and assert the teardown ends the stream.
                    _ = attached.TrySetResult();
                }
            }
        }, observeCts.Token);

        await attached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Act: tearing down the manager must end the observer without cancelling the observer's own token.
        await manager.DisposeAsync();
        _manager = null; // already disposed; avoid a double dispose in DisposeAsync().

        var completed = await Task.WhenAny(observerTask, Task.Delay(TimeSpan.FromSeconds(10)));
        completed.Should().BeSameAs(
            observerTask, "manager teardown signals a null replacement so the observer ends cleanly");

        // Surface any fault and confirm the observer was not force-cancelled (it ended via the null signal).
        await observerTask;
        observeCts.IsCancellationRequested.Should().BeFalse();
    }

    #region Helpers

    private SubAgentManager CreateManager(
        IReadOnlyDictionary<string, SubAgentTemplate> templates,
        int maxConcurrent = 5)
    {
        var options = new SubAgentOptions
        {
            Templates = templates,
            MaxConcurrentSubAgents = maxConcurrent,
        };

        var manager = new SubAgentManager(
            parentAgent: _parentMock.Object,
            parentContracts: [],
            parentHandlers: new Dictionary<string, ToolHandler>(),
            options: options,
            source: new MutableSubAgentTemplateSource(options.Templates));
        _manager = manager;
        return manager;
    }

    /// <summary>
    /// A template whose real <see cref="SubAgentTemplate.AgentFactory"/> is never invoked (bypassed
    /// by <see cref="SubAgentManager.TestAgentFactoryOverride"/>).
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

    private static string ParseAgentId(string spawnJson)
    {
        using var doc = JsonDocument.Parse(spawnJson);
        return doc.RootElement.GetProperty("agent_id").GetString()!;
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25);
        }
    }

    #endregion
}

/// <summary>
/// Minimal <see cref="IMultiTurnAgent"/> test double whose subscriptions faithfully END when the
/// instance is disposed — mirroring <c>MultiTurnAgentBase.DisposeAsync</c> completing subscriber
/// channels. Each <see cref="SubscribeAsync"/> call yields the configured <see cref="RunMessages"/> then
/// blocks the subscription open until this instance is disposed (or the subscriber cancels), so a test
/// can drive an owned-provider restart's instance swap and prove an observer follows it across the swap.
/// </summary>
internal sealed class ObservableFakeAgent : IMultiTurnAgent
{
    private readonly TaskCompletionSource _disposed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public required IReadOnlyList<IMessage> RunMessages { get; init; }

    public string ThreadId { get; init; } = "fake-thread";

    public string? CurrentRunId => null;

    public bool IsRunning { get; private set; }

    public ValueTask<SendReceipt> SendAsync(
        List<IMessage> messages,
        string? inputId = null,
        string? parentRunId = null,
        CancellationToken ct = default)
    {
        return new ValueTask<SendReceipt>(
            new SendReceipt(Guid.NewGuid().ToString("N"), inputId, DateTimeOffset.UtcNow));
    }

    public ValueTask<SendReceipt?> TrySendAsync(
        List<IMessage> messages,
        string? inputId = null,
        string? parentRunId = null,
        CancellationToken ct = default)
    {
        throw new NotSupportedException("Not used by these tests.");
    }

    public IAsyncEnumerable<IMessage> ExecuteRunAsync(
        UserInput userInput,
        CancellationToken ct = default)
    {
        throw new NotSupportedException("Not used by these tests.");
    }

    public async IAsyncEnumerable<IMessage> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var msg in RunMessages)
        {
            ct.ThrowIfCancellationRequested();
            yield return msg;
            await Task.Yield();
        }

        // Keep the subscription open until this instance is disposed (a restart disposes the previous
        // loop) or the subscriber cancels — then end, just as a real agent's channel closes on dispose.
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var reg = ct.Register(() => cancelled.TrySetResult());
        _ = await Task.WhenAny(_disposed.Task, cancelled.Task);
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
        _ = _disposed.TrySetResult();
        return ValueTask.CompletedTask;
    }
}
