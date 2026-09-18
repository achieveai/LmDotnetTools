using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests.SubAgents;

/// <summary>
/// A sub-agent that is still <see cref="SubAgentStatus.Running"/> when the host stops never reaches a
/// terminal transition of its own, so nothing ever stamps a terminal status onto its persisted
/// metadata: every later reader — including one in a brand-new process — keeps seeing <c>running</c>
/// for a child that stopped existing when the process did. (Observed in production: four children on a
/// published instance had read <c>running</c> for 7–47 hours across restarts, with no terminal
/// timestamp, under three parent conversations and two providers.)
///
/// <para>
/// <see cref="SubAgentManager.DisposeAsync"/> is the one place that knows both facts at once — that the
/// child is still running, and that it is about to stop being anything at all — so it marks such a
/// child <see cref="SubAgentStatus.Stopped"/> and pushes that through the child's own store before the
/// loop and the store are torn down. The push is BOUNDED for the same reason every other await on that
/// path is: a wedged store must degrade host shutdown into a logged warning, never hang it.
/// </para>
/// </summary>
public class SubAgentShutdownTerminalStatusTests
{
    [Fact]
    public async Task DisposeAsync_MarksAStillRunningChildStopped_AndPushesItThroughTheChildStore()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new DescribingMetadataStore();
        await using var manager = CreateManager(BlockingProvider(entered), store);

        _ = await manager.SpawnAsync("test-agent", "a task that never finishes", runInBackground: true);
        await entered.Task.WaitAsync(Wait.DefaultTimeout);

        await manager.DisposeAsync();

        // The snapshot the child's store saw AT THE MOMENT OF THE PUSH — the same live-roster describe
        // the sample's provenance decorator stamps from, so this is exactly what would land on disk.
        var described = store
            .LastDescribed.Should()
            .NotBeNull(
                "a child still Running at shutdown must have its terminal status pushed through its own "
                    + "store, or its persisted metadata says `running` forever"
            )
            .And.Subject.Should()
            .BeOfType<SubAgentSnapshot>()
            .Subject;

        described.Status.Should().Be(SubAgentStatus.Stopped);
        described.FailureCode.Should().Be(SubAgentFailureCodes.HostShutdown);
        described.TerminalAtUtc.Should().NotBeNull("a terminal status without its instant cannot be aged");
    }

    [Fact]
    public async Task DisposeAsync_CompletesWithinItsCeiling_WhenTheTerminalStatusPushWedges()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new DescribingMetadataStore();
        var manager = CreateManager(BlockingProvider(entered), store);
        manager.TestPerAgentBackgroundTaskDisposeCeiling = TimeSpan.FromMilliseconds(200);

        try
        {
            _ = await manager.SpawnAsync("test-agent", "a task that never finishes", runInBackground: true);
            await entered.Task.WaitAsync(Wait.DefaultTimeout);

            // From here on the store never answers. Armed AFTER the run's own writes so this wedges the
            // shutdown push specifically, not some unrelated write the child made while running.
            store.WedgeUpdatesUntil(release.Task);

            // The whole point of the bound: a store that never answers must not hold the process open.
            await manager.DisposeAsync().AsTask().WaitAsync(Wait.DefaultTimeout);
        }
        finally
        {
            _ = release.TrySetResult();
        }

        // Both halves. The first says the push was actually attempted (a manager that never pushes at
        // shutdown would "complete in time" vacuously); the second is the completion above.
        store
            .WedgedUpdateCount.Should()
            .BeGreaterThan(
                0,
                "the bound is only meaningful if disposal really does push the terminal status through the store"
            );
    }

    #region Helpers

    private static SubAgentManager CreateManager(IStreamingAgent provider, IConversationStore store)
    {
        var parent = new Mock<IMultiTurnAgent>();
        _ = parent
            .Setup(p =>
                p.SendAsync(
                    It.IsAny<List<IMessage>>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new SendReceipt("receipt-1", null, DateTimeOffset.UtcNow));

        var options = new SubAgentOptions
        {
            MaxConcurrentSubAgents = 2,
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["test-agent"] = new()
                {
                    Name = "test-agent",
                    SystemPrompt = "You are a test agent.",
                    AgentFactory = () => provider,
                },
            },
            // The provenance-aware shape (#275), because it is the one that hands the store a LIVE
            // describe over the manager's roster — the callback the sample's decorator builds its
            // stamp from. Asserting on what THAT returns is asserting on what reaches disk.
            ProvenanceAwareConversationStoreFactory = (_, _, describeChild) =>
                store is DescribingMetadataStore describing ? describing.Bind(describeChild) : store,
        };

        return new SubAgentManager(
            parent.Object,
            [],
            new Dictionary<string, ToolHandler>(),
            options,
            new MutableSubAgentTemplateSource(options.Templates)
        );
    }

    /// <summary>A provider whose stream never yields, so the spawned child stays Running.</summary>
    private static IStreamingAgent BlockingProvider(TaskCompletionSource entered)
    {
        var provider = new Mock<IStreamingAgent>();
        _ = provider
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(
                (IEnumerable<IMessage> _, GenerateReplyOptions? _, CancellationToken ct) =>
                {
                    _ = entered.TrySetResult();
                    return Task.FromResult(BlockingStream(ct));
                }
            );
        return provider.Object;
    }

    private static async IAsyncEnumerable<IMessage> BlockingStream(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct
    )
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        yield break;
    }

    /// <summary>
    /// In-memory store that records the live snapshot its provenance describe resolves to whenever the
    /// manager pushes metadata through it, and can optionally park that push.
    /// </summary>
    private sealed class DescribingMetadataStore : IConversationStore
    {
        private readonly InMemoryConversationStore _inner = new();
        private Func<string, SubAgentSnapshot?>? _describe;
        private Task? _wedge;
        private int _wedgedUpdates;

        /// <summary>The snapshot the describe resolved to on the most recent metadata update.</summary>
        public SubAgentSnapshot? LastDescribed { get; private set; }

        /// <summary>How many metadata updates have parked since <see cref="WedgeUpdatesUntil"/>.</summary>
        public int WedgedUpdateCount => Volatile.Read(ref _wedgedUpdates);

        /// <summary>From now on, every metadata update parks on <paramref name="release"/>.</summary>
        public void WedgeUpdatesUntil(Task release) => _wedge = release;

        public DescribingMetadataStore Bind(Func<string, SubAgentSnapshot?> describe)
        {
            _describe = describe;
            return this;
        }

        public Task AppendMessagesAsync(
            string threadId,
            IReadOnlyList<PersistedMessage> messages,
            CancellationToken ct = default
        ) => _inner.AppendMessagesAsync(threadId, messages, ct);

        public Task<IReadOnlyList<PersistedMessage>> LoadMessagesAsync(
            string threadId,
            CancellationToken ct = default
        ) => _inner.LoadMessagesAsync(threadId, ct);

        public Task ReplaceMessageAsync(
            string threadId,
            PersistedMessage replacement,
            CancellationToken ct = default
        ) => _inner.ReplaceMessageAsync(threadId, replacement, ct);

        public Task SaveMetadataAsync(string threadId, ThreadMetadata metadata, CancellationToken ct = default) =>
            _inner.SaveMetadataAsync(threadId, metadata, ct);

        public Task<ThreadMetadata?> LoadMetadataAsync(string threadId, CancellationToken ct = default) =>
            _inner.LoadMetadataAsync(threadId, ct);

        public async Task UpdateMetadataAsync(
            string threadId,
            Func<ThreadMetadata?, ThreadMetadata> update,
            CancellationToken ct = default
        )
        {
            LastDescribed = _describe?.Invoke(threadId) ?? LastDescribed;
            if (_wedge is { } wedge)
            {
                _ = Interlocked.Increment(ref _wedgedUpdates);
                await wedge.WaitAsync(ct);
            }

            await _inner.UpdateMetadataAsync(threadId, update, ct);
        }

        public Task DeleteThreadAsync(string threadId, CancellationToken ct = default) =>
            _inner.DeleteThreadAsync(threadId, ct);

        public Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(int limit = 50, CancellationToken ct = default) =>
            _inner.ListThreadsAsync(limit, 0, null, ct);

        public Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
            int limit,
            int offset,
            ConversationListOptions? options,
            CancellationToken ct = default
        ) => _inner.ListThreadsAsync(limit, offset, options, ct);
    }

    #endregion
}
