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
/// <para>
/// <b>What <c>DisposeAsync_DrainsAnAgentStillInItsPreLoopStartupWindow</c> does NOT prove.</b> It is
/// satisfied by EITHER half of the #506 change alone — publishing <c>_runTask</c> before startup (which
/// makes the agent's own <c>StopAsync</c> wait for the startup write) or the pool's run-task drain below
/// (which waits for it regardless). Green there is therefore not evidence for either half, and it must
/// not be cited as such; <c>DisposeAsync_DrainsTheRunTaskEvenWhenTheAgentsOwnStopDoesNot</c> is the one
/// that isolates the pool's drain, because its agent's stop provably does not do the waiting.
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
        _ = raced
            .Should()
            .NotBeSameAs(
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
        _ = raced
            .Should()
            .NotBeSameAs(
                dispose,
                "pool disposal must not return while an agent is still inside the pre-loop startup window, "
                    + "where StopAsync is a no-op because the run-loop task does not exist yet"
            );
        _ = agent.StartupWriteCompleted.Should().BeFalse();

        agent.ReleaseStartup();

        await dispose.WaitAsync(Generous);
        _ = agent
            .StartupWriteCompleted.Should()
            .BeTrue("the startup store write must have landed before disposal returned");
    }

    [Fact]
    public async Task DisposeAsync_DrainsTheRunTaskEvenWhenTheAgentsOwnStopDoesNot()
    {
        var store = new InMemoryConversationStore();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var written = 0;

        // A plain IMultiTurnAgent whose StopAsync returns Task.CompletedTask without touching the run
        // — the shape MultiTurnAgentBase is NOT, and the reason the pool cannot delegate this wait to
        // the agent. Nothing in IMultiTurnAgent obliges a stop to drain the run task, so the only
        // thing that can wait for the task the pool started is the pool.
        var agent = new FakeMultiTurnAgent("thread-lingering")
        {
            RunBehavior = async runCt =>
            {
                // Deliberately unused: the write below must survive the pool cancelling this token.
                _ = runCt;
                _ = entered.TrySetResult();

                // Uncancellable: a writer past its own cancellation check is what disposal must wait
                // out, not one it can cancel away.
                await gate.Task;

                await store.UpdateMetadataAsync(
                    "thread-lingering",
                    existing =>
                        existing
                        ?? new ThreadMetadata
                        {
                            ThreadId = "thread-lingering",
                            LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        },
                    CancellationToken.None
                );

                _ = Interlocked.Exchange(ref written, 1);
            },
        };

        var pool = new MultiTurnAgentPool(
            _ => new MultiTurnAgentPool.AgentCreationResult(agent),
            providerRegistry: null,
            conversationStore: null,
            NullLogger<MultiTurnAgentPool>.Instance
        );

        _ = pool.GetOrCreateAgent("thread-lingering", Mode);

        await entered.Task.WaitAsync(Generous);

        var dispose = pool.DisposeAsync().AsTask();

        var raced = await Task.WhenAny(dispose, Task.Delay(BlockedObservation));
        _ = raced
            .Should()
            .NotBeSameAs(
                dispose,
                "the pool must drain the run task it started itself, rather than assume the agent's own StopAsync did"
            );

        gate.TrySetResult();

        await dispose.WaitAsync(Generous);
        _ = (Volatile.Read(ref written) != 0)
            .Should()
            .BeTrue("the run task's store write must have landed before disposal returned");
    }

    [Fact]
    public async Task DisposeAsync_StillDrainsTheRunTask_WhenTheAgentsOwnDisposeThrows()
    {
        var store = new InMemoryConversationStore();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var written = 0;

        var agent = new FakeMultiTurnAgent("thread-throwing-dispose")
        {
            // The whole point of the fixture. A throw here lands between the guarded StopAsync and the
            // drain, so an unguarded await would skip the drain, the owned resources, and the CTS.
            ThrowOnDispose = true,
            RunBehavior = async runCt =>
            {
                _ = runCt;
                _ = entered.TrySetResult();

                // Uncancellable, for the same reason as the sibling tests: what disposal must survive
                // is a writer already past its own cancellation check.
                await gate.Task;

                await store.UpdateMetadataAsync(
                    "thread-throwing-dispose",
                    existing =>
                        existing
                        ?? new ThreadMetadata
                        {
                            ThreadId = "thread-throwing-dispose",
                            LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        },
                    CancellationToken.None
                );

                _ = Interlocked.Exchange(ref written, 1);
            },
        };

        var owned = new RecordingAsyncDisposable();

        var pool = new MultiTurnAgentPool(
            _ => new MultiTurnAgentPool.AgentCreationResult(agent, [owned]),
            providerRegistry: null,
            conversationStore: null,
            NullLogger<MultiTurnAgentPool>.Instance
        );

        _ = pool.GetOrCreateAgent("thread-throwing-dispose", Mode);

        await entered.Task.WaitAsync(Generous);

        var dispose = pool.DisposeAsync().AsTask();

        // The pool's own DisposeAsync catches per-entry failures and logs them, so the regression this
        // test guards is SILENT at the caller: nothing throws either way. Only the effects separate
        // them, which is why every assertion below is about what did or did not happen, never about
        // an exception.
        var raced = await Task.WhenAny(dispose, Task.Delay(BlockedObservation));
        _ = raced
            .Should()
            .NotBeSameAs(
                dispose,
                "a throw from the agent's own DisposeAsync must not carry disposal past the run-task drain"
            );

        gate.TrySetResult();

        await dispose.WaitAsync(Generous);

        _ = (Volatile.Read(ref written) != 0)
            .Should()
            .BeTrue(
                "the run task's store write must have landed before disposal returned, even though the "
                    + "agent's own DisposeAsync threw on the way to the drain"
            );
        _ = owned
            .Disposed.Should()
            .BeTrue(
                "owned resources are torn down AFTER the agent, so a throw from the agent's dispose must "
                    + "not be what leaks them"
            );
    }

    /// <summary>Records whether it was disposed. Stands in for a pooled agent's owned MCP clients.</summary>
    private sealed class RecordingAsyncDisposable : IAsyncDisposable
    {
        private int _disposed;

        public bool Disposed => Volatile.Read(ref _disposed) != 0;

        public ValueTask DisposeAsync()
        {
            _ = Interlocked.Exchange(ref _disposed, 1);
            return ValueTask.CompletedTask;
        }
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
            CancellationToken ct = default
        )
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

        public Task DeleteThreadAsync(string threadId, CancellationToken ct = default) =>
            _inner.DeleteThreadAsync(threadId, ct);

        public Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
            int limit = 50,
            int offset = 0,
            ConversationListOptions? options = null,
            CancellationToken ct = default
        ) => _inner.ListThreadsAsync(limit, offset, options, ct);

        public Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
            ConversationListScope scope,
            int limit = 50,
            int offset = 0,
            ConversationListOptions? options = null,
            CancellationToken ct = default
        ) => _inner.ListThreadsAsync(scope, limit, offset, options, ct);
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
                existing =>
                    existing
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
