using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils;
using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
///     #690: a todo nudge/digest addressed to a sub-agent whose run has FINISHED must go through the
///     manager's lifecycle path rather than a direct <c>TrySendAsync</c>, which bypasses the admission
///     that reactivates an idle agent. Provider ownership follows the LOOP, not an individual run, so a
///     finished child keeps its owned provider and the follow-up executes on that same live pipeline —
///     the provider is disposed only when the runtime is torn down. (Historically completion disposed
///     the owned provider, which is what produced the field's doomed runs: a direct send was accepted
///     and then died on its first provider call with <see cref="ObjectDisposedException" />.)
/// </summary>
public sealed class TodoNotificationDeliveryTests
{
    /// <summary>
    ///     A provider that behaves like a disposed <c>HttpClient</c>-backed client after
    ///     <see cref="IAsyncDisposable.DisposeAsync" />: every call after disposal throws
    ///     <see cref="ObjectDisposedException" /> and is counted, which is exactly the doomed-run shape.
    /// </summary>
    private sealed class DisposeAwareProvider
    {
        private int _calls;
        private int _callsAfterDispose;
        private int _disposed;

        public Mock<IStreamingAgent> Mock { get; } = new();

        public List<IReadOnlyList<IMessage>> Requests { get; } = [];

        public int Calls => Volatile.Read(ref _calls);

        public int CallsAfterDispose => Volatile.Read(ref _callsAfterDispose);

        public bool Disposed => Volatile.Read(ref _disposed) == 1;

        public DisposeAwareProvider()
        {
            Mock.Setup(a =>
                    a.GenerateReplyStreamingAsync(
                        It.IsAny<IEnumerable<IMessage>>(),
                        It.IsAny<GenerateReplyOptions>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                    (messages, _, _) =>
                    {
                        if (Disposed)
                        {
                            _ = Interlocked.Increment(ref _callsAfterDispose);
                            throw new ObjectDisposedException("HttpClient");
                        }

                        _ = Interlocked.Increment(ref _calls);
                        lock (Requests)
                        {
                            Requests.Add([.. messages]);
                        }

                        return Task.FromResult(Reply());
                    }
                );
            Mock.As<IAsyncDisposable>()
                .Setup(d => d.DisposeAsync())
                .Returns(() =>
                {
                    Volatile.Write(ref _disposed, 1);
                    return ValueTask.CompletedTask;
                });
        }

        private static async IAsyncEnumerable<IMessage> Reply([EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            yield return new TextMessage { Text = "done", Role = Role.Assistant };
            await Task.Yield();
        }
    }

    [Fact]
    public async Task DeliverAsync_ToFinishedSubAgent_ReusesLiveOwnedProviderAndDeliversNotification()
    {
        // An owned provider belongs to the reusable runtime, not to an individual completed run.
        var providers = new List<DisposeAwareProvider>();
        var template = new SubAgentTemplate
        {
            Name = "worker",
            SystemPrompt = "You are a worker.",
            AgentFactory = () => throw new InvalidOperationException("the characteristics factory owns the provider"),
            CharacteristicsAgentFactory = _ =>
            {
                var provider = new DisposeAwareProvider();
                lock (providers)
                {
                    providers.Add(provider);
                }

                return new SubAgentProviderAgent(provider.Mock.Object, ImmutableDictionary<string, object?>.Empty)
                {
                    OwnsAgent = true,
                };
            },
        };
        var templates = new Dictionary<string, SubAgentTemplate> { ["worker"] = template };

        var root = new Mock<IMultiTurnAgent>();
        root.Setup(p =>
                p.SendAsync(
                    It.IsAny<List<IMessage>>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new SendReceipt("receipt-1", null, DateTimeOffset.UtcNow));

        var manager = new SubAgentManager(
            parentAgent: root.Object,
            parentContracts: [],
            parentHandlers: new Dictionary<string, ToolHandler>(),
            options: new SubAgentOptions { Templates = templates, MaxConcurrentSubAgents = 2 },
            source: new MutableSubAgentTemplateSource(templates)
        );

        try
        {
            var spawnJson = await manager.SpawnAsync("worker", "first task", name: "alpha", runInBackground: true);
            var agentId = JsonDocument.Parse(spawnJson).RootElement.GetProperty("agent_id").GetString()!;

            // Wait for actual completion, without requiring disposal of the reusable provider.
            await Wait.UntilAsync(
                () =>
                {
                    lock (providers)
                    {
                        return providers.Count == 1
                            && manager.Peek(agentId).Contains("\"completed\"", StringComparison.Ordinal);
                    }
                },
                "the child completed its first run",
                TimeSpan.FromSeconds(10)
            );
            var originalProvider = providers[0];
            originalProvider.Calls.Should().Be(1, "the first run reached its provider exactly once");

            // Act: the board talks back to the finished child, the way Program.cs's nudge/digest do.
            var nudge = NotifyMessage.Create(
                NotifyKinds.TodoNudge,
                detail: "T1 is still assigned to you",
                label: "alpha"
            );
            var delivered = await TodoNotificationDelivery.DeliverAsync(
                root.Object,
                manager,
                "alpha",
                nudge,
                CancellationToken.None
            );

            // Non-vacuity: wait until a provider actually ran the notification. A delivery nobody
            // executed would otherwise pass the no-call-after-dispose assertion for free.
            await Wait.UntilAsync(
                () =>
                {
                    lock (providers)
                    {
                        return originalProvider.Calls > 1 || providers.Skip(1).Any(p => p.Calls > 0);
                    }
                },
                "the notification was run through a provider",
                TimeSpan.FromSeconds(10),
                observed: () =>
                    $"delivered={delivered}, providers={providers.Count}, callsAfterDispose={originalProvider.CallsAfterDispose}"
            );

            // The same live provider handles the follow-up, including its typed notification.
            delivered.Should().BeTrue("the manager admits a follow-up to a finished child");
            originalProvider.CallsAfterDispose.Should().Be(0);
            originalProvider.Disposed.Should().BeFalse();
            providers.Should().ContainSingle("completion retains the reusable owned provider");
            originalProvider.Calls.Should().Be(2);
            originalProvider
                .Requests.Last()
                .OfType<NotifyMessage>()
                .Should()
                .ContainSingle(
                    m => m.NotifyKind == NotifyKinds.TodoNudge,
                    "the notification reaches the child as itself"
                );
        }
        finally
        {
            await Wait.ForTeardownAsync(manager, "the sub-agent manager under test");
        }
    }
}
