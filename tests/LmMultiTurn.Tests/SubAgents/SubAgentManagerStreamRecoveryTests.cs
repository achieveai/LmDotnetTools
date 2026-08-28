using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests.SubAgents;

/// <summary>
/// Pins how <see cref="SubAgentManager"/>'s per-sub-agent monitor must react to a
/// <see cref="StreamRecoveryMessage"/> on the subscription it watches the run through.
/// <para>
/// The subscription is the monitor's ONLY view of the sub-agent. A TERMINAL recovery reason
/// (<see cref="StreamRecoveryReason.SlowConsumer"/>) ends that stream CLEANLY — the publisher
/// completes the channel rather than faulting it — so a monitor that does not inspect the message
/// falls straight out of its <c>await foreach</c> with no exception, resolves nothing, and leaves
/// every <c>WaitAgent</c>/<c>WaitForAgents</c>/<c>AwaitCompletionAsync</c> caller blocked forever
/// on a run nobody is watching. A NON-terminal reason
/// (<see cref="StreamRecoveryReason.ReplayTruncated"/>) is the opposite mistake: it only reports a
/// withheld replay PREFIX and the live tail still arrives on the same subscription, so terminalizing
/// on it would fail runs that are perfectly healthy.
/// </para>
/// <para>
/// These use <see cref="SubAgentManager.TestAgentFactoryOverride"/> to substitute a
/// <see cref="FakeMultiTurnAgent"/> — the real fan-out only emits these messages under genuine
/// backpressure/buffer-cap conditions that cannot be staged deterministically — while still
/// exercising the real spawn/monitor plumbing. Every wait is bounded, so a regression fails one
/// test instead of hanging the suite.
/// </para>
/// </summary>
public class SubAgentManagerStreamRecoveryTests : IAsyncLifetime
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private readonly Mock<IMultiTurnAgent> _parentMock = new();
    private SubAgentManager? _manager;

    public Task InitializeAsync()
    {
        _parentMock
            .Setup(p =>
                p.SendAsync(
                    It.IsAny<List<IMessage>>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new SendReceipt("receipt-1", null, DateTimeOffset.UtcNow));

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_manager != null)
        {
            // Bounded: an unbounded teardown turns one stalled test into an aborted run (#362).
            await Wait.ForTeardownAsync(_manager, "the sub-agent manager under test");
        }
    }

    [Fact]
    public async Task Monitor_TerminalStreamRecovery_FaultsTheCompletionLatchInsteadOfHanging()
    {
        // The stream is severed and then ends NORMALLY, which is precisely why this was invisible:
        // no exception reaches the monitor's catch, so the run stayed "running" forever with a
        // latch nobody would ever resolve.
        var agentId = await SpawnWithStreamAsync((_, ct) => SeveredStream(StreamRecoveryReason.SlowConsumer, ct));

        var observe = () => _manager!.ObserveCompletionAsync(agentId, CancellationToken.None).WaitAsync(Bound);

        var thrown = await observe
            .Should()
            .ThrowAsync<InvalidOperationException>(
                "a severed subscription must terminalize the run it was the only view of"
            );
        _ = thrown.WithMessage(
            "*severed (SlowConsumer)*",
            "the reason belongs in the message — it is the only clue the caller gets"
        );
    }

    [Fact]
    public async Task Monitor_TerminalStreamRecovery_MarksTheRunErroredSoObserversSeeATerminalState()
    {
        // Faulting the latch is only half the contract: the agent's own record must go terminal too,
        // or CheckAgent/ListAgents keep reporting a "running" sub-agent that no longer exists.
        var agentId = await SpawnWithStreamAsync((_, ct) => SeveredStream(StreamRecoveryReason.SlowConsumer, ct));

        // Observing is what deterministically waits for the monitor to finish reacting; the status
        // is stamped on that same path, before the latch is resolved.
        var observe = () => _manager!.ObserveCompletionAsync(agentId, CancellationToken.None).WaitAsync(Bound);
        _ = await observe.Should().ThrowAsync<InvalidOperationException>();

        using var peek = JsonDocument.Parse(_manager!.Peek(agentId));
        peek.RootElement.GetProperty("status")
            .GetString()
            .Should()
            .Be("error", "a run whose stream was severed is finished, not still running");
    }

    [Fact]
    public async Task Monitor_ReplayTruncatedRecovery_IsNonTerminalAndKeepsWatchingForCompletion()
    {
        // ReplayTruncated LEADS the stream: it says the buffered prefix was withheld, not that the
        // subscription is over. Treating it as terminal would fail a healthy run whose only sin was
        // joining late, so the monitor must ignore it and go on to observe the real completion.
        var agentId = await SpawnWithStreamAsync((_, ct) => TruncatedThenCompletingStream("run-1", "the answer", ct));

        var result = await _manager!.ObserveCompletionAsync(agentId, CancellationToken.None).WaitAsync(Bound);

        result.Should().Be("the answer", "the live tail follows a truncation advisory on the very same subscription");
    }

    #region Helpers

    /// <summary>
    /// Mirrors a slow-consumer eviction exactly: the advisory is the LAST message and the stream
    /// then ends cleanly (the real publisher calls <c>TryComplete()</c>, never <c>TryComplete(ex)</c>).
    /// </summary>
    private static async IAsyncEnumerable<IMessage> SeveredStream(
        StreamRecoveryReason reason,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        yield return new StreamRecoveryMessage("fake-thread", "run-1", "gen-1", reason);
        await Task.CompletedTask;
    }

    /// <summary>
    /// Mirrors a late join to an in-flight run: the truncation advisory leads, then the live tail —
    /// answer text and the run's terminal completion — arrives on the same subscription. Stays open
    /// afterwards like a real agent's subscription does between runs.
    /// </summary>
    private static async IAsyncEnumerable<IMessage> TruncatedThenCompletingStream(
        string runId,
        string answer,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        yield return new StreamRecoveryMessage("fake-thread", runId, "gen-1", StreamRecoveryReason.ReplayTruncated);
        yield return new TextMessage
        {
            Text = answer,
            Role = Role.Assistant,
            GenerationId = "gen-1",
        };
        yield return new RunCompletedMessage { CompletedRunId = runId };
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }

    /// <summary>
    /// Spawns one background sub-agent whose monitor reads <paramref name="subscribe"/>, and returns
    /// its agent id.
    /// </summary>
    private async Task<string> SpawnWithStreamAsync(Func<int, CancellationToken, IAsyncEnumerable<IMessage>> subscribe)
    {
        _manager = CreateManager();
        _manager.TestAgentFactoryOverride = (_, _) => new FakeMultiTurnAgent { SubscribeImpl = subscribe };

        var json = await _manager.SpawnAsync("worker", "task", runInBackground: true);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("agent_id").GetString()!;
    }

    private SubAgentManager CreateManager()
    {
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["worker"] = new()
                {
                    Name = "worker",
                    SystemPrompt = "You are a test agent.",
                    AgentFactory = () =>
                        throw new NotSupportedException(
                            "Bypassed by TestAgentFactoryOverride; should never be invoked."
                        ),
                },
            },
            MaxConcurrentSubAgents = 5,
        };

        return new SubAgentManager(
            parentAgent: _parentMock.Object,
            parentContracts: [],
            parentHandlers: new Dictionary<string, ToolHandler>(),
            options: options,
            source: new MutableSubAgentTemplateSource(options.Templates)
        );
    }

    #endregion
}
