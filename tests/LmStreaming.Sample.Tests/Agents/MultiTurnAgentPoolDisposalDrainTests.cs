using LmStreaming.Sample.Tests.TestDoubles;

namespace LmStreaming.Sample.Tests.Agents;

/// <summary>
/// Pins <see cref="MultiTurnAgentPool"/> disposal as a real durability boundary (#506): when
/// <c>DisposeAsync</c> returns, every conversation-store write the pool started has finished.
/// </summary>
/// <remarks>
/// Two independent escape paths are covered, because they escape through different fields and a
/// single test would leave whichever one it did not travel unproven:
/// <list type="bullet">
///   <item>
///     the fire-and-forget <c>PersistThreadBindingsIfNeededAsync</c> discarded with <c>_ =</c> on the
///     creation path — nothing held a reference to its task at all, so nothing could wait for it; and
///   </item>
///   <item>
///     <c>AgentEntry.RunTask</c> — the pool DID hold this one, and still never awaited it, so a run
///     still inside its pre-loop startup window (where <c>StopAsync</c> is a no-op because the loop
///     task does not exist yet) outlived the disposal that was supposed to have stopped it.
///   </item>
/// </list>
/// <para>
/// Every wait here is <c>WaitAsync</c> with a timeout rather than a poll: a helper that returns
/// quietly when its deadline passes would make every assertion below vacuous — the test would report
/// green for "the write never started" exactly as loudly as for "the write finished in time".
/// </para>
/// </remarks>
[Collection("EnvironmentVariables")]
public class MultiTurnAgentPoolDisposalDrainTests
{
    private static readonly AgentProfile Mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;

    /// <summary>How long a blocked-forever wait is given before the test gives up and FAILS.</summary>
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long disposal is watched for while the store write it must wait for is held open. Only an
    /// upper bound on the bug: today's disposal returns in single-digit milliseconds because it never
    /// looks at the in-flight write, so this window is three orders of magnitude larger than the
    /// signal it has to separate.
    /// </summary>
    private static readonly TimeSpan BlockedObservation = TimeSpan.FromMilliseconds(500);

    [Fact]
    public async Task DisposeAsync_DoesNotReturnWhileTheBindingPersistIsStillWriting()
    {
        var store = new GatedMetadataStore();
        var pool = new MultiTurnAgentPool(
            context => new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(context.ThreadId)),
            providerRegistry: null,
            conversationStore: store,
            NullLogger<MultiTurnAgentPool>.Instance
        );

        // First creation is what fires the discarded PersistThreadBindingsIfNeededAsync.
        _ = pool.GetOrCreateAgent("thread-persist", Mode);

        await store.UpdateEntered.Task.WaitAsync(Generous);

        var dispose = pool.DisposeAsync().AsTask();

        // The write is provably in flight (UpdateEntered fired) and provably not finished (the gate is
        // still closed), so a disposal that completes here has returned over a live store write.
        var raced = await Task.WhenAny(dispose, Task.Delay(BlockedObservation));
        _ = raced.Should().NotBeSameAs(
            dispose,
            "pool disposal must not return while a conversation-store metadata write it started is still in flight"
        );
        _ = store.UpdateCompleted.Should().BeFalse();

        store.ReleaseUpdate();

        await dispose.WaitAsync(Generous);
        _ = store.UpdateCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task DisposeAsync_DrainsAnAgentStillInItsPreLoopStartupWindow()
    {
        var store = new InMemoryConversationStore();
        var agent = new PreLoopBlockingAgent("thread-startup", store);

        // conversationStore: null so the pool's OWN binding persist cannot be what holds disposal
        // open. The only writer in this test is the agent's startup, reached through RunTask.
        var pool = new MultiTurnAgentPool(
            _ => new MultiTurnAgentPool.AgentCreationResult(agent),
            providerRegistry: null,
            conversationStore: null,
            NullLogger<MultiTurnAgentPool>.Instance
        );

        _ = pool.GetOrCreateAgent("thread-startup", Mode);

        await agent.StartupEntered.Task.WaitAsync(Generous);

        var dispose = pool.DisposeAsync().AsTask();

        var raced = await Task.WhenAny(dispose, Task.Delay(BlockedObservation));
        _ = raced.Should().NotBeSameAs(
            dispose,
            "pool disposal must not return while an agent is still inside the pre-loop startup window, "
                + "where StopAsync is a no-op because the run-loop task does not exist yet"
        );
        _ = agent.StartupWriteCompleted.Should().BeFalse();

        agent.ReleaseStartup();

        await dispose.WaitAsync(Generous);
        _ = agent.StartupWriteCompleted.Should().BeTrue(
            "the startup store write must have landed before disposal returned"
        );
    }

    /// <summary>
    /// Forwards everything to a real in-memory store, but holds <c>UpdateMetadataAsync</c> open until
    /// released — the single call the pool's discarded binding persist makes.
    /// </summary>
    private sealed class GatedMetadataStore : IConversationStore
    {
        private readonly InMemoryConversationStore _inner = new();
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _updateCompleted;

        /// <summary>Fires once <c>UpdateMetadataAsync</c> has been entered and is parked on the gate.</summary>
        public TaskCompletionSource UpdateEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Whether the gated write has run to completion.</summary>
        public bool UpdateCompleted => Volatile.Read(ref _updateCompleted) != 0;

        public void ReleaseUpdate() => _gate.TrySetResult();

        public async Task UpdateMetadataAsync(
            string threadId,
            Func<ThreadMetadata?, ThreadMetadata> update,
            CancellationToken ct = default)
        {
            _ = UpdateEntered.TrySetResult();

            // Deliberately NOT cancellable. A write already past its own cancellation check is exactly
            // the case disposal has to wait out; a gate that cancelled would let the fix be "cancel it"
            // rather than "wait for it", and lose the update this issue is about.
            await _gate.Task;

            await _inner.UpdateMetadataAsync(threadId, update, ct);
            _ = Interlocked.Exchange(ref _updateCompleted, 1);
        }

        public Task AppendMessagesAsync(
            string threadId,
            IReadOnlyList<PersistedMessage> messages,
            CancellationToken ct = default) => _inner.AppendMessagesAsync(threadId, messages, ct);

        public Task<IReadOnlyList<PersistedMessage>> LoadMessagesAsync(
            string threadId,
            CancellationToken ct = default) => _inner.LoadMessagesAsync(threadId, ct);

        public Task ReplaceMessageAsync(
            string threadId,
            PersistedMessage replacement,
            CancellationToken ct = default) => _inner.ReplaceMessageAsync(threadId, replacement, ct);

        public Task SaveMetadataAsync(
            string threadId,
            ThreadMetadata metadata,
            CancellationToken ct = default) => _inner.SaveMetadataAsync(threadId, metadata, ct);

        public Task<ThreadMetadata?> LoadMetadataAsync(string threadId, CancellationToken ct = default) =>
            _inner.LoadMetadataAsync(threadId, ct);

        public Task DeleteThreadAsync(string threadId, CancellationToken ct = default) =>
            _inner.DeleteThreadAsync(threadId, ct);

        public Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
            int limit = 50,
            int offset = 0,
            ConversationListOptions? options = null,
            CancellationToken ct = default) => _inner.ListThreadsAsync(limit, offset, options, ct);

        public Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
            ConversationListScope scope,
            int limit = 50,
            int offset = 0,
            ConversationListOptions? options = null,
            CancellationToken ct = default) => _inner.ListThreadsAsync(scope, limit, offset, options, ct);
    }

    /// <summary>
    /// A real <see cref="MultiTurnAgentBase"/> parked inside <c>OnBeforeRunAsync</c> — the last step of
    /// the pre-loop startup window, run before <c>RunAsync</c> assigns the loop task that
    /// <c>StopAsync</c> waits on.
    /// </summary>
    private sealed class PreLoopBlockingAgent(string threadId, IConversationStore store)
        : MultiTurnAgentBase(threadId, store: store)
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _startupWriteCompleted;

        /// <summary>Fires once startup has been entered and is parked on the gate.</summary>
        public TaskCompletionSource StartupEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Whether the startup store write has run to completion.</summary>
        public bool StartupWriteCompleted => Volatile.Read(ref _startupWriteCompleted) != 0;

        public void ReleaseStartup() => _gate.TrySetResult();

        protected override async Task OnBeforeRunAsync()
        {
            _ = StartupEntered.TrySetResult();

            // Uncancellable for the same reason as the store gate above: the point is a writer that
            // disposal must WAIT for, not one it can cancel out from under.
            await _gate.Task;

            await Store!.UpdateMetadataAsync(
                ThreadId,
                existing => existing
                    ?? new ThreadMetadata
                    {
                        ThreadId = ThreadId,
                        LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    },
                CancellationToken.None
            );

            _ = Interlocked.Exchange(ref _startupWriteCompleted, 1);
        }

        protected override Task RunLoopAsync(CancellationToken ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }
}
