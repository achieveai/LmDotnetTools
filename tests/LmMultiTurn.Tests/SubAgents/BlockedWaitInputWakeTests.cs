using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests.SubAgents;

/// <summary>
/// The handler half of "WaitAgents blocks everything": while a blocking wait holds the turn, the
/// owning agent cannot look at anything it has been sent, so a human who types while their agent is
/// waiting on a fan-out is ignored until the slowest child finishes.
/// </summary>
/// <remarks>
/// <para>
/// The fix races a third signal beside the completion tasks and the collaboration ledger: input
/// already queued for the OWNING agent, filtered by the same curated policy the run loop applies to
/// a parked <c>Wait</c>. Waking is never a cancellation — the waited agents keep running and their
/// results keep accruing — so every case here also checks that nothing was consumed.
/// </para>
/// <para>
/// The negative cases use a short <c>timeout_seconds</c> and assert <c>timeout</c> rather than
/// sleeping: a wrong policy ends the wait at once, so the expiry is what distinguishes "ignored the
/// chatter" from "never saw it".
/// </para>
/// </remarks>
public class BlockedWaitInputWakeTests : IAsyncLifetime
{
    private readonly PeekableParent _parent = new();
    private readonly List<SubAgentManager> _managers = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // Bounded AND best-effort, matching the other sub-agent suites: a throw mid-loop would leave
        // every LATER manager undisposed, trading one leak shape for another.
        List<Exception>? failures = null;
        foreach (var manager in _managers)
        {
            try
            {
                await Wait.ForTeardownAsync(manager, "a sub-agent manager created by this test");
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }
        }

        await _parent.DisposeAsync();

        if (failures is not null)
        {
            throw new AggregateException(failures);
        }
    }

    #region WaitForAgents - inputs that end the wait

    [Fact]
    public async Task AHumanMessageQueuedForTheWaiter_EndsTheWait_WithEveryTargetStillRunning()
    {
        // The user's report, in one call: the agent is parked on a fan-out and the person typing has
        // no way in. The wait has to end so the turn can read them - without touching the children.
        var (manager, provider) = CreateManager(CreateRegisteredRoot(), BlockingTemplate());
        var agentId = await SpawnAsync(provider);

        await QueueAsync(new TextMessage { Role = Role.User, Text = "stop, do the other thing first" });

        var payload = await InvokeAsync(provider, "WaitForAgents", new { agent_ids = agentId, timeout_seconds = 30 });

        using var doc = JsonDocument.Parse(payload.Text);
        var root = doc.RootElement;
        root.GetProperty("status").GetString().Should().Be("interrupted_by_input");
        root.GetProperty("input").GetProperty("kind").GetString().Should().Be("user_message");
        root.GetProperty("next_action").GetString().Should().Contain("Nothing was cancelled");
        root.GetProperty("agents").GetProperty("running").GetInt32().Should().Be(1);
        manager.TryPeek(agentId, out _).Should().BeTrue(because: "waking is not cancelling");
    }

    [Fact]
    public async Task TheInputThatEndedTheWait_IsStillQueuedForTheTurnThatFollows()
    {
        // The wait PEEKS. Consuming the message here would end the wait and lose the very thing it
        // woke for: the run loop would resume with an empty queue and the human would be ignored
        // twice over.
        var (_, provider) = CreateManager(CreateRegisteredRoot(), BlockingTemplate());
        var agentId = await SpawnAsync(provider);

        await QueueAsync(new TextMessage { Role = Role.User, Text = "answer me" });

        var payload = await InvokeAsync(provider, "WaitForAgents", new { agent_ids = agentId, timeout_seconds = 30 });
        StatusOf(payload).Should().Be("interrupted_by_input");

        _parent
            .Drain()
            .SelectMany(q => q.Input.Messages)
            .OfType<TextMessage>()
            .Select(t => t.Text)
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be("answer me");
    }

    [Theory]
    [InlineData(NotifyKinds.DescendantQuestion)]
    [InlineData(NotifyKinds.WorkflowCompletion)]
    public async Task AnOutOfBandNoticeWorthActingOn_EndsTheWait(string notifyKind)
    {
        // Both of these name work only the owner can do: a descendant is parked on a question the
        // human must be shown, and a finished workflow has a result nobody else will collect.
        var (_, provider) = CreateManager(CreateRegisteredRoot(), BlockingTemplate());
        var agentId = await SpawnAsync(provider);

        await QueueAsync(new NotifyMessage { NotifyKind = notifyKind, Label = "somebody-else" });

        var payload = await InvokeAsync(provider, "WaitForAgents", new { agent_ids = agentId, timeout_seconds = 30 });

        using var doc = JsonDocument.Parse(payload.Text);
        doc.RootElement.GetProperty("status").GetString().Should().Be("interrupted_by_input");
        doc.RootElement.GetProperty("input").GetProperty("kind").GetString().Should().Be(notifyKind);
        doc.RootElement.GetProperty("input").GetProperty("from").GetString().Should().Be("somebody-else");
    }

    [Fact]
    public async Task ACompletionNoticeForAnAgentThisWaitIsNotBlockedOn_EndsTheWait()
    {
        // A sibling fan-out finishing is news the owner is holding and cannot act on, because this
        // wait is blocked on somebody else entirely.
        var (_, provider) = CreateManager(CreateRegisteredRoot(), BlockingTemplate());
        var agentId = await SpawnAsync(provider);

        await QueueAsync(
            new NotifyMessage
            {
                NotifyKind = NotifyKinds.SubAgentCompletion,
                SourceToolName = "Agent",
                SourceToolCallId = "some-other-agent",
                Label = "researcher",
            }
        );

        var payload = await InvokeAsync(provider, "WaitForAgents", new { agent_ids = agentId, timeout_seconds = 30 });

        using var doc = JsonDocument.Parse(payload.Text);
        doc.RootElement.GetProperty("status").GetString().Should().Be("interrupted_by_input");
        doc.RootElement.GetProperty("input")
            .GetProperty("kind")
            .GetString()
            .Should()
            .Be(NotifyKinds.SubAgentCompletion);
    }

    #endregion

    #region WaitForAgents - inputs that must not end the wait

    [Theory]
    [InlineData(NotifyKinds.TodoNudge)]
    [InlineData(NotifyKinds.TodoDigest)]
    [InlineData(NotifyKinds.ContextDiscovery)]
    [InlineData(NotifyKinds.ClientNotification)]
    public async Task BackgroundChatterQueuedForTheWaiter_DoesNotEndTheWait(string notifyKind)
    {
        // None of these asks the owner for anything it could not do a minute later. Waking for them
        // would make every wait end immediately on a busy conversation, which is the opposite bug.
        var (_, provider) = CreateManager(CreateRegisteredRoot(), BlockingTemplate());
        var agentId = await SpawnAsync(provider);

        await QueueAsync(new NotifyMessage { NotifyKind = notifyKind, Label = "board" });

        var payload = await InvokeAsync(provider, "WaitForAgents", new { agent_ids = agentId, timeout_seconds = 1 });

        using var doc = JsonDocument.Parse(payload.Text);
        doc.RootElement.GetProperty("status").GetString().Should().Be("timeout");
        doc.RootElement.GetProperty("input").ValueKind.Should().Be(JsonValueKind.Null);
        doc.RootElement.GetProperty("next_action").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task ACompletionNoticeForAnAgentThisWaitIsBlockedOn_DoesNotEndTheWait()
    {
        // It is the wait's OWN result, and it arrives through the completion task with the agent's
        // output attached. Racing it here would end the wait a beat early, with the wrong status and
        // nothing to show for it.
        var (_, provider) = CreateManager(CreateRegisteredRoot(), BlockingTemplate());
        var agentId = await SpawnAsync(provider);

        await QueueAsync(
            new NotifyMessage
            {
                NotifyKind = NotifyKinds.SubAgentCompletion,
                SourceToolName = "Agent",
                SourceToolCallId = agentId,
                Label = "child",
            }
        );

        var payload = await InvokeAsync(provider, "WaitForAgents", new { agent_ids = agentId, timeout_seconds = 1 });

        StatusOf(payload).Should().Be("timeout");
    }

    [Fact]
    public async Task AWaitThatIgnoredATodoNudge_StillEndsWithCompletedWhenItsTargetFinishes()
    {
        // The other half of "does not wake": the wait is not merely quiet, it is still armed on the
        // thing it was asked to wait for.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (_, provider) = CreateManager(CreateRegisteredRoot(), GatedTemplate(release.Task));
        var agentId = await SpawnAsync(provider);

        await QueueAsync(new NotifyMessage { NotifyKind = NotifyKinds.TodoNudge, Label = "board" });

        var wait = InvokeAsync(provider, "WaitForAgents", new { agent_ids = agentId, timeout_seconds = 30 });
        release.SetResult();

        using var doc = JsonDocument.Parse((await wait).Text);
        doc.RootElement.GetProperty("status").GetString().Should().Be("completed");
        doc.RootElement.GetProperty("input").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task ATargetThatHasAlreadyFinished_OutranksAWakingInput()
    {
        // Precedence, not a tie-break dressed up as one. When both signals are true the agent's
        // result is the thing the caller asked for and the only one this call can hand back; the
        // message is still queued and arrives whole on the next turn either way. Reporting the
        // message instead would throw away a result and make the model wait for an agent that is
        // already done.
        var (_, provider) = CreateManager(CreateRegisteredRoot(), FinishingTemplate());
        var agentId = await SpawnAsync(provider);

        // Settle the child FIRST, so "it had finished" is a fact rather than a race with the input.
        var settled = await InvokeAsync(provider, "WaitForAgents", new { agent_ids = agentId, timeout_seconds = 30 });
        StatusOf(settled).Should().Be("completed", because: "the unchanged path must still report this");

        await QueueAsync(new TextMessage { Role = Role.User, Text = "meanwhile, about that other thing" });

        var payload = await InvokeAsync(provider, "WaitForAgents", new { agent_ids = agentId, timeout_seconds = 30 });

        using var doc = JsonDocument.Parse(payload.Text);
        doc.RootElement.GetProperty("status").GetString().Should().Be("completed");
        doc.RootElement.GetProperty("input").ValueKind.Should().Be(JsonValueKind.Null);
    }

    #endregion

    #region WaitAgent - the singular legacy wait

    [Fact]
    public async Task AHumanMessageQueuedForTheWaiter_AlsoEndsTheSingularLegacyWait()
    {
        // WaitAgent blocks the turn exactly as WaitForAgents does, and a legacy agent has no ledger
        // to be interrupted through at all, so it is the surface where a parked wait was most total.
        var (_, provider) = CreateManager(collaboration: null, BlockingTemplate());
        var agentId = await SpawnLegacyAsync(provider);

        await QueueAsync(new TextMessage { Role = Role.User, Text = "come back" });

        var payload = await InvokeAsync(provider, "WaitAgent", new { agent_id = agentId, timeout_seconds = 30 });

        using var doc = JsonDocument.Parse(payload.Text);
        doc.RootElement.GetProperty("status").GetString().Should().Be("interrupted_by_input");
        doc.RootElement.GetProperty("input").GetProperty("kind").GetString().Should().Be("user_message");
        doc.RootElement.GetProperty("next_action").GetString().Should().Contain("still running");
    }

    [Fact]
    public async Task ATodoNudgeQueuedForTheWaiter_DoesNotEndTheSingularLegacyWait()
    {
        var (_, provider) = CreateManager(collaboration: null, BlockingTemplate());
        var agentId = await SpawnLegacyAsync(provider);

        await QueueAsync(new NotifyMessage { NotifyKind = NotifyKinds.TodoNudge, Label = "board" });

        var payload = await InvokeAsync(provider, "WaitAgent", new { agent_id = agentId, timeout_seconds = 1 });

        StatusOf(payload).Should().Be("timeout");
    }

    #endregion

    #region The tool contract

    [Fact]
    public void BothWaitDescriptors_StateTheNewEnding_AndThatChatterDoesNotCauseIt()
    {
        // A status the model is never told about is a status it cannot branch on: it would read the
        // unfamiliar ending as a failure and either give up or re-wait blindly.
        var (_, collaborative) = CreateManager(CreateRegisteredRoot(), BlockingTemplate());
        var (_, legacy) = CreateManager(collaboration: null, BlockingTemplate());

        var waitForAgents = Description(collaborative, "WaitForAgents");
        var waitAgent = Description(legacy, "WaitAgent");

        waitForAgents.Should().Contain("interrupted_by_input");
        waitForAgents.Should().Contain("background");
        waitAgent.Should().Contain("interrupted_by_input");
        waitAgent.Should().Contain("background");
    }

    #endregion

    #region Helpers

    /// <summary>
    /// A parent with a REAL input queue. The production waits peek the owning agent's queue through
    /// <see cref="MultiTurnAgentBase"/>, so a mocked <see cref="IMultiTurnAgent"/> — what the other
    /// sub-agent suites use — has nothing to peek and would make every case here vacuous.
    /// </summary>
    private sealed class PeekableParent() : MultiTurnAgentBase("blocked-wait-input-wake")
    {
        protected override Task RunLoopAsync(CancellationToken ct) => Task.CompletedTask;

        /// <summary>What the run loop would take on its next pass.</summary>
        public IReadOnlyList<QueuedInput> Drain() => TryDrainInputs(out var inputs) ? inputs : [];
    }

    private async Task QueueAsync(IMessage message)
    {
        var receipt = await _parent.TrySendAsync([message]);
        receipt.Should().NotBeNull(because: "a test whose input never reached the queue proves nothing");
    }

    private (SubAgentManager Manager, SubAgentToolProvider Provider) CreateManager(
        AgentCollaborationSetup? collaboration,
        SubAgentTemplate template
    )
    {
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate> { ["worker"] = template },
            MaxConcurrentSubAgents = 5,
        };

        var source = new MutableSubAgentTemplateSource(options.Templates);
        var manager = new SubAgentManager(
            parentAgent: _parent,
            parentContracts: [],
            parentHandlers: new Dictionary<string, ToolHandler>(),
            options: options,
            source: source,
            collaboration: collaboration
        );

        _managers.Add(manager);
        return (manager, new SubAgentToolProvider(manager, source));
    }

    private static AgentCollaborationSetup CreateRegisteredRoot()
    {
        var setup = AgentCollaborationSetup.CreateRoot(new AgentCollaborationOptions());
        _ = setup.Directory.TryRegister(
            setup.Context,
            setup.Name,
            AgentCollaborationStatuses.Running,
            new SilentEndpoint()
        );
        return setup;
    }

    private static async Task<string> SpawnAsync(SubAgentToolProvider provider, string name = "child")
    {
        var payload = await InvokeAsync(
            provider,
            "Agent",
            new
            {
                subagent_type = "worker",
                prompt = "work",
                role = "worker role",
                description = "Does a unit of work.",
                name,
                run_in_background = true,
            }
        );

        payload.IsError.Should().BeFalse(payload.Text);
        using var doc = JsonDocument.Parse(payload.Text);
        return doc.RootElement.GetProperty("agent_id").GetString()!;
    }

    /// <summary>The legacy spawn shape, which carries no collaboration identity fields.</summary>
    private static async Task<string> SpawnLegacyAsync(SubAgentToolProvider provider)
    {
        var payload = await InvokeAsync(
            provider,
            "Agent",
            new
            {
                subagent_type = "worker",
                prompt = "work",
                run_in_background = true,
            }
        );

        payload.IsError.Should().BeFalse(payload.Text);
        using var doc = JsonDocument.Parse(payload.Text);
        return doc.RootElement.GetProperty("agent_id").GetString()!;
    }

    private static async Task<ToolHandlerResultPayload> InvokeAsync(
        SubAgentToolProvider provider,
        string toolName,
        object args
    )
    {
        var handler = provider.GetFunctions().First(f => f.Contract.Name == toolName).Handler;
        var result = await handler(JsonSerializer.Serialize(args), new ToolCallContext(), CancellationToken.None);

        return result.Should().BeOfType<ToolHandlerResult.Resolved>().Subject.Payload;
    }

    private static string StatusOf(ToolHandlerResultPayload payload)
    {
        using var doc = JsonDocument.Parse(payload.Text);
        return doc.RootElement.GetProperty("status").GetString()!;
    }

    private static string Description(SubAgentToolProvider provider, string toolName) =>
        provider.GetFunctions().First(f => f.Contract.Name == toolName).Contract.Description!;

    /// <summary>A child that never finishes, so only the racer under test can end the wait.</summary>
    private static SubAgentTemplate BlockingTemplate() => TemplateOver(BlockingStream);

    /// <summary>A child that finishes the moment it is asked to.</summary>
    private static SubAgentTemplate FinishingTemplate() => TemplateOver(_ => FinishedStream());

    /// <summary>A child whose finishing moment the test chooses.</summary>
    private static SubAgentTemplate GatedTemplate(Task gate) => TemplateOver(ct => GatedStream(gate, ct));

    private static SubAgentTemplate TemplateOver(Func<CancellationToken, IAsyncEnumerable<IMessage>> stream) =>
        new()
        {
            SystemPrompt = "You are a worker.",
            Description = "Does work.",
            AgentFactory = () =>
            {
                var mock = new Mock<IStreamingAgent>();
                _ = mock.Setup(a =>
                        a.GenerateReplyStreamingAsync(
                            It.IsAny<IEnumerable<IMessage>>(),
                            It.IsAny<GenerateReplyOptions>(),
                            It.IsAny<CancellationToken>()
                        )
                    )
                    .Returns<IEnumerable<IMessage>, GenerateReplyOptions?, CancellationToken>(
                        (_, _, ct) => Task.FromResult(stream(ct))
                    );
                return mock.Object;
            },
        };

    private static async IAsyncEnumerable<IMessage> BlockingStream([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        yield break;
    }

    private static async IAsyncEnumerable<IMessage> FinishedStream()
    {
        await Task.Yield();
        yield return new TextMessage { Text = "done", Role = Role.Assistant };
    }

    private static async IAsyncEnumerable<IMessage> GatedStream(
        Task gate,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        await gate.WaitAsync(ct);
        yield return new TextMessage { Text = "done", Role = Role.Assistant };
    }

    /// <summary>A root endpoint that accepts delivery and keeps nothing; no test here reads it.</summary>
    private sealed class SilentEndpoint : IAgentWriteEndpoint
    {
        public ValueTask<AgentDeliveryOutcome> DeliverAsync(
            AgentMessage message,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult(new AgentDeliveryOutcome(AgentDeliveryDisposition.Delivered));
    }

    #endregion
}
