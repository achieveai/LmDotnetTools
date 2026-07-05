using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Persistence;

/// <summary>
/// Integration-level tests for <see cref="MultiTurnAgentBase"/>'s restart reconciliation
/// (<c>ReconcileRunLedgerAsync</c>, which fires once per process instance from the start of
/// <c>RunAsync</c>). Covers the crash-window edge cases: dangling Queued/InProgress runs marked
/// Interrupted, terminal statuses left untouched, orphan accepted-inputs synthesized into
/// Interrupted rows, once-per-instance idempotency, and a swallowed transient store fault.
/// </summary>
public class RunLedgerRecoveryTests
{
    #region Dangling run reconciliation

    [Theory]
    [InlineData(RunStatus.Queued)]
    [InlineData(RunStatus.InProgress)]
    public async Task Reconcile_MarksDanglingRun_Interrupted(RunStatus seededStatus)
    {
        // Simulate a crash that left a run mid-flight: pre-seed the store BEFORE the (fresh
        // process) agent starts, then start RunAsync and expect reconciliation to mark it
        // Interrupted (nothing can still be running it after a fresh start).
        var store = new InMemoryConversationStore();
        const string threadId = "recovery-dangling";
        const string runId = "run-dangling";
        var createdAt = DateTimeOffset.UtcNow;
        await store.UpsertRunLedgerAsync(new RunLedgerEntry(
            threadId, runId, seededStatus, ["input-1"], createdAt, createdAt));

        var agent = new RecoveryTestAgent(threadId, store: store, persistRunLedger: true);
        using var cts = new CancellationTokenSource();
        _ = agent.RunAsync(cts.Token);

        var interrupted = await WaitUntilAsync(
            async () => (await store.LoadRunLedgerAsync(runId))?.Status == RunStatus.Interrupted,
            TimeSpan.FromSeconds(5));

        interrupted.Should().BeTrue($"a dangling {seededStatus} run must be reconciled to Interrupted on restart");
        (await store.LoadRunLedgerAsync(runId))!.Status.Should().Be(RunStatus.Interrupted);

        await cts.CancelAsync();
        await agent.DisposeAsync();
    }

    [Theory]
    [InlineData(RunStatus.Completed)]
    [InlineData(RunStatus.Errored)]
    public async Task Reconcile_LeavesTerminalRun_Untouched(RunStatus terminalStatus)
    {
        // Terminal statuses are final forever; reconciliation must never overwrite them.
        var store = new InMemoryConversationStore();
        const string threadId = "recovery-terminal";
        const string runId = "run-terminal";
        var createdAt = DateTimeOffset.UtcNow;
        await store.UpsertRunLedgerAsync(new RunLedgerEntry(
            threadId, runId, terminalStatus, ["input-1"], createdAt, createdAt));

        var agent = new RecoveryTestAgent(threadId, store: store, persistRunLedger: true);
        using var cts = new CancellationTokenSource();
        _ = agent.RunAsync(cts.Token);

        // Once the loop is running, reconciliation has definitely completed (it awaits before the
        // loop task is assigned) — so a still-terminal status proves it was left untouched.
        var running = await WaitUntilAsync(() => agent.IsRunning, TimeSpan.FromSeconds(5));
        running.Should().BeTrue();

        (await store.LoadRunLedgerAsync(runId))!.Status.Should().Be(terminalStatus);

        await cts.CancelAsync();
        await agent.DisposeAsync();
    }

    #endregion

    #region Orphan accepted-input synthesis

    [Fact]
    public async Task Reconcile_SynthesizesInterruptedRun_ForOrphanAcceptedInput()
    {
        // Crash after 202-accepted but before StartRunAsync ran: an accepted-input record exists
        // with NO ledger entry covering it. Reconciliation must synthesize an Interrupted row so
        // the inputId resolves without a restart-specific code path.
        var store = new InMemoryConversationStore();
        const string threadId = "recovery-orphan";
        const string inputId = "orphan-input";
        await store.RecordAcceptedInputAsync(threadId, inputId, DateTimeOffset.UtcNow);

        var agent = new RecoveryTestAgent(threadId, store: store, persistRunLedger: true);
        using var cts = new CancellationTokenSource();
        _ = agent.RunAsync(cts.Token);

        var synthesized = await WaitUntilAsync(
            async () => (await store.ListRunLedgerAsync(threadId))
                .Any(e => e.Status == RunStatus.Interrupted && e.InputIds.Contains(inputId)),
            TimeSpan.FromSeconds(5));

        synthesized.Should().BeTrue();
        var orphan = (await store.ListRunLedgerAsync(threadId))
            .Single(e => e.InputIds.Contains(inputId));
        orphan.Status.Should().Be(RunStatus.Interrupted);
        orphan.ThreadId.Should().Be(threadId);

        await cts.CancelAsync();
        await agent.DisposeAsync();
    }

    [Fact]
    public async Task Reconcile_DoesNotSynthesizeOrphan_WhenAcceptedInputAlreadyCoveredByRun()
    {
        // An accepted-input whose id is already listed on an existing (terminal) run is NOT an
        // orphan — no synthetic row should be created for it.
        var store = new InMemoryConversationStore();
        const string threadId = "recovery-covered";
        const string inputId = "covered-input";
        const string runId = "run-covered";
        var createdAt = DateTimeOffset.UtcNow;
        await store.UpsertRunLedgerAsync(new RunLedgerEntry(
            threadId, runId, RunStatus.Completed, [inputId], createdAt, createdAt));
        await store.RecordAcceptedInputAsync(threadId, inputId, createdAt);

        var agent = new RecoveryTestAgent(threadId, store: store, persistRunLedger: true);
        using var cts = new CancellationTokenSource();
        _ = agent.RunAsync(cts.Token);

        var running = await WaitUntilAsync(() => agent.IsRunning, TimeSpan.FromSeconds(5));
        running.Should().BeTrue();

        var entries = await store.ListRunLedgerAsync(threadId);
        entries.Should().ContainSingle("the covered input must not spawn a second, synthetic run");
        entries.Single().RunId.Should().Be(runId);

        await cts.CancelAsync();
        await agent.DisposeAsync();
    }

    #endregion

    #region Once-per-instance idempotency

    [Fact]
    public async Task Reconcile_RunsOncePerInstance_NotOncePerRunAsyncCall()
    {
        // Reconciliation is a once-per-process-instance concept. A second RunAsync on the SAME
        // instance must not re-reconcile, which would double-synthesize the orphan row.
        var store = new InMemoryConversationStore();
        const string threadId = "recovery-idempotent";
        const string inputId = "idempotent-orphan";
        await store.RecordAcceptedInputAsync(threadId, inputId, DateTimeOffset.UtcNow);

        var agent = new RecoveryTestAgent(threadId, store: store, persistRunLedger: true);
        using var cts = new CancellationTokenSource();

        // First start: reconciliation synthesizes exactly one orphan row.
        _ = agent.RunAsync(cts.Token);
        var synthesized = await WaitUntilAsync(
            async () => (await store.ListRunLedgerAsync(threadId)).Any(e => e.InputIds.Contains(inputId)),
            TimeSpan.FromSeconds(5));
        synthesized.Should().BeTrue();
        CountCovering(await store.ListRunLedgerAsync(threadId), inputId).Should().Be(1);
        await agent.StopAsync();

        // Second start on the same instance: reconciliation is guarded, so no new orphan.
        _ = agent.RunAsync(cts.Token);
        var runningAgain = await WaitUntilAsync(() => agent.IsRunning, TimeSpan.FromSeconds(5));
        runningAgain.Should().BeTrue();
        // Give any (erroneous) second reconciliation a chance to run before asserting stability.
        await Task.Delay(100);
        CountCovering(await store.ListRunLedgerAsync(threadId), inputId)
            .Should().Be(1, "restart reconciliation must not re-fire on a second RunAsync of the same instance");

        await cts.CancelAsync();
        await agent.DisposeAsync();
    }

    #endregion

    #region Fault tolerance

    [Fact]
    public async Task Reconcile_SwallowsTransientStoreFault_AndStillProcessesRealInput()
    {
        // A transient store fault during reconciliation must be logged-and-swallowed, never
        // preventing the agent from starting and processing a genuine queued input afterward.
        var inner = new InMemoryConversationStore();
        var faulting = new FaultOnceOnListRunLedgerStore(inner);
        const string threadId = "recovery-faulting";

        var agent = new RecoveryTestAgent(threadId, store: faulting, persistRunLedger: true);
        using var cts = new CancellationTokenSource();
        _ = agent.RunAsync(cts.Token);

        // Reconciliation's ListRunLedgerAsync throws once; startup must proceed regardless.
        await agent.SendAsync([new TextMessage { Text = "Hi", Role = Role.User }], inputId: "real-input");

        // Query the INNER store directly so the test never trips the decorator's one-shot fault.
        var processed = await WaitUntilAsync(
            async () => (await inner.ListRunLedgerAsync(threadId))
                .Any(e => e.InputIds.Contains("real-input") && e.Status == RunStatus.Completed),
            TimeSpan.FromSeconds(5));

        processed.Should().BeTrue("a swallowed reconciliation fault must not stop real input processing");
        faulting.ListRunLedgerCallCount.Should().BeGreaterThan(0, "reconciliation must have attempted the faulting call");

        await cts.CancelAsync();
        await agent.DisposeAsync();
    }

    #endregion

    #region Helpers

    private static int CountCovering(IReadOnlyList<RunLedgerEntry> entries, string inputId)
        => entries.Count(e => e.InputIds.Contains(inputId));

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return condition();
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return await condition();
    }

    #endregion

    #region Test doubles

    /// <summary>
    /// Minimal concrete <see cref="MultiTurnAgentBase"/> whose loop drives one run per drained
    /// batch. Reconciliation fires automatically from <c>RunAsync</c>; the loop's own run
    /// processing lets fault-tolerance tests prove startup proceeded after a swallowed fault.
    /// </summary>
    private sealed class RecoveryTestAgent : MultiTurnAgentBase
    {
        public RecoveryTestAgent(string threadId, IConversationStore? store, bool persistRunLedger)
            : base(threadId, store: store, persistRunLedger: persistRunLedger)
        {
        }

        protected override TimeSpan FallbackGracePeriod => TimeSpan.FromMilliseconds(100);

        protected override async Task RunLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                if (!await InputReader.WaitToReadAsync(ct))
                {
                    break;
                }

                if (!TryDrainInputs(out var batch) || batch.Count == 0)
                {
                    continue;
                }

                var assignment = await StartRunAsync(batch, ct: ct);
                await PublishToAllAsync(
                    new RunAssignmentMessage { Assignment = assignment, ThreadId = ThreadId },
                    ct);
                await CompleteRunAsync(assignment.RunId, assignment.GenerationId, false, null, 0, ct: ct);
            }
        }
    }

    /// <summary>
    /// Wraps an <see cref="InMemoryConversationStore"/> and throws exactly once on the first
    /// <see cref="IRunLedgerStore.ListRunLedgerAsync"/> call — the call reconciliation makes — then
    /// delegates everything to the inner store. Implements both store interfaces because a
    /// <c>persistRunLedger</c> agent requires the same instance to be both.
    /// </summary>
    private sealed class FaultOnceOnListRunLedgerStore : IConversationStore, IRunLedgerStore
    {
        private readonly InMemoryConversationStore _inner;
        private int _listRunLedgerCalls;

        public FaultOnceOnListRunLedgerStore(InMemoryConversationStore inner) => _inner = inner;

        public int ListRunLedgerCallCount => Volatile.Read(ref _listRunLedgerCalls);

        public Task<IReadOnlyList<RunLedgerEntry>> ListRunLedgerAsync(string threadId, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _listRunLedgerCalls) == 1)
            {
                throw new InvalidOperationException("Transient store fault (first ListRunLedgerAsync).");
            }

            return _inner.ListRunLedgerAsync(threadId, ct);
        }

        // === IRunLedgerStore delegation ===
        public Task UpsertRunLedgerAsync(RunLedgerEntry entry, CancellationToken ct = default)
            => _inner.UpsertRunLedgerAsync(entry, ct);

        public Task<RunLedgerEntry?> LoadRunLedgerAsync(string runId, CancellationToken ct = default)
            => _inner.LoadRunLedgerAsync(runId, ct);

        public Task RecordAcceptedInputAsync(string threadId, string inputId, DateTimeOffset acceptedAt, CancellationToken ct = default)
            => _inner.RecordAcceptedInputAsync(threadId, inputId, acceptedAt, ct);

        public Task RemoveAcceptedInputAsync(string threadId, string inputId, CancellationToken ct = default)
            => _inner.RemoveAcceptedInputAsync(threadId, inputId, ct);

        public Task<IReadOnlySet<string>> ListAcceptedInputIdsAsync(string threadId, CancellationToken ct = default)
            => _inner.ListAcceptedInputIdsAsync(threadId, ct);

        // === IConversationStore delegation ===
        public Task AppendMessagesAsync(string threadId, IReadOnlyList<PersistedMessage> messages, CancellationToken ct = default)
            => _inner.AppendMessagesAsync(threadId, messages, ct);

        public Task ReplaceMessageAsync(string threadId, PersistedMessage replacement, CancellationToken ct = default)
            => _inner.ReplaceMessageAsync(threadId, replacement, ct);

        public Task<IReadOnlyList<PersistedMessage>> LoadMessagesAsync(string threadId, CancellationToken ct = default)
            => _inner.LoadMessagesAsync(threadId, ct);

        public Task SaveMetadataAsync(string threadId, ThreadMetadata metadata, CancellationToken ct = default)
            => _inner.SaveMetadataAsync(threadId, metadata, ct);

        public Task<ThreadMetadata?> LoadMetadataAsync(string threadId, CancellationToken ct = default)
            => _inner.LoadMetadataAsync(threadId, ct);

        public Task UpdateMetadataAsync(string threadId, Func<ThreadMetadata?, ThreadMetadata> update, CancellationToken ct = default)
            => _inner.UpdateMetadataAsync(threadId, update, ct);

        public Task DeleteThreadAsync(string threadId, CancellationToken ct = default)
            => _inner.DeleteThreadAsync(threadId, ct);

        public Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(int limit = 50, int offset = 0, CancellationToken ct = default)
            => _inner.ListThreadsAsync(limit, offset, ct);
    }

    #endregion
}
