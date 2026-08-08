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

namespace LmMultiTurn.Tests.SubAgents;

/// <summary>
/// Behavioural coverage for <c>WaitAgent</c>, the singular blocking wait offered ONLY in the legacy
/// (collaboration-off) surface. Without it a legacy agent that spawned in the background had no way to
/// stop other than polling <c>CheckAgent</c> in a loop, burning a turn per poll.
/// </summary>
/// <remarks>
/// Every case here is driven through real spawns and the manager's own observation primitives — no test
/// sleeps and no polling — because the point of the tool is that it does not poll either.
/// </remarks>
public class SubAgentWaitAgentToolTests : IAsyncLifetime
{
    private readonly Mock<IMultiTurnAgent> _parentMock = new();
    private readonly List<SubAgentManager> _managers = [];

    public Task InitializeAsync()
    {
        _ = _parentMock
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
        foreach (var manager in _managers)
        {
            await manager.DisposeAsync();
        }
    }

    [Fact]
    public void WaitAgentDescription_TellsTheModelWhichIdNamespaceItTakes()
    {
        // Agent ids and workflow ids look alike and are handed out by tools that sit side by side in the
        // Workspace Agent surface; naming the source of the id is what stops a workflowId being waited on.
        var (_, provider) = CreateManager(CompletingAgent("done"));

        provider.GetFunctions().Single(f => f.Contract.Name == "WaitAgent")
            .Contract.Description.Should()
            .Contain("Use an `agent_id` returned by `Agent`; do not pass workflow IDs.");
    }

    [Fact]
    public async Task WaitAgent_ReturnsOnceTheAgentFinishes()
    {
        var (manager, provider) = CreateManager(CompletingAgent("all done"));
        var agentId = await SpawnBackgroundAsync(manager);

        var payload = await InvokeAsync(provider, new { agent_id = agentId });

        payload.IsError.Should().BeFalse(payload.Text);
        using var doc = JsonDocument.Parse(payload.Text);
        doc.RootElement.GetProperty("status").GetString().Should().Be("completed");

        var agent = doc.RootElement.GetProperty("agent");
        agent.GetProperty("status").GetString().Should().Be("completed");
        agent.GetProperty("last_result").GetString().Should().Contain("all done",
            "the result is the whole reason to wait — a wait that ends without it costs another turn");
    }

    [Fact]
    public async Task WaitAgent_ReportsATerminalFailureInsteadOfBlockingForever()
    {
        // A failed child is just as terminal as a finished one. If the wait only ended on success, the
        // one outcome the caller most needs to hear about would be the one that hangs it.
        var (manager, provider) = CreateManager(FailingAgent());
        var agentId = await SpawnBackgroundAsync(manager);

        var payload = await InvokeAsync(provider, new { agent_id = agentId });

        payload.IsError.Should().BeFalse(payload.Text);
        using var doc = JsonDocument.Parse(payload.Text);
        doc.RootElement.GetProperty("status").GetString().Should().Be("completed");
        doc.RootElement.GetProperty("agent").GetProperty("status").GetString().Should().Be("error");
    }

    [Fact]
    public async Task WaitAgent_OnTimeout_ReportsTheAgentStillRunning()
    {
        var (manager, provider) = CreateManager(BlockingAgent());
        var agentId = await SpawnBackgroundAsync(manager);

        var payload = await InvokeAsync(provider, new { agent_id = agentId, timeout_seconds = 1 });

        payload.IsError.Should().BeFalse(payload.Text);
        using var doc = JsonDocument.Parse(payload.Text);
        doc.RootElement.GetProperty("status").GetString().Should().Be("timeout");
        doc.RootElement.GetProperty("agent").GetProperty("status").GetString().Should().Be("running",
            "the timeout abandons the observation only — nothing about the agent is cancelled");
    }

    [Fact]
    public async Task WaitAgent_UnknownId_NamesTheIdsThatWouldHaveWorked()
    {
        var (manager, provider) = CreateManager(BlockingAgent());
        var agentId = await SpawnBackgroundAsync(manager);

        var payload = await InvokeAsync(provider, new { agent_id = "not-an-agent" });

        payload.IsError.Should().BeTrue();
        payload.ErrorCode.Should().Be("unknown_agent");
        payload.Text.Should().Contain(agentId,
            "a mistyped id is a model mistake, and the fix is to show it the ids it could have used");
    }

    [Fact]
    public async Task WaitAgent_UnknownIdWithNoAgentsAtAll_StillExplainsItself()
    {
        var (_, provider) = CreateManager(CompletingAgent("done"));

        var payload = await InvokeAsync(provider, new { agent_id = "not-an-agent" });

        payload.IsError.Should().BeTrue();
        payload.ErrorCode.Should().Be("unknown_agent");
        payload.Text.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task WaitAgent_IsCancelledWithTheTurn()
    {
        var (manager, provider) = CreateManager(BlockingAgent());
        var agentId = await SpawnBackgroundAsync(manager);

        using var cts = new CancellationTokenSource();
        var waiting = InvokeAsync(provider, new { agent_id = agentId }, cts.Token);
        await cts.CancelAsync();

        var act = () => waiting;
        await act.Should().ThrowAsync<OperationCanceledException>(
            "an abandoned turn must not leave a tool call blocked on a child that never finishes");
    }

    #region The wait ended, but the agent never reached a terminal state

    /// <summary>
    /// A queued spawn whose start throws is the case where "the wait ended" and "the agent finished"
    /// come apart. The pump removes it from the queue BEFORE attempting the start and the failed start
    /// rolls its registration back, so the id ends up in neither table: the completion observation
    /// faults, and the post-wait peek misses. Reporting "completed" would tell the model its child
    /// succeeded while handing it nothing at all.
    /// </summary>
    [Fact]
    public async Task WaitAgent_WhenTheQueuedAgentFailsToStart_SaysSoInsteadOfClaimingCompletion()
    {
        var (manager, provider, release) = CreateManagerWithFullPool(ThrowingAgentFactory());
        _ = await SpawnBackgroundAsync(manager, "blocker");
        var queuedId = await SpawnBackgroundAsync(manager);

        // Deliberately not awaited. The handler runs synchronously as far as its first real await, so
        // by the time this call returns it is already past the id check and parked on the start latch.
        // Freeing the permit only AFTER that point is what makes the race deterministic rather than a
        // sleep-and-hope: without the ordering the spawn could fail before the wait ever began.
        var waiting = InvokeAsync(provider, new { agent_id = queuedId });
        release.SetResult();

        await AssertUnavailableAsync(waiting);
    }

    /// <summary>
    /// The same divergence reached by the other route: disposal cancels a still-queued spawn with the
    /// MANAGER's token, not the caller's, so the wait unblocks while the caller's own token stays
    /// perfectly healthy — there is no cancellation for the caller to observe and nothing to rethrow.
    /// </summary>
    [Fact]
    public async Task WaitAgent_WhenTheManagerIsDisposedMidWait_EndsWithAStableResult()
    {
        var (manager, provider, _) = CreateManagerWithFullPool(CompletingAgent("never reached"));
        _ = await SpawnBackgroundAsync(manager, "blocker");
        var queuedId = await SpawnBackgroundAsync(manager);

        var waiting = InvokeAsync(provider, new { agent_id = queuedId });
        await manager.DisposeAsync();

        await AssertUnavailableAsync(waiting);
    }

    /// <summary>
    /// The shared verdict for both routes: a parseable, non-throwing result that does not claim the
    /// agent finished. The <c>agent</c> null check is the one that pins the crash — the post-wait peek
    /// yields an EMPTY string on a miss, and handing that to the JSON parser throws.
    /// </summary>
    private static async Task AssertUnavailableAsync(Task<ToolHandlerResultPayload> waiting)
    {
        var payload = await waiting;

        payload.IsError.Should().BeFalse(payload.Text);
        using var doc = JsonDocument.Parse(payload.Text);

        doc.RootElement.GetProperty("status").GetString().Should().Be("unavailable",
            "the wait ended without the agent reaching a terminal state, which is neither completion nor timeout");
        doc.RootElement.GetProperty("agent").ValueKind.Should().Be(JsonValueKind.Null,
            "there is no snapshot to report, and an empty peek result must never reach the parser");
        doc.RootElement.GetProperty("detail").GetString().Should().NotBeNullOrWhiteSpace(
            "a status the model has never seen before is only actionable if it says what happened");
    }

    #endregion

    #region The timeout_seconds argument

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitAgent_WithNoTimeoutRequested_WaitsWithoutACapRatherThanBeingRejected(
        bool asExplicitNull)
    {
        // The contract for an omitted optional cap is "no cap", and validating the argument must not
        // cost that. An explicit null is the same statement — models routinely emit one for a
        // parameter they chose not to set — so it must be accepted on the identical path, not
        // rejected as a malformed integer.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (manager, provider) = CreateManager(GatedAgent(gate.Task));
        var agentId = await SpawnBackgroundAsync(manager);

        object args = asExplicitNull
            ? new { agent_id = agentId, timeout_seconds = (int?)null }
            : new { agent_id = agentId };
        var wait = InvokeAsync(provider, args);

        // Non-vacuity: the wait is genuinely still waiting. Were the value rejected, this would
        // already have produced a result rather than being parked on the agent.
        wait.IsCompleted.Should().BeFalse("an uncapped wait ends only when the agent does");

        gate.SetResult();
        var payload = await wait.WaitAsync(Bound);

        payload.IsError.Should().BeFalse(payload.Text);
        using var doc = JsonDocument.Parse(payload.Text);
        doc.RootElement.GetProperty("status").GetString().Should().Be("completed");
    }

    [Theory]
    [InlineData("\"abc\"", "a value that is not a number at all")]
    [InlineData("\"\"", "an empty string")]
    [InlineData("true", "a value of the wrong JSON type")]
    [InlineData("0", "zero")]
    [InlineData("-5", "a negative")]
    [InlineData("\"-5\"", "a negative sent as a string")]
    public async Task WaitAgent_UnusableTimeout_IsRejectedInsteadOfSilentlyWaitingForever(
        string rawTimeout,
        string because)
    {
        // Every one of these used to parse to null — indistinguishable from omitted — and the
        // `is > 0` gate then turned it into the UNBOUNDED wait the model passed timeout_seconds
        // specifically to avoid. A cap that was asked for and not applied is an error, not a
        // default: say so, so the model can correct the call. The agent here never finishes, so a
        // regression does not merely assert wrong — it hangs against the bound.
        var (manager, provider) = CreateManager(BlockingAgent());
        var agentId = await SpawnBackgroundAsync(manager);

        // Raw JSON rather than an anonymous object, so the wrong-type and empty-string shapes a real
        // model emits are exercised exactly as they arrive on the wire.
        var payload = await InvokeRawAsync(
            provider, $$"""{"agent_id":"{{agentId}}","timeout_seconds":{{rawTimeout}}}""").WaitAsync(Bound);

        payload.IsError.Should().BeTrue($"{because} cannot be honoured as a cap");
        payload.ErrorCode.Should().Be("invalid_args");
        payload.Text.Should().Contain("positive whole number",
            "the rejection has to tell the model what would have worked");
    }

    [Fact]
    public async Task WaitAgent_TimeoutSentAsANumericString_IsAcceptedLikeTheInteger()
    {
        // Models routinely emit integers as strings, and this parser has always accepted them. The
        // validation must not narrow that: rejecting "600" would break working callers. Asserting on
        // a completing agent keeps this free of any real delay — the cap is accepted, never reached.
        // (WaitAgent_OnTimeout_ReportsTheAgentStillRunning covers a positive cap actually firing.)
        var (manager, provider) = CreateManager(CompletingAgent("done"));
        var agentId = await SpawnBackgroundAsync(manager);

        var payload = await InvokeRawAsync(
            provider, $$"""{"agent_id":"{{agentId}}","timeout_seconds":"600"}""").WaitAsync(Bound);

        payload.IsError.Should().BeFalse(payload.Text);
        using var doc = JsonDocument.Parse(payload.Text);
        doc.RootElement.GetProperty("status").GetString().Should().Be("completed");
    }

    #endregion

    #region Helpers

    private (SubAgentManager Manager, SubAgentToolProvider Provider) CreateManager(
        Func<IStreamingAgent> agentFactory)
    {
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["test-agent"] = new()
                {
                    SystemPrompt = "You are a test agent.",
                    AgentFactory = agentFactory,
                },
            },
            MaxConcurrentSubAgents = 5,
        };

        var source = new MutableSubAgentTemplateSource(options.Templates);
        var manager = new SubAgentManager(
            parentAgent: _parentMock.Object,
            parentContracts: [],
            parentHandlers: new Dictionary<string, ToolHandler>(),
            options: options,
            source: source);

        _managers.Add(manager);
        return (manager, new SubAgentToolProvider(manager, source));
    }

    /// <summary>
    /// A manager whose ONLY concurrency permit is already held by a running "blocker", so the next
    /// background spawn is deferred to the queue instead of started. Returns the gate that ends the
    /// blocker — completing it frees the permit, which is what lets the pump dequeue.
    /// </summary>
    private (SubAgentManager Manager, SubAgentToolProvider Provider, TaskCompletionSource Release)
        CreateManagerWithFullPool(Func<IStreamingAgent> queuedAgentFactory)
    {
        // RunContinuationsAsynchronously: the test thread completes this, and it must not inline-run
        // the sub-agent's stream continuation on its way to the next assertion.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["blocker"] = new()
                {
                    SystemPrompt = "You hold the only permit.",
                    AgentFactory = GatedAgent(release.Task),
                },
                ["test-agent"] = new()
                {
                    SystemPrompt = "You are a test agent.",
                    AgentFactory = queuedAgentFactory,
                },
            },
            MaxConcurrentSubAgents = 1,
        };

        var source = new MutableSubAgentTemplateSource(options.Templates);
        var manager = new SubAgentManager(
            parentAgent: _parentMock.Object,
            parentContracts: [],
            parentHandlers: new Dictionary<string, ToolHandler>(),
            options: options,
            source: source);

        _managers.Add(manager);
        return (manager, new SubAgentToolProvider(manager, source), release);
    }

    /// <summary>Bound on every wait here, so a regression fails one test instead of hanging the suite.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static async Task<string> SpawnBackgroundAsync(
        SubAgentManager manager,
        string template = "test-agent")
    {
        var spawnJson = await manager.SpawnAsync(template, "Do some work", runInBackground: true);
        using var doc = JsonDocument.Parse(spawnJson);
        return doc.RootElement.GetProperty("agent_id").GetString()!;
    }

    private static async Task<ToolHandlerResultPayload> InvokeAsync(
        SubAgentToolProvider provider,
        object args,
        CancellationToken ct = default)
        => await InvokeRawAsync(provider, JsonSerializer.Serialize(args), ct);

    /// <summary>
    /// Invokes <c>WaitAgent</c> with a literal argument string, for the malformed shapes a real model
    /// emits that a typed object cannot express (wrong JSON type, empty string).
    /// </summary>
    private static async Task<ToolHandlerResultPayload> InvokeRawAsync(
        SubAgentToolProvider provider,
        string argsJson,
        CancellationToken ct = default)
    {
        var handler = provider.GetFunctions().Single(f => f.Contract.Name == "WaitAgent").Handler;
        var result = await handler(argsJson, new ToolCallContext(), ct);

        return result.Should().BeOfType<ToolHandlerResult.Resolved>().Subject.Payload;
    }

    private static Func<IStreamingAgent> CompletingAgent(string text) => () =>
    {
        var mock = new Mock<IStreamingAgent>();
        _ = mock
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(ToAsyncEnumerable(
                [new TextMessage { Text = text, Role = Role.Assistant }])));
        return mock.Object;
    };

    /// <summary>A sub-agent whose provider call throws, so its run ends in <c>error</c>.</summary>
    private static Func<IStreamingAgent> FailingAgent() => () =>
    {
        var mock = new Mock<IStreamingAgent>();
        _ = mock
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("API call failed"));
        return mock.Object;
    };

    /// <summary>A sub-agent that never finishes, so only a timeout or a cancellation can end the wait.</summary>
    private static Func<IStreamingAgent> BlockingAgent() => () =>
    {
        var mock = new Mock<IStreamingAgent>();
        _ = mock
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions?, CancellationToken>(
                (_, _, ct) => Task.FromResult(BlockingStream(ct)));
        return mock.Object;
    };

    private static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(
        List<IMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var message in messages)
        {
            ct.ThrowIfCancellationRequested();
            yield return message;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<IMessage> BlockingStream(
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        yield break;
    }

    /// <summary>A sub-agent that runs until the test releases <paramref name="gate"/>.</summary>
    private static Func<IStreamingAgent> GatedAgent(Task gate) => () =>
    {
        var mock = new Mock<IStreamingAgent>();
        _ = mock
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions?, CancellationToken>(
                (_, _, ct) => Task.FromResult(GatedStream(gate, ct)));
        return mock.Object;
    };

    private static async IAsyncEnumerable<IMessage> GatedStream(
        Task gate,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        yield return new TextMessage { Text = "blocker done", Role = Role.Assistant };
    }

    /// <summary>
    /// A template whose agent cannot be built at all. Queued behind a full pool, the throw lands in the
    /// pump at dequeue time — after the spawn already handed the caller a stable "queued" id.
    /// </summary>
    private static Func<IStreamingAgent> ThrowingAgentFactory() =>
        () => throw new InvalidOperationException("agent factory failed");

    #endregion
}
