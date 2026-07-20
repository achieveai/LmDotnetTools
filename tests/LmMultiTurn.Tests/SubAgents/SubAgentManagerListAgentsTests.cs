using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
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
/// Unit tests for the presentation-only read seam added for WI #194:
/// <see cref="SubAgentManager.ListAgents"/> (a side-effect-free snapshot of every registered
/// child) and <see cref="SubAgentManager.TryGetAgent"/> (non-throwing resolve of one child by
/// id or caller-supplied name). These exercise read-only exposure only — no change to spawn,
/// send, monitor, restart, or dispose behavior.
/// </summary>
public class SubAgentManagerListAgentsTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly Mock<IMultiTurnAgent> _parentMock = new();
    private SubAgentManager? _manager;

    public SubAgentManagerListAgentsTests(ITestOutputHelper output)
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
    public async Task ListAgents_ReturnsSnapshotForEveryRegisteredAgent_RunningAndFinished()
    {
        // Two real sub-agent loops: one whose provider completes (Completed, has turn activity) and
        // one whose provider blocks (Running, no completed turn), so the snapshot must reflect both
        // lifecycle states at once.
        var finisherProvider = new Mock<IStreamingAgent>();
        SetupStreamingResponse(finisherProvider, (_, _) => ToAsyncEnumerable(
            new TextMessage { Text = "done", Role = Role.Assistant }));

        var runnerProvider = new Mock<IStreamingAgent>();
        SetupStreamingResponse(runnerProvider, (_, ct) => BlockingStream(ct));

        var manager = CreateManager(new Dictionary<string, SubAgentTemplate>
        {
            ["finisher"] = TemplateFor(finisherProvider.Object),
            ["runner"] = TemplateFor(runnerProvider.Object),
        });

        var finisherJson = await manager.SpawnAsync(
            "finisher", "finish the report", name: "fin", runInBackground: true);
        var finisherId = ParseAgentId(finisherJson);

        // Block until the finisher genuinely reaches its terminal completion, so its snapshot status
        // is deterministic rather than a timing race.
        _ = await manager.ObserveCompletionAsync(finisherId, CancellationToken.None);

        var runnerJson = await manager.SpawnAsync(
            "runner", "keep running", name: "run", runInBackground: true);
        var runnerId = ParseAgentId(runnerJson);

        var snapshots = manager.ListAgents();
        foreach (var s in snapshots)
        {
            _output.WriteLine(
                $"snapshot id={s.AgentId} name={s.Name} template={s.TemplateName} " +
                $"task={s.Task} status={s.Status} thread={s.ThreadId} last={s.LastActivityUtc:o}");
        }

        snapshots.Should().HaveCount(2);

        var finisher = snapshots.Single(s => s.AgentId == finisherId);
        finisher.Name.Should().Be("fin");
        finisher.TemplateName.Should().Be("finisher");
        finisher.Task.Should().Be("finish the report");
        finisher.Status.Should().Be(SubAgentStatus.Completed);
        finisher.ThreadId.Should().Be($"subagent-{finisherId}");
        finisher.LastActivityUtc.Should().NotBeNull(
            "the finisher produced an assistant turn, so its turn buffer is non-empty");

        var runner = snapshots.Single(s => s.AgentId == runnerId);
        runner.Name.Should().Be("run");
        runner.TemplateName.Should().Be("runner");
        runner.Task.Should().Be("keep running");
        runner.Status.Should().Be(SubAgentStatus.Running);
        runner.ThreadId.Should().Be($"subagent-{runnerId}");
    }

    [Fact]
    public async Task TryGetAgent_ResolvesByIdAndByName_ReturnsFalseForUnknown()
    {
        var provider = new Mock<IStreamingAgent>();
        SetupStreamingResponse(provider, (_, _) => ToAsyncEnumerable(
            new TextMessage { Text = "done", Role = Role.Assistant }));

        var manager = CreateManager(new Dictionary<string, SubAgentTemplate>
        {
            ["worker"] = TemplateFor(provider.Object),
        });

        var spawnJson = await manager.SpawnAsync(
            "worker", "do work", name: "alpha", runInBackground: true);
        var agentId = ParseAgentId(spawnJson);

        manager.TryGetAgent(agentId, out var byId).Should().BeTrue();
        byId.Should().NotBeNull();
        byId!.ThreadId.Should().Be($"subagent-{agentId}");

        manager.TryGetAgent("alpha", out var byName).Should().BeTrue();
        byName.Should().NotBeNull();
        ReferenceEquals(byId, byName).Should().BeTrue(
            "resolving by id and by name must return the same live instance");

        manager.TryGetAgent("does-not-exist", out var missing).Should().BeFalse();
        missing.Should().BeNull();
    }

    [Fact]
    public async Task TryGetAgent_AfterRestart_ReturnsCurrentInstance()
    {
        // A finished owned-provider sub-agent, when sent a new message, is restarted with a fresh
        // agent instance. TryGetAgent must return the CURRENT (post-restart) instance, not the stale
        // one captured at spawn time.
        var createdAgents = new List<FakeMultiTurnAgent>();
        var agentCallCount = 0;

        var manager = CreateManager(new Dictionary<string, SubAgentTemplate>
        {
            ["owned"] = DummyTemplate("owned"),
        });

        manager.TestAgentFactoryOverride = (agentId, _) =>
        {
            var idx = Interlocked.Increment(ref agentCallCount);
            var agent = new FakeMultiTurnAgent
            {
                ThreadId = $"subagent-{agentId}",
                SubscribeImpl = (_, ct) => idx == 1
                    ? FakeMultiTurnAgent.CompleteOnceThenWaitForeverStream("run-1", ct)
                    : FakeMultiTurnAgent.WaitForeverStream(ct),
            };
            lock (createdAgents)
            {
                createdAgents.Add(agent);
            }

            return agent;
        };

        // Owned provider so completion disposes it, forcing the restart to rebuild a fresh agent.
        manager.TestOwnedProviderOverride = (_, _) => new Mock<IStreamingAgent>().Object;

        var spawnJson = await manager.SpawnAsync("owned", "task", runInBackground: true);
        var agentId = ParseAgentId(spawnJson);

        await WaitForConditionAsync(
            () =>
            {
                try { return manager.Peek(agentId).Contains("\"completed\""); }
                catch { return false; }
            },
            TimeSpan.FromSeconds(10));

        manager.TryGetAgent(agentId, out var beforeRestart).Should().BeTrue();
        FakeMultiTurnAgent first;
        lock (createdAgents)
        {
            first = createdAgents[0];
        }

        ReferenceEquals(beforeRestart, first).Should().BeTrue(
            "before the restart the resolved instance is the original spawn instance");

        // Act: send to the finished agent -> restart with a fresh instance.
        _ = await manager.SendMessageAsync(agentId, "continue", runInBackground: true);

        FakeMultiTurnAgent second;
        lock (createdAgents)
        {
            createdAgents.Should().HaveCount(2, "the restart must have created a replacement instance");
            second = createdAgents[1];
        }

        manager.TryGetAgent(agentId, out var afterRestart).Should().BeTrue();
        ReferenceEquals(afterRestart, second).Should().BeTrue(
            "TryGetAgent must return the current post-restart instance");
        ReferenceEquals(afterRestart, first).Should().BeFalse(
            "TryGetAgent must not return the stale pre-restart instance");
        _output.WriteLine(
            $"first={first.ThreadId}#{first.GetHashCode()} second={second.ThreadId}#{second.GetHashCode()} " +
            $"resolved=#{afterRestart!.GetHashCode()}");
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

    private static SubAgentTemplate TemplateFor(IStreamingAgent provider)
    {
        return new SubAgentTemplate
        {
            SystemPrompt = "You are a test agent.",
            AgentFactory = () => provider,
        };
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

    private static void SetupStreamingResponse(
        Mock<IStreamingAgent> provider,
        Func<GenerateReplyOptions?, CancellationToken, IAsyncEnumerable<IMessage>> streamFactory)
    {
        provider
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<IMessage> _, GenerateReplyOptions? options, CancellationToken ct) =>
                Task.FromResult(streamFactory(options, ct)));
    }

    private static string ParseAgentId(string spawnJson)
    {
        using var doc = JsonDocument.Parse(spawnJson);
        return doc.RootElement.GetProperty("agent_id").GetString()!;
    }

    private static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(params IMessage[] messages)
    {
        foreach (var msg in messages)
        {
            yield return msg;
            await Task.Yield();
        }
    }

    /// <summary>
    /// A provider stream that never yields and only unwinds on cancellation — keeps the sub-agent's
    /// run in progress (Running) deterministically without any timing dependence.
    /// </summary>
    private static async IAsyncEnumerable<IMessage> BlockingStream(
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        yield break;
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
