using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Persistence.Models;
using Moq;

namespace CodeReviewDaemon.Sample.Tests.Agents;

/// <summary>
/// Unit tests for <see cref="InProcessReviewSubAgentCompletionSource"/>: it must read every direct child
/// from the EXACT live <see cref="SubAgentManager"/> passed into it (constructed in the same call stack,
/// per Task 4's brief — no loop-lookup registry), and the <see cref="ReviewSubAgentNode"/>s it returns must
/// carry no live execution handle back into that manager (a value-copy snapshot the barrier can compare
/// safely, exactly like <see cref="SubAgentManager.ListAgents"/> already promises for its own callers).
/// </summary>
public sealed class InProcessReviewSubAgentCompletionSourceTests : IAsyncLifetime
{
    private readonly Mock<IMultiTurnAgent> _parentMock = new();
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

    [Fact]
    public async Task GetSnapshotAsync_MapsEveryDirectChildFromTheExactLiveManager_AsAnImmutableSnapshot()
    {
        // A finisher (reaches Completed deterministically via ObserveCompletionAsync) and a blocking
        // runner (stays Running) — the same construction pattern SubAgentManagerListAgentsTests uses to
        // exercise both lifecycle states in one manager, built directly in this call stack (no factory
        // registry lookup), exactly as the brief requires.
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
        _ = await manager.ObserveCompletionAsync(finisherId, CancellationToken.None);

        var runnerJson = await manager.SpawnAsync(
            "runner", "keep running", name: "run", runInBackground: true);
        var runnerId = ParseAgentId(runnerJson);

        var source = new InProcessReviewSubAgentCompletionSource(manager);

        var snapshot = await source.GetSnapshotAsync(TestRun(), "thread-root", CancellationToken.None);

        snapshot.Nodes.Should().HaveCount(2);

        var finisher = snapshot.Nodes.Single(n => n.AgentId == finisherId);
        finisher.Name.Should().Be("fin");
        finisher.Template.Should().Be("finisher");
        finisher.ThreadId.Should().Be($"subagent-{finisherId}");
        finisher.ParentThreadId.Should().Be("thread-root");
        finisher.Depth.Should().Be(1);
        finisher.Status.Should().Be(ReviewSubAgentStatus.Completed);
        finisher.TerminalAtUtc.Should().NotBeNull(
            "the finisher reached a terminal completion, so its terminal timestamp must be recorded");

        var runner = snapshot.Nodes.Single(n => n.AgentId == runnerId);
        runner.Name.Should().Be("run");
        runner.Template.Should().Be("runner");
        runner.ParentThreadId.Should().Be("thread-root");
        runner.Depth.Should().Be(1);
        runner.Status.Should().Be(ReviewSubAgentStatus.Running);
        runner.TerminalAtUtc.Should().BeNull("a still-running sub-agent has no terminal instant yet");

        // No execution handle escapes: dispose the manager AFTER taking the snapshot and confirm the
        // already-returned nodes are unaffected — proving they are a value copy, not a lazy view back
        // into the manager's live state.
        await manager.DisposeAsync();
        _manager = null;
        finisher.AgentId.Should().Be(finisherId);
        runner.AgentId.Should().Be(runnerId);
    }

    [Fact]
    public async Task GetSnapshotAsync_ReturnsAnEmptyTree_WhenTheManagerHasNoChildrenYet()
    {
        var manager = CreateManager(new Dictionary<string, SubAgentTemplate>());
        var source = new InProcessReviewSubAgentCompletionSource(manager);

        var snapshot = await source.GetSnapshotAsync(TestRun(), "thread-root", CancellationToken.None);

        snapshot.Nodes.Should().BeEmpty();
    }

    [Fact]
    public void Constructor_ThrowsOnANullManager()
    {
        var act = () => new InProcessReviewSubAgentCompletionSource(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    #region Helpers

    private static ReviewRun TestRun() =>
        new()
        {
            RepoId = 1,
            PrId = "42",
            HeadSha = "head-sha",
            BaseSha = "base-sha",
            TriggerWatermark = "watermark-1",
            ReviewKind = "full",
            VariantId = "primary",
            Mode = "collect-only",
            Stage = ReviewStage.Reviewed,
            WorkflowStatus = WorkflowStatus.Running,
            PrLifecycleState = PrLifecycleState.Open,
        };

    private SubAgentManager CreateManager(IReadOnlyDictionary<string, SubAgentTemplate> templates)
    {
        var options = new SubAgentOptions { Templates = templates, MaxConcurrentSubAgents = 5 };

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

    /// <summary>A provider stream that never yields and only unwinds on cancellation — keeps the sub-agent
    /// in Running deterministically without any timing dependence.</summary>
    private static async IAsyncEnumerable<IMessage> BlockingStream([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        yield break;
    }

    #endregion
}
