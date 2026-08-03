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
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests.Collaboration;

/// <summary>
/// What happens to a claimed wait interrupt when the wait that claimed it is cancelled instead of
/// answered.
/// </summary>
/// <remarks>
/// <para>
/// A question grants exactly one wait interrupt, so the claim is a resource: whoever takes it and does
/// not surface it must give it back. Every abandonment path is covered elsewhere except the one that
/// leaves by throwing — and that is the path where losing the claim is permanent, because the question
/// stays open and no later wait can ever be woken by it again.
/// </para>
/// <para>
/// The window is a handful of thread-pool hops wide, so the test drives the continuations itself
/// through <see cref="PumpingSynchronizationContext"/> rather than trying to hit it by timing.
/// </para>
/// </remarks>
public class SubAgentWaitInterruptCancellationTests : IAsyncLifetime
{
    private readonly Mock<IMultiTurnAgent> _parentMock = new();
    private readonly List<SubAgentManager> _managers = [];

    public Task InitializeAsync()
    {
        _ = _parentMock
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
        foreach (var manager in _managers)
        {
            await manager.DisposeAsync();
        }
    }

    [Fact]
    public async Task WaitForAgents_CancelledAfterClaimingAQuestion_GivesTheInterruptBack()
    {
        var root = CreateRegisteredRoot();
        var (_, provider) = CreateManager(root);
        var handler = provider
            .GetFunctions()
            .First(f => f.Contract.Name == "WaitForAgents")
            .Handler;
        var agentId = await SpawnAndResolveIdAsync(provider);
        var (_, asker) = RegisterPeer(root, "asker");

        using var cts = new CancellationTokenSource();
        var pump = new PumpingSynchronizationContext();
        var previous = SynchronizationContext.Current;
        Task<ToolHandlerResult> wait;

        // Installed only for the call itself, so every continuation inside the handler lands in a queue
        // this test decides when to run. Nothing between here and the pump can advance the handler.
        SynchronizationContext.SetSynchronizationContext(pump);
        try
        {
            wait = handler(
                JsonSerializer.Serialize(new { agent_ids = agentId }),
                new ToolCallContext(),
                cts.Token
            );
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        wait.IsCompleted.Should().BeFalse("the child blocks, so the wait is parked on its racers");

        // Admission raises the ledger notice on this thread, so the handler's watcher claims the
        // interrupt here and now; only its resumption is deferred to the pump.
        var question = root
            .Bundle.TrySend(asker.AgentId, root.AgentId, AgentMessageType.Question)
            .MessageId!;
        root.Bundle.Ledger.Find(question)!
            .WaitInterruptClaimed.Should()
            .BeTrue("the parked wait claimed the question it is about to report");

        cts.Cancel();
        pump.RunUntil(wait, TimeSpan.FromSeconds(10));

        await FluentActions
            .Awaiting(() => wait)
            .Should()
            .ThrowAsync<OperationCanceledException>("the caller asked for the wait to stop");

        // The caller received an exception rather than the question, so the interrupt was never
        // surfaced. Left claimed, this one question would silently disarm every future wait.
        var entry = root.Bundle.Ledger.Find(question)!;
        entry.IsClosed.Should().BeFalse("interrupting is not answering");
        entry.WaitInterruptClaimed.Should().BeFalse("an unreported claim has to be given back");

        // The claim is only worth anything if it can still be spent, so prove it behaviourally too.
        var second = await InvokeAsync(
            provider,
            "WaitForAgents",
            new { agent_ids = agentId, timeout_seconds = 5 }
        );

        using var doc = JsonDocument.Parse(second.Text);
        doc.RootElement.GetProperty("status").GetString().Should().Be("question_received");
        doc.RootElement.GetProperty("question")
            .GetProperty("message_id")
            .GetString()
            .Should()
            .Be(question);
    }

    /// <summary>
    /// A synchronization context that queues continuations instead of running them, so a test can
    /// decide exactly where an async method is suspended when something else happens.
    /// </summary>
    private sealed class PumpingSynchronizationContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_queue)
            {
                _queue.Enqueue((d, state));
            }
        }

        public override void Send(SendOrPostCallback d, object? state) => d(state);

        /// <summary>Runs queued continuations until <paramref name="task"/> settles.</summary>
        /// <remarks>
        /// Work can still be posted from elsewhere (a cancelled racer completing on the thread pool),
        /// so an empty queue is a reason to wait rather than to stop.
        /// </remarks>
        public void RunUntil(Task task, TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (!task.IsCompleted)
            {
                if (TryDequeue(out var work))
                {
                    work.Callback(work.State);
                    continue;
                }

                if (DateTimeOffset.UtcNow > deadline)
                {
                    throw new TimeoutException("The pumped call did not settle in time.");
                }

                Thread.Sleep(1);
            }
        }

        private bool TryDequeue(out (SendOrPostCallback Callback, object? State) work)
        {
            lock (_queue)
            {
                return _queue.TryDequeue(out work);
            }
        }
    }

    private static AgentCollaborationSetup CreateRegisteredRoot()
    {
        var setup = AgentCollaborationSetup.CreateRoot(new AgentCollaborationOptions());
        _ = setup.Directory.TryRegister(
            setup.Context,
            setup.Name,
            AgentCollaborationStatuses.Running,
            new AcceptingEndpoint()
        );
        return setup;
    }

    private static (AcceptingEndpoint Endpoint, AgentCollaborationSetup Setup) RegisterPeer(
        AgentCollaborationSetup root,
        string name
    )
    {
        var context = root.Context.CreateChild(
            $"agent-{name}",
            AgentKind.SubAgent,
            $"{name} role",
            $"Stands in for {name}."
        );
        var endpoint = new AcceptingEndpoint();

        _ = root.Directory.TryAcquireCapacity(context.AgentId);
        root.Directory.TryRegister(context, name, AgentCollaborationStatuses.Running, endpoint)
            .Succeeded.Should()
            .BeTrue();

        return (endpoint, root.ForChild(context, name));
    }

    private (SubAgentManager Manager, SubAgentToolProvider Provider) CreateManager(
        AgentCollaborationSetup collaboration
    )
    {
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["worker"] = new()
                {
                    SystemPrompt = "You are a worker.",
                    Description = "Does work.",
                    AgentFactory = () =>
                    {
                        var mock = new Mock<IStreamingAgent>();
                        _ = mock
                            .Setup(a =>
                                a.GenerateReplyStreamingAsync(
                                    It.IsAny<IEnumerable<IMessage>>(),
                                    It.IsAny<GenerateReplyOptions>(),
                                    It.IsAny<CancellationToken>()
                                )
                            )
                            .Returns<IEnumerable<IMessage>, GenerateReplyOptions?, CancellationToken>(
                                (_, _, ct) => Task.FromResult(BlockingStream(ct))
                            );
                        return mock.Object;
                    },
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
            source: source,
            collaboration: collaboration
        );

        _managers.Add(manager);
        return (manager, new SubAgentToolProvider(manager, source));
    }

    private static async Task<string> SpawnAndResolveIdAsync(SubAgentToolProvider provider)
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
                name = "child",
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
        var result = await handler(
            JsonSerializer.Serialize(args),
            new ToolCallContext(),
            CancellationToken.None
        );

        return result.Should().BeOfType<ToolHandlerResult.Resolved>().Subject.Payload;
    }

    private static async IAsyncEnumerable<IMessage> BlockingStream(
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        yield break;
    }

    /// <summary>A stand-in owner that accepts anything handed to it.</summary>
    private sealed class AcceptingEndpoint : IAgentWriteEndpoint
    {
        public ValueTask<AgentDeliveryOutcome> DeliverAsync(
            AgentMessage message,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult(new AgentDeliveryOutcome(AgentDeliveryDisposition.Delivered));
    }
}
