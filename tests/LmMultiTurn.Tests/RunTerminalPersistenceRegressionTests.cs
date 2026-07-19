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
/// Spec-review regression tests for WI #194 tasks 1-4 (commit e315b1fd):
/// <list type="bullet">
/// <item>
/// A terminal run-ledger write that throws on its first attempt must not permanently orphan the
/// run — the exactly-once terminal arbiter must release its claim on failure so a caller's retry
/// (e.g. the natural-completion-then-error-completion fallback in <c>MultiTurnAgentLoop.RunLoopAsync</c>)
/// can still durably persist and publish exactly one terminal outcome.
/// </item>
/// <item>
/// <c>CancelCurrentRunAsync</c> and natural/error completion must race atomically under the same
/// lock: once <c>CompleteRunAsync</c> has claimed a run's terminal outcome (even if the ledger
/// write for that outcome hasn't finished yet), a concurrent <c>CancelCurrentRunAsync</c> for the
/// same run must NOT report <see cref="RunCancellationResult.Accepted"/> — the final outcome is
/// already decided.
/// </item>
/// </list>
/// </summary>
public class RunTerminalPersistenceRegressionTests
{
    [Fact]
    public async Task CompleteRunAsync_LedgerThrowsOnFirstTerminalUpsert_RetrySucceeds_EmitsSingleErrorTerminal_LedgerNotStuckInProgress()
    {
        // Arrange: a run-ledger store whose FIRST terminal-status upsert (the natural-completion
        // attempt) throws, but succeeds on any later call (the error-completion retry that
        // MultiTurnAgentLoop.RunLoopAsync's own "catch (Exception ex) when (ex is not
        // OperationCanceledException)" fallback performs). The two StartRunAsync upserts
        // (Queued, then InProgress) must succeed normally so the run actually starts.
        var ledgerStore = new FlakyTerminalUpsertLedgerStore(throwOnUpsertCallNumber: 3);

        var mockAgent = new Mock<IStreamingAgent>();
        mockAgent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>((_, _, _) =>
                Task.FromResult(ToAsyncEnumerable(
                    [new TextMessage { Text = "hi", Role = Role.Assistant }])));

        var registry = new FunctionRegistry();
        await using var loop = new MultiTurnAgentLoop(
            mockAgent.Object,
            registry,
            "ledger-retry-thread",
            store: ledgerStore,
            persistRunLedger: true);

        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        var input = new UserInput([new TextMessage { Text = "Hi", Role = Role.User }], InputId: "ledger-retry-in-1");

        // Driven on a background task with a bounded wait — if the exactly-once arbiter
        // permanently orphans this run (the pre-fix bug), no RunCompletedMessage is ever
        // published and this enumeration would otherwise hang forever instead of failing fast.
        var messages = new List<IMessage>();
        var completion = Task.Run(async () =>
        {
            await foreach (var msg in loop.ExecuteRunAsync(input, cts.Token))
            {
                messages.Add(msg);
            }

            return messages;
        });

        await completion.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert: exactly one terminal — the retried error completion — reached the subscriber.
        // Before the fix, the exactly-once arbiter (keyed by an unbounded process-lifetime runId
        // dictionary claimed BEFORE the ledger write) permanently claimed the run on the first
        // (failing) attempt and silently no-op'd the retry: zero RunCompletedMessage would be
        // published here, and the ledger would stay InProgress forever.
        var completions = messages.OfType<RunCompletedMessage>().ToList();
        completions.Should().HaveCount(
            1,
            "the retried error-completion attempt must still durably publish exactly one terminal outcome");
        completions[0].IsError.Should().BeTrue("the retry runs the error-completion branch after the natural attempt's ledger write failed");
        completions[0].IsCancelled.Should().BeFalse();

        var runId = completions[0].CompletedRunId;
        var ledgerEntry = await ledgerStore.LoadRunLedgerAsync(runId);
        ledgerEntry.Should().NotBeNull();
        ledgerEntry!.Status.Should().Be(
            RunStatus.Errored,
            "the ledger must reflect the retried terminal write, not remain stuck at InProgress");

        await cts.CancelAsync();
    }

    [Fact]
    public async Task CancelCurrentRunAsync_RacingCompleteRunAsyncThatAlreadyClaimedTerminal_ReturnsNoActiveRun_NotAccepted()
    {
        // Arrange: a run-ledger store that lets StartRunAsync's two upserts through immediately,
        // then GATES the terminal upsert so the test can deterministically observe the moment
        // CompleteRunAsync has claimed the terminal outcome (under _stateLock) but has NOT yet
        // finished persisting it.
        var ledgerStore = new GatingTerminalUpsertLedgerStore();

        var registry = new FunctionRegistry();
        await using var agent = new NaturalCompletionOnlyAgent("atomic-stop-thread", ledgerStore);

        using var cts = new CancellationTokenSource();
        _ = agent.RunAsync(cts.Token);

        var input = new UserInput([new TextMessage { Text = "Hi", Role = Role.User }], InputId: "atomic-stop-in-1");

        var assignmentTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var messages = new List<IMessage>();
        var completion = Task.Run(async () =>
        {
            await foreach (var msg in agent.ExecuteRunAsync(input, cts.Token))
            {
                messages.Add(msg);
                if (msg is RunAssignmentMessage ram && !assignmentTcs.Task.IsCompleted)
                {
                    assignmentTcs.TrySetResult(ram.Assignment.RunId);
                }
            }

            return messages;
        });

        var runId = await assignmentTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Wait until CompleteRunAsync's natural-completion terminal write has actually started —
        // i.e. it has already won the exactly-once claim under _stateLock — but is gated before
        // finishing.
        await ledgerStore.TerminalUpsertStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Act: a Stop for the SAME run arriving while natural completion is already
        // terminalizing (claim already taken) must NOT be reported as Accepted — the outcome is
        // already decided in natural completion's favor.
        var outcome = await agent.CancelCurrentRunAsync(runId);

        // Release the gate so the natural completion's ledger write (and the rest of
        // CompleteRunAsync) can finish.
        ledgerStore.ReleaseTerminalUpsert();

        var allMessages = await completion.WaitAsync(TimeSpan.FromSeconds(5));

        outcome.Should().Be(
            RunCancellationResult.NoActiveRun,
            "natural completion already claimed this run's terminal outcome before Stop arrived — Accepted would misreport the final status as Cancelled");

        var completions = allMessages.OfType<RunCompletedMessage>().ToList();
        completions.Should().HaveCount(1);
        completions[0].CompletedRunId.Should().Be(runId);
        completions[0].IsCancelled.Should().BeFalse("natural completion won the race, so the outcome must be the natural one, not Cancelled");
        completions[0].IsError.Should().BeFalse();

        var ledgerEntry = await ledgerStore.LoadRunLedgerAsync(runId);
        ledgerEntry.Should().NotBeNull();
        ledgerEntry!.Status.Should().Be(RunStatus.Completed);

        await cts.CancelAsync();
    }

    private static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(
        List<IMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var msg in messages)
        {
            ct.ThrowIfCancellationRequested();
            yield return msg;
            await Task.Yield();
        }
    }

    /// <summary>
    /// Decorates <see cref="InMemoryConversationStore"/>, throwing once on a specific
    /// <see cref="UpsertRunLedgerAsync"/> call (1-indexed across the whole store's lifetime) and
    /// delegating every other call — including all later retries — normally.
    /// </summary>
    private sealed class FlakyTerminalUpsertLedgerStore : IConversationStore, IRunLedgerStore
    {
        private readonly InMemoryConversationStore _inner = new();
        private readonly int _throwOnUpsertCallNumber;
        private int _upsertCallCount;

        public FlakyTerminalUpsertLedgerStore(int throwOnUpsertCallNumber)
        {
            _throwOnUpsertCallNumber = throwOnUpsertCallNumber;
        }

        public Task UpsertRunLedgerAsync(RunLedgerEntry entry, CancellationToken ct = default)
        {
            var callNumber = Interlocked.Increment(ref _upsertCallCount);
            if (callNumber == _throwOnUpsertCallNumber)
            {
                throw new InvalidOperationException(
                    $"Simulated run-ledger persistence failure on upsert call #{callNumber}");
            }

            return _inner.UpsertRunLedgerAsync(entry, ct);
        }

        public Task<RunLedgerEntry?> LoadRunLedgerAsync(string runId, CancellationToken ct = default)
            => _inner.LoadRunLedgerAsync(runId, ct);

        public Task<IReadOnlyList<RunLedgerEntry>> ListRunLedgerAsync(string threadId, CancellationToken ct = default)
            => _inner.ListRunLedgerAsync(threadId, ct);

        public Task RecordAcceptedInputAsync(string threadId, string inputId, DateTimeOffset acceptedAt, CancellationToken ct = default)
            => _inner.RecordAcceptedInputAsync(threadId, inputId, acceptedAt, ct);

        public Task RemoveAcceptedInputAsync(string threadId, string inputId, CancellationToken ct = default)
            => _inner.RemoveAcceptedInputAsync(threadId, inputId, ct);

        public Task<IReadOnlySet<string>> ListAcceptedInputIdsAsync(string threadId, CancellationToken ct = default)
            => _inner.ListAcceptedInputIdsAsync(threadId, ct);

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

    /// <summary>
    /// Decorates <see cref="InMemoryConversationStore"/>, letting the first two
    /// <see cref="UpsertRunLedgerAsync"/> calls (StartRunAsync's Queued + InProgress writes)
    /// through immediately, then signalling <see cref="TerminalUpsertStarted"/> and gating the
    /// THIRD call (the terminal write) on <see cref="ReleaseTerminalUpsert"/> so a test can
    /// deterministically observe "CompleteRunAsync has claimed the terminal outcome but not yet
    /// finished persisting it".
    /// </summary>
    private sealed class GatingTerminalUpsertLedgerStore : IConversationStore, IRunLedgerStore
    {
        private readonly InMemoryConversationStore _inner = new();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _upsertCallCount;

        public TaskCompletionSource TerminalUpsertStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseTerminalUpsert() => _release.TrySetResult();

        public async Task UpsertRunLedgerAsync(RunLedgerEntry entry, CancellationToken ct = default)
        {
            var callNumber = Interlocked.Increment(ref _upsertCallCount);
            if (callNumber == 3)
            {
                TerminalUpsertStarted.TrySetResult();
                await _release.Task.WaitAsync(ct);
            }

            await _inner.UpsertRunLedgerAsync(entry, ct);
        }

        public Task<RunLedgerEntry?> LoadRunLedgerAsync(string runId, CancellationToken ct = default)
            => _inner.LoadRunLedgerAsync(runId, ct);

        public Task<IReadOnlyList<RunLedgerEntry>> ListRunLedgerAsync(string threadId, CancellationToken ct = default)
            => _inner.ListRunLedgerAsync(threadId, ct);

        public Task RecordAcceptedInputAsync(string threadId, string inputId, DateTimeOffset acceptedAt, CancellationToken ct = default)
            => _inner.RecordAcceptedInputAsync(threadId, inputId, acceptedAt, ct);

        public Task RemoveAcceptedInputAsync(string threadId, string inputId, CancellationToken ct = default)
            => _inner.RemoveAcceptedInputAsync(threadId, inputId, ct);

        public Task<IReadOnlySet<string>> ListAcceptedInputIdsAsync(string threadId, CancellationToken ct = default)
            => _inner.ListAcceptedInputIdsAsync(threadId, ct);

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

    /// <summary>
    /// Minimal concrete <see cref="MultiTurnAgentBase"/> whose loop starts one run per drained
    /// batch and completes it via a single natural <c>CompleteRunAsync</c> call (no turn
    /// execution, no error/cancellation branches) — isolating the natural-completion-vs-Stop race
    /// from any provider/turn-execution behavior.
    /// </summary>
    private sealed class NaturalCompletionOnlyAgent : MultiTurnAgentBase
    {
        public NaturalCompletionOnlyAgent(string threadId, IConversationStore store)
            : base(threadId, store: store, persistRunLedger: true)
        {
        }

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

                await CompleteRunAsync(assignment.RunId, assignment.GenerationId, ct: ct);
            }
        }
    }
}
