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
///     manager's lifecycle path. The finished child's loop is still alive and still accepts input, but
///     its owned provider was disposed at completion — so a direct <c>TrySendAsync</c> is accepted and
///     then starts a run that dies on its first provider call (78 doomed runs in the field, all
///     <see cref="ObjectDisposedException" /> on the provider client). The manager path restarts the
///     child with a fresh provider instead.
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
    public async Task DeliverAsync_ToFinishedSubAgentWithDisposedProvider_RestartsWithFreshProviderInsteadOfDoomedRun()
    {
        // Arrange: a template whose provider the child OWNS, so completion disposes it (the field shape:
        // a tier/characteristics-routed provider client per run).
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

            // The run finished AND its owned provider is gone: the exact state the field runs were in.
            await Wait.UntilAsync(
                () =>
                {
                    lock (providers)
                    {
                        return providers.Count == 1
                            && providers[0].Disposed
                            && manager.Peek(agentId).Contains("\"completed\"", StringComparison.Ordinal);
                    }
                },
                "the child completed its first run and its owned provider was disposed",
                TimeSpan.FromSeconds(10)
            );
            var disposedProvider = providers[0];
            disposedProvider.Calls.Should().Be(1, "the first run reached its provider exactly once");

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

            // Non-vacuity: wait until the notification reached SOME provider — the disposed one (the
            // doomed run this issue is about) or a fresh one (the restart). A delivery nobody ran would
            // otherwise pass the "no call after dispose" assertion for free.
            await Wait.UntilAsync(
                () =>
                {
                    lock (providers)
                    {
                        return disposedProvider.CallsAfterDispose > 0 || providers.Skip(1).Any(p => p.Calls > 0);
                    }
                },
                "the notification was run through a provider",
                TimeSpan.FromSeconds(10),
                observed: () =>
                    $"delivered={delivered}, providers={providers.Count}, callsAfterDispose={disposedProvider.CallsAfterDispose}"
            );

            // Assert: no run ever touched the disposed provider; the child was restarted on a fresh one
            // and that fresh run carried the typed notification.
            delivered.Should().BeTrue("the manager path admits a finished child by restarting it");
            disposedProvider
                .CallsAfterDispose.Should()
                .Be(0, "a notification must never start a run against a provider disposed at completion");
            providers.Should().HaveCount(2, "the restart built a fresh owned provider");
            providers[1].Calls.Should().Be(1);
            providers[1]
                .Requests.Single()
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
