using AchieveAi.LmDotnetTools.LmTestUtils;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests.Triggers;

/// <summary>
/// Task 14: <see cref="MultiTurnAgentLoop"/>'s recovery path must call
/// <see cref="TriggerRuntime.RestoreNotifyWaitsAsync"/> via <c>OnThreadRecoveredAsync</c>
/// regardless of whether the thread has any persisted message rows, so a thread rehydrated after
/// a restart re-arms any notify-mode waits that were persisted to its <see cref="INotifyWaitStore"/>.
/// Notify waits aren't backed by a deferred tool-call placeholder in history — they live in their
/// own store, keyed only by thread — so this restore path is entirely separate from the
/// block-wait one already covered by <see cref="DeferredToolExecutionTests"/> (which does depend
/// on message history via <c>OnHistoryRestoredAsync</c>).
/// Reuses the shared notify-capable <see cref="ManualTriggerSource"/> fake (see
/// <see cref="NotifyEnvelopeDeliveryTests"/>) and a Dictionary-backed <see cref="INotifyWaitStore"/>
/// double (no SQLite needed here — see <see cref="TriggerRuntimeNotifyRestoreTests"/> for the
/// runtime-level restore coverage this loop-level test builds on).
/// </summary>
public class NotifyRestoreLoopTests
{
    private readonly Mock<IStreamingAgent> _mockAgent = new();
    private readonly Mock<ILogger<MultiTurnAgentLoop>> _loggerMock = new();

    [Fact]
    public async Task RestoredThread_WithNoMessages_ReArmsPersistedNotifyWaits()
    {
        const string threadId = "notify-restore-thread";
        const string waitId = "w1";
        const string runId = "run_prev";

        // Deliberately seed ZERO message rows: notify_waits are persisted in their own store,
        // keyed only by thread, independent of conversation history. A thread can have an active
        // notify wait with no messages at all (e.g. armed on the very first turn), so recovery of
        // the wait must not be gated on persisted messages being non-empty.
        var convStore = new InMemoryConversationStore();
        await convStore.SaveMetadataAsync(threadId, new ThreadMetadata
        {
            ThreadId = threadId,
            LatestRunId = runId,
            LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

        // Seed an active, restorable notify row as if a previous process armed it and the
        // process then restarted before it fired.
        var notifyStore = new InMemoryNotifyWaitStore();
        await notifyStore.SaveAsync(new NotifyWaitRecord(
            waitId,
            threadId,
            "manual-restorable",
            "{}",
            null,
            null,
            0,
            DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            "active"));

        var manual = new ManualTriggerSource();
        var options = new TriggerOptions
        {
            NotifyWaitStore = notifyStore,
            ThreadId = threadId,
            AdditionalRegistrations =
            [
                new TriggerSourceRegistration
                {
                    Kind = "manual-restorable",
                    Description = "test notify source (restorable)",
                    ArgsSchema = "{}",
                    Capabilities = new TriggerCapabilities(SupportsBlock: true, SupportsNotify: true, SupportsRestore: true),
                    Source = manual,
                },
            ],
        };

        var finalText = new TextMessage { Text = "handled the fire", Role = Role.Assistant };
        _mockAgent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (_, _, _) => Task.FromResult(ToAsyncEnumerable([finalText])));

        await using var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            new FunctionRegistry(),
            threadId,
            store: convStore,
            logger: _loggerMock.Object,
            triggerOptions: options);

        // Act: drive the restore path directly (mirrors DeferredToolExecutionTests' pattern of
        // calling RecoverAsync without ever starting RunAsync first). With zero persisted
        // messages, RecoverAsync's own return value stays false (unchanged, message-count-driven
        // semantics) — the point of this test is that notify-wait restore still runs on this path.
        var recovered = await loop.RecoverAsync();
        recovered.Should().BeFalse();

        // Assert (1): the runtime re-armed the wait — the source has a live sink registered
        // under the *original* wait id, which only happens via RestoreNotifyWaitsAsync's
        // ArmCoreAsync call (nothing else in restore touches the notify-wait store).
        manual.Sinks.Should().ContainKey(waitId);

        // Assert (2): the re-armed wait actually delivers. Fire it and confirm the loop injects
        // a fresh <trigger>-tagged user turn and completes a new run — exactly as a live-armed
        // notify wait would (see NotifyEnvelopeDeliveryTests).
        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        // The subscription must be LIVE before the fire. SubscribeAsync registers the subscriber
        // synchronously — under the replay lock, ahead of the iterator's first suspension point —
        // so issuing (deliberately not awaiting) the first move is what makes it live. Registering
        // on a scheduled continuation instead races the run to completion, and RunCompletedMessage
        // closes the replay buffer: a subscriber that joins after it sees nothing at all and waits
        // forever. Under whole-solution thread-pool load that race is what this test used to lose.
        var subscription = loop.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var firstMove = subscription.MoveNextAsync();

        await manual.Sinks[waitId].FireAsync(new TriggerFireEvent("fire-after-restore"), cts.Token);

        var sawRunCompleted = await DrainToRunCompletedAsync(subscription, firstMove)
            .WaitAsync(TimeSpan.FromSeconds(5));
        sawRunCompleted.Should().BeTrue();

        // No move is in flight here, so disposal is safe and unregisters the subscriber.
        await subscription.DisposeAsync();
        await cts.CancelAsync();

        // AddToHistory persists fire-and-forget, so the write lands after the run completes. Loud: a
        // deadline that fell through silently reported the absent write as a missing <trigger>
        // envelope, which is the same red as a restored loop that never delivered the fire at all.
        IReadOnlyList<IMessage> history = [];
        await Wait.UntilAsync(
            async () =>
            {
                history = MessagePersistenceConverter.FromPersistedMessages(
                    await convStore.LoadMessagesAsync(threadId));
                return history.OfType<TextMessage>().Any(
                    m => m.Role == Role.User && m.Text.Contains("<trigger>"));
            },
            "the fire-and-forget AddToHistory write put the restored loop's <trigger> envelope into "
                + "persisted history",
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(50));

        history.OfType<TextMessage>().Should().Contain(
            m => m.Role == Role.User && m.Text.Contains("<trigger>") && m.Text.Contains("fire-after-restore"));
    }

    /// <summary>
    /// Drains an already-established subscription until a <see cref="RunCompletedMessage"/> arrives.
    /// The first move is passed in rather than issued here because that move is what registered the
    /// subscriber: it has to be issued before whatever starts the run, or the run can complete —
    /// and close the replay buffer — before anyone is listening.
    /// </summary>
    private static async Task<bool> DrainToRunCompletedAsync(
        IAsyncEnumerator<IMessage> subscription,
        ValueTask<bool> firstMove)
    {
        for (var move = firstMove; await move; move = subscription.MoveNextAsync())
        {
            if (subscription.Current is RunCompletedMessage)
            {
                return true;
            }
        }

        return false;
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

    /// <summary>
    /// PR #158 review fix: recovery must never block on the loop's bounded input channel.
    /// <see cref="TriggerRuntime.RestoreNotifyWaitsAsync"/> delivers a terminal envelope
    /// (<c>trigger_lost_on_restart</c>) for every unregistered/non-restorable notify row it finds.
    /// Before the fix, that delivery went through the loop's BLOCKING
    /// <c>EnqueueTriggerNotifyAsync</c> (<c>TryWrite</c> falling back to <c>WriteAsync</c>). With
    /// more terminal rows to restore than the channel's capacity, and recovery running here
    /// (deliberately) BEFORE <c>RunAsync</c> starts the reader, the channel fills and the fallback
    /// <c>WriteAsync</c> would hang forever waiting for a reader that never starts — a startup
    /// deadlock. The fix threads a non-blocking <c>TryEnqueueTriggerNotify</c> delegate through
    /// <see cref="TriggerRuntime"/> for restore-time delivery only: a row is deleted only once its
    /// envelope was actually accepted into the channel, and rows that don't fit are retained
    /// (not lost) for redelivery on the next recovery.
    /// </summary>
    [Fact]
    public async Task RestoreNotifyWaits_ChannelSmallerThanTerminalRows_CompletesWithoutBlocking_AndRetainsRejectedRows()
    {
        const string threadId = "notify-restore-overflow-thread";
        const int terminalRowCount = 3;
        const int channelCapacity = 1; // smaller than terminalRowCount by design

        var convStore = new InMemoryConversationStore();
        await convStore.SaveMetadataAsync(threadId, new ThreadMetadata
        {
            ThreadId = threadId,
            LatestRunId = "run_prev",
            LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

        // Seed more terminal (unregistered-kind) notify_waits rows than the input channel can
        // hold. Each is non-restorable, so RestoreNotifyWaitsAsync attempts to deliver one
        // trigger_lost_on_restart envelope per row.
        var notifyStore = new InMemoryNotifyWaitStore();
        for (var i = 0; i < terminalRowCount; i++)
        {
            await notifyStore.SaveAsync(new NotifyWaitRecord(
                $"w{i}",
                threadId,
                "no-such-kind", // deliberately unregistered -> always non-restorable/terminal
                "{}",
                null,
                null,
                0,
                DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                "active"));
        }

        var options = new TriggerOptions
        {
            NotifyWaitStore = notifyStore,
            ThreadId = threadId,
        };

        await using var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            new FunctionRegistry(),
            threadId,
            store: convStore,
            logger: _loggerMock.Object,
            inputChannelCapacity: channelCapacity,
            triggerOptions: options);

        // Act: drive recovery directly WITHOUT ever starting RunAsync, so nothing drains the
        // bounded input channel while RestoreNotifyWaitsAsync is delivering terminal envelopes.
        // Bound the wait so a regression fails as a timeout instead of hanging the whole suite.
        var recovered = await loop.RecoverAsync().WaitAsync(TimeSpan.FromSeconds(5));
        recovered.Should().BeFalse(); // zero message rows for this thread

        var remainingRows = await notifyStore.LoadActiveAsync(threadId);

        // Exactly `channelCapacity` envelopes fit and were accepted -> those rows are deleted.
        // The rest could not be enqueued without blocking, so they must be retained for
        // redelivery on the next recovery rather than silently lost.
        remainingRows.Should().HaveCount(
            terminalRowCount - channelCapacity,
            "rows whose envelope could not be enqueued without blocking must be retained, not dropped");
    }

    /// <summary>Simple in-memory <see cref="INotifyWaitStore"/> test double — no SQLite needed here.</summary>
    private sealed class InMemoryNotifyWaitStore : INotifyWaitStore
    {
        private readonly ConcurrentDictionary<string, NotifyWaitRecord> _rows = new(StringComparer.Ordinal);

        public Task SaveAsync(NotifyWaitRecord record, CancellationToken ct = default)
        {
            _rows[record.WaitId] = record;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string threadId, string waitId, CancellationToken ct = default)
        {
            _rows.TryRemove(waitId, out _);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<NotifyWaitRecord>> LoadActiveAsync(string threadId, CancellationToken ct = default)
        {
            IReadOnlyList<NotifyWaitRecord> result =
                [.. _rows.Values.Where(r => r.ThreadId == threadId && r.Status == "active")];
            return Task.FromResult(result);
        }
    }
}
