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

        var runCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var msg in loop.SubscribeAsync(cts.Token))
                {
                    if (msg is RunCompletedMessage)
                    {
                        runCompleted.TrySetResult(true);
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await manual.Sinks[waitId].FireAsync(new TriggerFireEvent("fire-after-restore"), cts.Token);
        await runCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await cts.CancelAsync();

        // AddToHistory persists fire-and-forget, so give the write a brief window to land.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        IReadOnlyList<IMessage> history = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            history = MessagePersistenceConverter.FromPersistedMessages(
                await convStore.LoadMessagesAsync(threadId));
            if (history.OfType<TextMessage>().Any(
                m => m.Role == Role.User && m.Text.Contains("<trigger>")))
            {
                break;
            }
            await Task.Delay(50);
        }

        history.OfType<TextMessage>().Should().Contain(
            m => m.Role == Role.User && m.Text.Contains("<trigger>") && m.Text.Contains("fire-after-restore"));
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
