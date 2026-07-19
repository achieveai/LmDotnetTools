using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// WI #194 tasks 7-8: the opt-in <c>strictCanonicalPersistence</c> mode on
/// <see cref="MultiTurnAgentBase"/> — one per-agent ordered persistence queue covering every
/// <c>AddToHistory</c> canonical append and <see cref="MultiTurnAgentBase"/>'s
/// <c>ReplacePersistedAsync</c> replacement, and a terminal flush barrier in
/// <c>CompleteRunAsync</c> that fails closed (no terminal ledger write, no
/// <see cref="RunCompletedMessage"/>) when any queued write failed. Default (best-effort) mode
/// must remain byte-for-byte unaffected.
/// </summary>
public class StrictCanonicalPersistenceTests
{
    [Fact]
    public async Task AddToHistory_StrictMode_OrdersCanonicalAppendsDespiteGatedStoreCompletion()
    {
        // Arrange: gate the FIRST AppendMessagesAsync call so it cannot complete until released.
        var store = new ConfigurableCanonicalStore { GateAppendCallNumber = 1 };
        await using var agent = new CanonicalPersistenceTestAgent(
            "strict-order-thread", store, strictCanonicalPersistence: true);

        await agent.StartRunForTestAsync();

        // Act: two ordinary (fire-and-forget-to-the-caller) AddToHistory appends.
        agent.AddToHistoryForTest(new TextMessage { Text = "first", Role = Role.User }, "run-1");
        agent.AddToHistoryForTest(new TextMessage { Text = "second", Role = Role.User }, "run-1");

        // Wait until the store has actually seen the FIRST call arrive (proves the chain
        // reached it), while it's still gated.
        await store.AppendGateReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert: the SECOND append must not have reached the store yet — it is chained
        // strictly behind the first and must wait for it, even though the caller already
        // returned from both AddToHistory calls above.
        store.AppendCallCount.Should().Be(
            1,
            "the second canonical append must not start until the first (still gated) one completes");

        // Release the gate and let the chain drain.
        store.ReleaseAppendGate();
        await agent.CanonicalPersistenceTailSnapshotForTests().WaitAsync(TimeSpan.FromSeconds(5));

        store.AppendCallCount.Should().Be(2);
        store.OperationLog.Should().Equal("Append#1", "Append#2");
    }

    [Fact]
    public async Task ReplacePersistedAsync_StrictMode_WaitsBehindGatedPlaceholderAppend()
    {
        // Arrange: gate the placeholder's append so it cannot reach the store until released.
        var store = new ConfigurableCanonicalStore { GateAppendCallNumber = 1 };
        await using var agent = new CanonicalPersistenceTestAgent(
            "strict-replace-order-thread", store, strictCanonicalPersistence: true);

        await agent.StartRunForTestAsync();

        var placeholder = new ToolCallResultMessage
        {
            ToolCallId = "tc-1",
            Result = string.Empty,
            IsDeferred = true,
            Role = Role.User,
        };
        var resolved = placeholder with { Result = "done", IsDeferred = false };

        // Act: enqueue the placeholder append (gated) FIRST — directly, not via Task.Run, so its
        // enqueue onto the ordered chain happens synchronously and deterministically before the
        // replacement's enqueue below (matching production: the loop always fully awaits
        // AddDeferredToHistoryAsync before any resolution can call ReplacePersistedAsync). Each
        // call synchronously enqueues its own chain node before suspending on the still-open gate,
        // so the returned Task can be captured without awaiting it yet.
        var appendTask = agent.AddDeferredToHistoryForTestAsync(placeholder);
        var replaceTask = agent.ReplacePersistedForTestAsync(placeholder, resolved);

        await store.AppendGateReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert: the moment the store observes the append call ARRIVE (still gated, blocked
        // inside AppendMessagesAsync), the replacement's chain node — which strictly awaits the
        // append's node first — cannot possibly have started its own operation yet. No sleep is
        // needed: this is a direct consequence of the chain's ordering guarantee, not a timing
        // race.
        store.ReplaceCallCount.Should().Be(
            0,
            "the replacement must run after the placeholder append it replaces, not concurrently with it");

        store.ReleaseAppendGate();

        await appendTask.WaitAsync(TimeSpan.FromSeconds(5));
        await replaceTask.WaitAsync(TimeSpan.FromSeconds(5));

        store.OperationLog.Should().Equal("Append#1", "Replace#1");
    }

    [Fact]
    public async Task ReplacePersistedAsync_StrictMode_StoreFailure_PropagatesToCaller()
    {
        var store = new ConfigurableCanonicalStore { ThrowOnReplace = true };
        await using var agent = new CanonicalPersistenceTestAgent(
            "strict-replace-failure-thread", store, strictCanonicalPersistence: true);

        await agent.StartRunForTestAsync();

        var placeholder = new ToolCallResultMessage
        {
            ToolCallId = "tc-2",
            Result = string.Empty,
            IsDeferred = true,
            Role = Role.User,
        };
        var resolved = placeholder with { Result = "done", IsDeferred = false };

        await agent.AddDeferredToHistoryForTestAsync(placeholder);

        var act = async () => await agent.ReplacePersistedForTestAsync(placeholder, resolved);

        await act.Should().ThrowAsync<InvalidOperationException>(
            "strict mode must surface a replacement store failure to the caller instead of swallowing it");
    }

    [Fact]
    public async Task ResolveToolCallAsync_StrictMode_ReplacementStoreFailure_PropagatesToCaller()
    {
        // Loop-level proof: the strict-mode replacement failure contract is visible through
        // MultiTurnAgentLoop.ResolveToolCallAsync, not just the MultiTurnAgentBase primitive.
        var store = new ConfigurableCanonicalStore { ThrowOnReplace = true };

        var toolCall = new ToolCallMessage
        {
            FunctionName = "long_op",
            FunctionArgs = "{}",
            ToolCallId = "tc_deferred",
            Role = Role.Assistant,
        };
        var mockAgent = new Mock<IStreamingAgent>();
        mockAgent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(ToAsyncEnumerable([toolCall])));

        var registry = new FunctionRegistry();
        registry.AddFunction(
            new FunctionContract { Name = "long_op", Description = "test", Parameters = [] },
            (_, _, _) => Task.FromResult<ToolHandlerResult>(new ToolHandlerResult.Deferred()));

        await using var loop = new MultiTurnAgentLoop(
            mockAgent.Object,
            registry,
            "strict-loop-replace-failure-thread",
            store: store,
            strictCanonicalPersistence: true);

        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        var input = new UserInput([new TextMessage { Text = "go", Role = Role.User }]);
        await foreach (var _ in loop.ExecuteRunAsync(input, cts.Token))
        {
            // Drain until the run (which ends on the deferral) completes.
        }

        var act = async () => await loop.ResolveToolCallAsync("tc_deferred", "final-value");

        await act.Should().ThrowAsync<InvalidOperationException>(
            "strict mode's replacement-failure propagation contract must reach ResolveToolCallAsync's caller");

        await cts.CancelAsync();
    }

    private static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(
        IEnumerable<IMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var msg in messages)
        {
            ct.ThrowIfCancellationRequested();
            yield return msg;
            await Task.Yield();
        }
    }

    [Fact]
    public async Task ReplacePersistedAsync_DefaultMode_StoreFailure_IsSwallowed_NotPropagated()
    {
        // Regression: default (best-effort) mode must keep swallowing/logging a replacement
        // store failure exactly as before strict mode existed.
        var store = new ConfigurableCanonicalStore { ThrowOnReplace = true };
        await using var agent = new CanonicalPersistenceTestAgent(
            "default-replace-failure-thread", store, strictCanonicalPersistence: false);

        await agent.StartRunForTestAsync();

        var placeholder = new ToolCallResultMessage
        {
            ToolCallId = "tc-3",
            Result = string.Empty,
            IsDeferred = true,
            Role = Role.User,
        };
        var resolved = placeholder with { Result = "done", IsDeferred = false };

        await agent.AddDeferredToHistoryForTestAsync(placeholder);

        var act = async () => await agent.ReplacePersistedForTestAsync(placeholder, resolved);

        await act.Should().NotThrowAsync(
            "default mode preserves today's best-effort logged/swallowed replacement failure");
    }

    [Fact]
    public async Task CompleteRunAsync_StrictMode_CanonicalAppendFailure_PreventsTerminalMessageAndLedgerCompletion()
    {
        var store = new ConfigurableCanonicalStore { ThrowOnAppend = true };
        await using var agent = new CanonicalPersistenceTestAgent(
            "strict-flush-failure-thread", store, strictCanonicalPersistence: true, persistRunLedger: true);

        // Subscribe and register (synchronously, before anything is published) via the
        // established GetAsyncEnumerator + MoveNextAsync pattern (see MultiTurnAgentReplayTests):
        // the first MoveNextAsync call registers the subscriber before it can ever suspend. Not
        // `await using` — the pending MoveNextAsync below is deliberately left unresolved until
        // this method explicitly cancels/awaits it (a compiler-generated async iterator forbids
        // disposing while a MoveNextAsync call is still outstanding).
        using var subscribeCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var subscriber = agent.SubscribeAsync(subscribeCts.Token).GetAsyncEnumerator(subscribeCts.Token);
        var next = subscriber.MoveNextAsync();

        var assignment = await agent.StartRunForTestAsync();
        agent.AddToHistoryForTest(new TextMessage { Text = "hello", Role = Role.User }, assignment.RunId);

        // Let the (failing) queued append actually run before completing the run — we only need
        // it to have RUN (so CompleteRunAsync's flush below observes the sticky fault), not
        // succeeded, so the expected failure here is swallowed.
        try
        {
            await agent.CanonicalPersistenceTailSnapshotForTests().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Expected — see remark above.
        }

        // Act 1: natural completion must fail closed — no terminal ledger write, no terminal
        // message — because the queued canonical append failed.
        var naturalComplete = async () =>
            await agent.CompleteRunForTestAsync(assignment.RunId, assignment.GenerationId);
        await naturalComplete.Should().ThrowAsync<InvalidOperationException>();

        // Act 2: the caller's error-completion fallback (mirroring MultiTurnAgentLoop.RunLoopAsync's
        // natural-then-error retry) must ALSO fail closed on the SAME broken agent — an error
        // terminal must not claim durable success while canonical persistence remains failed.
        var errorComplete = async () =>
            await agent.CompleteRunForTestAsync(
                assignment.RunId, assignment.GenerationId, isError: true, errorMessage: "boom");
        await errorComplete.Should().ThrowAsync<InvalidOperationException>();

        // Assert: no terminal message was ever published — the still-pending subscriber read
        // must not have completed. Give it a short, bounded window rather than asserting
        // instantaneously, since a message that WAS (incorrectly) published might take a moment
        // to reach the channel. `ValueTask.AsTask()` may only be consumed once, so materialize it
        // to a plain Task exactly once and reuse that reference for both the race and the assert.
        var nextTask = next.AsTask();
        var raced = await Task.WhenAny(nextTask, Task.Delay(TimeSpan.FromMilliseconds(500)));
        raced.Should().NotBe(
            nextTask,
            "no terminal message may be published — natural or error — while canonical persistence is broken");

        // Resolve the still-pending read (cancel, then await the expected cancellation) before
        // disposing the enumerator — see remark on `subscriber` above.
        await subscribeCts.CancelAsync();
        try
        {
            await nextTask;
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        await subscriber.DisposeAsync();

        var ledgerEntry = await store.LoadRunLedgerAsync(assignment.RunId);
        ledgerEntry.Should().NotBeNull();
        ledgerEntry!.Status.Should().Be(
            RunStatus.InProgress,
            "the terminal ledger write must never be reached while the canonical persistence flush fails");
    }

    [Fact]
    public async Task CompleteRunAsync_DefaultMode_CanonicalAppendFailure_StillPublishesTerminalAndCompletesLedger()
    {
        // Regression: default (best-effort) mode must complete/publish exactly as before —
        // a canonical append failure is logged and swallowed, never blocking the terminal.
        var store = new ConfigurableCanonicalStore { ThrowOnAppend = true };
        await using var agent = new CanonicalPersistenceTestAgent(
            "default-flush-failure-thread", store, strictCanonicalPersistence: false, persistRunLedger: true);

        using var subscribeCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var subscriber = agent.SubscribeAsync(subscribeCts.Token).GetAsyncEnumerator(subscribeCts.Token);
        var next = subscriber.MoveNextAsync();

        var assignment = await agent.StartRunForTestAsync();
        agent.AddToHistoryForTest(new TextMessage { Text = "hello", Role = Role.User }, assignment.RunId);

        await agent.CompleteRunForTestAsync(assignment.RunId, assignment.GenerationId);

        (await next.AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        subscriber.Current.Should().BeOfType<RunCompletedMessage>().Which.Should().Match<RunCompletedMessage>(
            m => m.CompletedRunId == assignment.RunId && !m.IsError && !m.IsCancelled);

        var ledgerEntry = await store.LoadRunLedgerAsync(assignment.RunId);
        ledgerEntry.Should().NotBeNull();
        ledgerEntry!.Status.Should().Be(RunStatus.Completed);
    }

    [Fact]
    public async Task DisposeAsync_StrictMode_DoesNotHang_WithPendingCanonicalPersistence()
    {
        var store = new ConfigurableCanonicalStore { GateAppendCallNumber = 1 };
        var agent = new CanonicalPersistenceTestAgent(
            "strict-dispose-thread", store, strictCanonicalPersistence: true);

        await agent.StartRunForTestAsync();
        agent.AddToHistoryForTest(new TextMessage { Text = "never released", Role = Role.User }, "run-1");

        await store.AppendGateReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Act: dispose while the chain is permanently gated (never released). Disposal must
        // bound-await and return promptly rather than hang forever.
        var disposeTask = agent.DisposeAsync().AsTask();
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task DisposeAsync_StrictMode_DurabilityTokenCancellation_DoesNotRecordStickyFault()
    {
        // A permanently-gated canonical write observes the per-agent durability lifetime token
        // (see `_canonicalPersistenceLifetimeCts`), NOT any run/request caller token. DisposeAsync
        // cancels that token to unblock the gate. The resulting OperationCanceledException from
        // the gated store call must be a disposal-driven shutdown signal, never a sticky
        // `_canonicalPersistenceFault` — that fault would otherwise permanently brick a future
        // CompleteRunAsync flush for reasons entirely of disposal's own making.
        var store = new ConfigurableCanonicalStore { GateAppendCallNumber = 1 };
        var agent = new CanonicalPersistenceTestAgent(
            "strict-dispose-fault-thread", store, strictCanonicalPersistence: true);

        await agent.StartRunForTestAsync();
        agent.AddToHistoryForTest(new TextMessage { Text = "never released", Role = Role.User }, "run-1");

        await store.AppendGateReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        agent.HasCanonicalPersistenceFaultForTests.Should().BeFalse(
            "no failure has happened yet — the write is merely gated");

        var disposeTask = agent.DisposeAsync().AsTask();
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(10));

        agent.HasCanonicalPersistenceFaultForTests.Should().BeFalse(
            "disposal cancelling its own durability token to unblock a gated write must never be recorded as a sticky canonical persistence fault");
    }

    [Fact]
    public async Task AddDeferredToHistoryAsync_StrictMode_RunTokenCancellation_CancelsCallerWaitOnly_WriteLaterCompletesAndFlushSucceeds()
    {
        // Requirement: a normal run-token cancellation (e.g. CancelCurrentRunAsync) must cancel
        // ONLY the caller's own wait for the queued write — never the underlying store write
        // itself (which runs under the per-agent durability token) — and must never record a
        // sticky canonical persistence fault or brick a later terminal flush.
        var store = new ConfigurableCanonicalStore { GateAppendCallNumber = 1 };
        await using var agent = new CanonicalPersistenceTestAgent(
            "strict-run-token-cancel-thread", store, strictCanonicalPersistence: true);

        var assignment = await agent.StartRunForTestAsync();

        var placeholder = new ToolCallResultMessage
        {
            ToolCallId = "tc-run-cancel",
            Result = string.Empty,
            IsDeferred = true,
            Role = Role.User,
        };

        using var runCts = new CancellationTokenSource();

        var appendTask = agent.AddDeferredToHistoryForTestAsync(placeholder, runCts.Token);

        await store.AppendGateReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Act: cancel the caller's own (run) token while the write is still gated inside the
        // store call.
        await runCts.CancelAsync();

        var act = async () => await appendTask;
        await act.Should().ThrowAsync<OperationCanceledException>(
            "the caller's own WaitAsync must observe its cancelled token");

        // Assert: the underlying write is UNAFFECTED by the caller's cancellation — it is still
        // gated (only one call has reached the store so far) and has not failed.
        store.AppendCallCount.Should().Be(1);
        agent.HasCanonicalPersistenceFaultForTests.Should().BeFalse(
            "a caller/run-token cancellation must never poison the sticky canonical persistence fault");

        // Release the gate: the write — running under the durability token, never the
        // already-cancelled caller token — completes successfully on its own.
        store.ReleaseAppendGate();
        await agent.CanonicalPersistenceTailSnapshotForTests().WaitAsync(TimeSpan.FromSeconds(5));

        store.AppendCallCount.Should().Be(1);
        store.OperationLog.Should().Equal("Append#1");
        agent.HasCanonicalPersistenceFaultForTests.Should().BeFalse();

        // A later terminal flush on this SAME (still-live) agent must succeed — the earlier
        // caller-side cancellation left no trace on the chain.
        await agent.CompleteRunForTestAsync(assignment.RunId, assignment.GenerationId);
    }

    [Fact]
    public async Task ReplacePersistedAsync_StrictMode_CallerCancellation_CancelsCallerWaitOnly_WriteLaterCompletesAndFlushSucceeds()
    {
        // Mirrors the run-token test above, for ReplacePersistedAsync's caller — e.g. an external
        // ResolveToolCallAsync request whose OWN cancellation must never abort the queued
        // replacement write or poison the sticky canonical persistence fault.
        var store = new ConfigurableCanonicalStore { GateReplaceCallNumber = 1 };
        await using var agent = new CanonicalPersistenceTestAgent(
            "strict-replace-caller-cancel-thread", store, strictCanonicalPersistence: true);

        var assignment = await agent.StartRunForTestAsync();

        var placeholder = new ToolCallResultMessage
        {
            ToolCallId = "tc-replace-cancel",
            Result = string.Empty,
            IsDeferred = true,
            Role = Role.User,
        };
        var resolved = placeholder with { Result = "done", IsDeferred = false };

        await agent.AddDeferredToHistoryForTestAsync(placeholder);

        using var callerCts = new CancellationTokenSource();

        var replaceTask = agent.ReplacePersistedForTestAsync(placeholder, resolved, callerCts.Token);

        await store.ReplaceGateReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Act: cancel the caller's own request token while the replacement is still gated
        // inside the store call.
        await callerCts.CancelAsync();

        var act = async () => await replaceTask;
        await act.Should().ThrowAsync<OperationCanceledException>(
            "the caller's own WaitAsync must observe its cancelled token");

        store.ReplaceCallCount.Should().Be(1);
        agent.HasCanonicalPersistenceFaultForTests.Should().BeFalse(
            "a caller-token cancellation must never poison the sticky canonical persistence fault");

        // Release the gate: the write completes successfully under the (never-cancelled)
        // durability token.
        store.ReleaseReplaceGate();
        await agent.CanonicalPersistenceTailSnapshotForTests().WaitAsync(TimeSpan.FromSeconds(5));

        store.ReplaceCallCount.Should().Be(1);
        store.OperationLog.Should().Equal("Append#1", "Replace#1");
        agent.HasCanonicalPersistenceFaultForTests.Should().BeFalse();

        // A later terminal flush on this SAME (still-live) agent must succeed.
        await agent.CompleteRunForTestAsync(assignment.RunId, assignment.GenerationId);
    }

    [Fact]
    public async Task EnqueueCanonicalPersistenceOperation_BlockedBeforeLock_RacingDisposal_CannotAppendAfterSnapshot()
    {
        // Closes the enqueue-vs-disposal TOCTOU: an enqueue call blocked right before acquiring
        // `_canonicalPersistenceLock` — exactly the instant DisposeAsync marks `_isDisposed` and
        // snapshots the chain's tail — must resume into a predictable ObjectDisposedException
        // rather than silently appending a node disposal's snapshot already missed.
        var store = new ConfigurableCanonicalStore();
        var agent = new CanonicalPersistenceTestAgent(
            "strict-enqueue-dispose-race-thread", store, strictCanonicalPersistence: true);

        await agent.StartRunForTestAsync();

        var hookReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hookGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.PreCanonicalPersistenceLockHookForTests = () =>
        {
            hookReached.TrySetResult();
            return hookGate.Task;
        };

        var message = new ToolCallResultMessage
        {
            ToolCallId = "tc-toctou",
            Result = string.Empty,
            IsDeferred = true,
            Role = Role.User,
        };

        // Act: start the deferred append — it will suspend on the test hook BEFORE ever
        // acquiring `_canonicalPersistenceLock` or touching the store.
        var appendTask = agent.AddDeferredToHistoryForTestAsync(message);

        await hookReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Disposal proceeds while the append is still blocked at the hook — it marks
        // `_isDisposed` and snapshots the (still-empty) chain tail with no knowledge of the
        // blocked call, then tears down without ever waiting on it, so this must complete
        // promptly rather than hang for the blocked call to resume.
        var disposeTask = agent.DisposeAsync().AsTask();
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(10));

        // Now let the blocked append resume: it must observe `_isDisposed` already true when it
        // finally acquires the lock, and fail there — before adding any node or touching the
        // store.
        hookGate.TrySetResult();

        var act = async () => await appendTask;
        await act.Should().ThrowAsync<ObjectDisposedException>(
            "an enqueue call that loses the race to disposal must fail before appending a node");

        store.AppendCallCount.Should().Be(
            0,
            "the write must never reach the store once disposal has already snapshotted the chain's tail");
    }

    /// <summary>
    /// Minimal concrete <see cref="MultiTurnAgentBase"/> exposing the protected
    /// canonical-persistence primitives for direct testing without needing a full
    /// <see cref="MultiTurnAgentBase.RunLoopAsync"/> drive loop — <see cref="StartRunForTestAsync"/>
    /// establishes the active-run state those primitives read directly.
    /// </summary>
    private sealed class CanonicalPersistenceTestAgent : MultiTurnAgentBase
    {
        public CanonicalPersistenceTestAgent(
            string threadId,
            IConversationStore store,
            bool strictCanonicalPersistence,
            bool persistRunLedger = false)
            : base(
                threadId,
                store: store,
                persistRunLedger: persistRunLedger,
                strictCanonicalPersistence: strictCanonicalPersistence)
        {
        }

        protected override Task RunLoopAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<RunAssignment> StartRunForTestAsync(CancellationToken ct = default)
        {
            var input = new QueuedInput(
                new UserInput([]), Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
            return StartRunAsync([input], ct: ct);
        }

        public void AddToHistoryForTest(IMessage message, string runIdOverride) =>
            AddToHistory(message, runIdOverride);

        public Task AddDeferredToHistoryForTestAsync(IMessage message, CancellationToken ct = default) =>
            AddDeferredToHistoryAsync(message, ct);

        public Task ReplacePersistedForTestAsync(
            ToolCallResultMessage old, ToolCallResultMessage updated, CancellationToken ct = default) =>
            ReplacePersistedAsync(old, updated, ct);

        public Task CompleteRunForTestAsync(
            string runId,
            string generationId,
            bool isError = false,
            string? errorMessage = null,
            CancellationToken ct = default) =>
            CompleteRunAsync(runId, generationId, isError: isError, errorMessage: errorMessage, ct: ct);
    }

    /// <summary>
    /// Configurable <see cref="IConversationStore"/>/<see cref="IRunLedgerStore"/> decorator over
    /// <see cref="InMemoryConversationStore"/> supporting: gating a specific
    /// <see cref="AppendMessagesAsync"/> call number until released, unconditionally throwing on
    /// append/replace, and a shared ordered operation log (post-gate) proving canonical
    /// persistence chain ordering.
    /// </summary>
    private sealed class ConfigurableCanonicalStore : IConversationStore, IRunLedgerStore
    {
        private readonly InMemoryConversationStore _inner = new();
        private readonly TaskCompletionSource _appendRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _replaceRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _logLock = new();
        private int _appendCallCount;
        private int _replaceCallCount;

        public TaskCompletionSource AppendGateReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReplaceGateReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>1-indexed AppendMessagesAsync call number to gate. 0 (default) gates none.</summary>
        public int GateAppendCallNumber { get; set; }

        /// <summary>1-indexed ReplaceMessageAsync call number to gate. 0 (default) gates none.</summary>
        public int GateReplaceCallNumber { get; set; }

        public bool ThrowOnAppend { get; set; }

        public bool ThrowOnReplace { get; set; }

        public List<string> OperationLog { get; } = [];

        public int AppendCallCount => Volatile.Read(ref _appendCallCount);

        public int ReplaceCallCount => Volatile.Read(ref _replaceCallCount);

        public void ReleaseAppendGate() => _appendRelease.TrySetResult();

        public void ReleaseReplaceGate() => _replaceRelease.TrySetResult();

        public async Task AppendMessagesAsync(
            string threadId, IReadOnlyList<PersistedMessage> messages, CancellationToken ct = default)
        {
            var callNumber = Interlocked.Increment(ref _appendCallCount);
            if (callNumber == GateAppendCallNumber)
            {
                AppendGateReached.TrySetResult();
                await _appendRelease.Task.WaitAsync(ct);
            }

            lock (_logLock)
            {
                OperationLog.Add($"Append#{callNumber}");
            }

            if (ThrowOnAppend)
            {
                throw new InvalidOperationException("Simulated canonical append failure");
            }

            await _inner.AppendMessagesAsync(threadId, messages, ct);
        }

        public async Task ReplaceMessageAsync(
            string threadId, PersistedMessage replacement, CancellationToken ct = default)
        {
            var callNumber = Interlocked.Increment(ref _replaceCallCount);
            if (callNumber == GateReplaceCallNumber)
            {
                ReplaceGateReached.TrySetResult();
                await _replaceRelease.Task.WaitAsync(ct);
            }

            lock (_logLock)
            {
                OperationLog.Add($"Replace#{callNumber}");
            }

            if (ThrowOnReplace)
            {
                throw new InvalidOperationException("Simulated canonical replace failure");
            }

            await _inner.ReplaceMessageAsync(threadId, replacement, ct);
        }

        public Task<IReadOnlyList<PersistedMessage>> LoadMessagesAsync(
            string threadId, CancellationToken ct = default) =>
            _inner.LoadMessagesAsync(threadId, ct);

        public Task SaveMetadataAsync(string threadId, ThreadMetadata metadata, CancellationToken ct = default) =>
            _inner.SaveMetadataAsync(threadId, metadata, ct);

        public Task<ThreadMetadata?> LoadMetadataAsync(string threadId, CancellationToken ct = default) =>
            _inner.LoadMetadataAsync(threadId, ct);

        public Task UpdateMetadataAsync(
            string threadId, Func<ThreadMetadata?, ThreadMetadata> update, CancellationToken ct = default) =>
            _inner.UpdateMetadataAsync(threadId, update, ct);

        public Task DeleteThreadAsync(string threadId, CancellationToken ct = default) =>
            _inner.DeleteThreadAsync(threadId, ct);

        public Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
            int limit = 50, int offset = 0, CancellationToken ct = default) =>
            _inner.ListThreadsAsync(limit, offset, ct);

        public Task UpsertRunLedgerAsync(RunLedgerEntry entry, CancellationToken ct = default) =>
            _inner.UpsertRunLedgerAsync(entry, ct);

        public Task<RunLedgerEntry?> LoadRunLedgerAsync(string runId, CancellationToken ct = default) =>
            _inner.LoadRunLedgerAsync(runId, ct);

        public Task<IReadOnlyList<RunLedgerEntry>> ListRunLedgerAsync(
            string threadId, CancellationToken ct = default) =>
            _inner.ListRunLedgerAsync(threadId, ct);

        public Task RecordAcceptedInputAsync(
            string threadId, string inputId, DateTimeOffset acceptedAt, CancellationToken ct = default) =>
            _inner.RecordAcceptedInputAsync(threadId, inputId, acceptedAt, ct);

        public Task RemoveAcceptedInputAsync(string threadId, string inputId, CancellationToken ct = default) =>
            _inner.RemoveAcceptedInputAsync(threadId, inputId, ct);

        public Task<IReadOnlySet<string>> ListAcceptedInputIdsAsync(
            string threadId, CancellationToken ct = default) =>
            _inner.ListAcceptedInputIdsAsync(threadId, ct);
    }
}
