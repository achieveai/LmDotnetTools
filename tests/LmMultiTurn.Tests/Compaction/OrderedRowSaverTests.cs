using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using LmMultiTurn.Tests.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests.Compaction;

public class OrderedRowSaverTests
{
    /// <summary>An in-memory store whose saves can be held open or failed, row by row.</summary>
    private sealed class ScriptedStore
    {
        private readonly InMemoryConversationStore _inner = new();
        private readonly Func<PersistedMessage, int, Task?> _script;
        private readonly TaskCompletionSource _firstAttempt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Dictionary<string, int> _attempts = new(StringComparer.Ordinal);
        private int _running;

        public IConversationStore Object { get; }

        public Task FirstAttemptStarted => _firstAttempt.Task;

        public int MaxConcurrent { get; private set; }

        public ScriptedStore(Func<PersistedMessage, int, Task?> script)
        {
            _script = script;
            var mock = new Mock<IConversationStore>();
            _ = mock.Setup(s =>
                    s.AppendMessagesAsync(
                        It.IsAny<string>(),
                        It.IsAny<IReadOnlyList<PersistedMessage>>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .Returns<string, IReadOnlyList<PersistedMessage>, CancellationToken>(AppendAsync);
            _ = mock.Setup(s => s.LoadMessagesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<string, CancellationToken>((thread, ct) => _inner.LoadMessagesAsync(thread, ct));
            Object = mock.Object;
        }

        public int Attempts(string id)
        {
            lock (_attempts)
            {
                return _attempts.GetValueOrDefault(id);
            }
        }

        private async Task AppendAsync(string threadId, IReadOnlyList<PersistedMessage> messages, CancellationToken ct)
        {
            int attempt;
            lock (_attempts)
            {
                attempt = _attempts.GetValueOrDefault(messages[0].Id) + 1;
                _attempts[messages[0].Id] = attempt;
                _running++;
                MaxConcurrent = Math.Max(MaxConcurrent, _running);
            }

            _ = _firstAttempt.TrySetResult();
            try
            {
                if (_script(messages[0], attempt) is { } gate)
                {
                    await gate;
                }

                await _inner.AppendMessagesAsync(threadId, messages, ct);
            }
            finally
            {
                lock (_attempts)
                {
                    _running--;
                }
            }
        }
    }

    /// <summary>Keeps every log entry with its level and rendered text, and can act on one as it is logged.</summary>
    private sealed class RecordingLogger(Action<LogLevel>? onLog = null) : ILogger
    {
        private readonly List<(LogLevel Level, string Text)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Text)> Entries
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries];
                }
            }
        }

        public IReadOnlyList<string> At(LogLevel level) =>
            [.. Entries.Where(e => e.Level == level).Select(e => e.Text)];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            lock (_entries)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }

            onLog?.Invoke(logLevel);
        }
    }

    private const string Thread = "thread-saver";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    private static PersistedMessage Row(string id) => ConversationStoreHarness.Row(Thread, id, timestamp: 1);

    private static async Task<List<string>> StoredIdsAsync(IConversationStore store) =>
        [.. (await store.LoadMessagesAsync(Thread)).OrderBy(r => r.Seq).Select(r => r.Id)];

    [Fact]
    public async Task OverlappingSaves_WithAFailingCall_NeverStoreTheResultBeforeItsCall()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ScriptedStore(
            (row, attempt) =>
                row.Id != "call" ? null
                : attempt == 1 ? release.Task
                : attempt == 2 ? Task.FromException(new InvalidOperationException("still locked"))
                : null
        );
        var saver = new OrderedRowSaver(store.Object, Thread, NullLogger.Instance);

        // The call's save is still running when its result is issued: the overlap fire-and-forget saves have.
        var callSaved = saver.Enqueue(Row("call"));
        var resultSaved = saver.Enqueue(Row("result"));
        await store.FirstAttemptStarted.WaitAsync(Limit);

        // Taken while the first attempt is held open: it sees that attempt fail, then its own retry fail.
        var firstPass = saver.DrainAsync();
        release.SetException(new InvalidOperationException("SQLite Error 5: 'database is locked'."));
        await firstPass.WaitAsync(Limit);

        (await StoredIdsAsync(store.Object)).Should().BeEmpty("the result must wait behind its failed call");

        await saver.DrainAsync().WaitAsync(Limit);

        await Task.WhenAll(callSaved, resultSaved).WaitAsync(Limit);
        (await StoredIdsAsync(store.Object)).Should().Equal("call", "result");
        store.MaxConcurrent.Should().Be(1);
    }

    [Fact]
    public async Task ARowThatKeepsFailing_StaysFirst_AndIsWrittenFirstOnceTheStoreRecovers()
    {
        var failing = true;
        var store = new ScriptedStore(
            (row, _) => row.Id == "call" && failing ? Task.FromException(new InvalidOperationException("locked")) : null
        );
        var logger = new RecordingLogger();
        var saver = new OrderedRowSaver(store.Object, Thread, logger);

        _ = saver.Enqueue(Row("call"));
        await saver.DrainAsync().WaitAsync(Limit);
        _ = saver.Enqueue(Row("result"));
        await saver.DrainAsync().WaitAsync(Limit);

        (await StoredIdsAsync(store.Object)).Should().BeEmpty("nothing may be stored ahead of the failed call");
        saver.Waiting.Should().Be(2);
        logger
            .At(LogLevel.Warning)
            .Should()
            .Contain(
                "Could not save row call for thread thread-saver; it stays first, with 1 later rows behind it",
                "a failed save that is not a duplicate is a Warning naming the rows held behind it"
            );
        logger.At(LogLevel.Information).Should().BeEmpty();

        failing = false;
        await saver.DrainAsync().WaitAsync(Limit);

        (await StoredIdsAsync(store.Object)).Should().Equal("call", "result");
        saver.Waiting.Should().Be(0);
    }

    [Fact]
    public async Task ARowAlreadyStored_DoesNotBlockTheRowsBehindIt_OnSqlite()
    {
        await using var stores = new ConversationStoreHarness();
        var store = stores.Open("sqlite");
        await store.AppendMessagesAsync(Thread, [Row("tcr:duplicate")]);
        var logger = new RecordingLogger();
        var saver = new OrderedRowSaver(store, Thread, logger);

        // A tool result's id comes from its call id, so a second copy is refused by the primary key for good.
        _ = saver.Enqueue(Row("tcr:duplicate"));
        _ = saver.Enqueue(Row("later"));
        await saver.DrainAsync().WaitAsync(Limit);

        (await StoredIdsAsync(store)).Should().Equal("tcr:duplicate", "later");
        saver.Waiting.Should().Be(0);
        logger
            .At(LogLevel.Information)
            .Should()
            .Equal("Row tcr:duplicate for thread thread-saver is already stored; treating its failed save as done");
        logger.At(LogLevel.Warning).Should().BeEmpty("a duplicate is not a failure");
    }

    [Fact]
    public async Task ADifferentRowUnderTheSameId_IsNotTakenAsThisRowsSave_OnSqlite()
    {
        await using var stores = new ConversationStoreHarness();
        var store = stores.Open("sqlite");
        await store.AppendMessagesAsync(Thread, [Row("tcr:reused") with { MessageJson = """{"text":"first"}""" }]);
        var logger = new RecordingLogger();
        var saver = new OrderedRowSaver(store, Thread, logger);

        // A reused tool-call id puts a different result under an id that is already taken.
        var act = () =>
            saver.SaveNowAsync(
                Row("tcr:reused") with
                {
                    MessageJson = """{"text":"second"}""",
                },
                CancellationToken.None
            );

        await act.Should().ThrowAsync<Exception>();
        saver.Waiting.Should().Be(0, "the caller unwinds the row, so it is withdrawn");
        logger.At(LogLevel.Warning).Should().NotBeEmpty();
        logger.At(LogLevel.Information).Should().BeEmpty("the stored row is not this one");
        (await store.LoadMessagesAsync(Thread)).Single().MessageJson.Should().Be("""{"text":"first"}""");
    }

    [Fact]
    public async Task SaveNow_WhenTheRowAheadStillFails_ThrowsAndWithdrawsItsRow()
    {
        var store = new ScriptedStore(
            (row, _) => row.Id == "call" ? Task.FromException(new InvalidOperationException("locked")) : null
        );
        var saver = new OrderedRowSaver(store.Object, Thread, NullLogger.Instance);
        _ = saver.Enqueue(Row("call"));

        var act = () => saver.SaveNowAsync(Row("placeholder"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("locked");
        saver.Waiting.Should().Be(1, "the caller unwinds the placeholder, so it must not be written later");
        (await StoredIdsAsync(store.Object)).Should().BeEmpty();
    }

    [Fact]
    public async Task SaveNow_CancelledWhileQueuedBehindAHeldAppend_WithdrawsItsRow()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ScriptedStore((row, attempt) => row.Id == "call" && attempt == 1 ? release.Task : null);
        var saver = new OrderedRowSaver(store.Object, Thread, NullLogger.Instance);
        _ = saver.Enqueue(Row("call"));
        await store.FirstAttemptStarted.WaitAsync(Limit);
        using var cancel = new CancellationTokenSource();

        var save = saver.SaveNowAsync(Row("placeholder"), cancel.Token);
        await cancel.CancelAsync();

        await save.Invoking(s => s.WaitAsync(Limit)).Should().ThrowAsync<OperationCanceledException>();
        release.SetResult();
        await saver.DrainAsync().WaitAsync(Limit);
        (await StoredIdsAsync(store.Object)).Should().Equal(["call"], "the caller unwound the placeholder");
        store.Attempts("placeholder").Should().Be(0);
    }

    [Fact]
    public async Task SaveNow_CancelledWhileItsOwnAppendRuns_ReturnsOnceThatAppendCommits()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ScriptedStore((row, attempt) => row.Id == "placeholder" && attempt == 1 ? release.Task : null);
        var saver = new OrderedRowSaver(store.Object, Thread, NullLogger.Instance);
        using var cancel = new CancellationTokenSource();

        var save = saver.SaveNowAsync(Row("placeholder"), cancel.Token);
        await store.FirstAttemptStarted.WaitAsync(Limit);
        await cancel.CancelAsync();
        release.SetResult();

        // The append committed, so reporting a cancellation would make the caller unwind a durable row.
        await save.WaitAsync(Limit);
        (await StoredIdsAsync(store.Object)).Should().Equal("placeholder");
        saver.Waiting.Should().Be(0);
    }

    [Fact]
    public async Task SaveNow_CancelledWhileItsAppendFails_WaitsForTheReplacementPumpWritingItAgain()
    {
        var firstAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAttemptStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ScriptedStore(
            (row, attempt) =>
            {
                if (row.Id != "placeholder")
                {
                    return null;
                }

                if (attempt == 1)
                {
                    return firstAttempt.Task;
                }

                _ = secondAttemptStarted.TrySetResult();
                return secondAttempt.Task;
            }
        );
        OrderedRowSaver? saver = null;
        Task? laterSaved = null;
        var logger = new RecordingLogger(level =>
        {
            // Runs on the failing pump after it let go of the queue: a row issued now starts a new pump, and
            // that pump takes the placeholder again before the cancelled caller looks at the queue.
            if (level == LogLevel.Warning && laterSaved is null)
            {
                laterSaved = saver!.Enqueue(Row("later"));
                secondAttemptStarted.Task.Wait(Limit);
            }
        });
        saver = new OrderedRowSaver(store.Object, Thread, logger);
        using var cancel = new CancellationTokenSource();
        try
        {
            var save = saver.SaveNowAsync(Row("placeholder"), cancel.Token);
            await store.FirstAttemptStarted.WaitAsync(Limit);
            cancel.Cancel();
            firstAttempt.SetException(new InvalidOperationException("locked"));
            await secondAttemptStarted.Task.WaitAsync(Limit);
            secondAttempt.SetResult();

            // The second append committed the placeholder, so the caller must keep it, and the row behind it
            // must not be taken off the queue in its place.
            await save.WaitAsync(Limit);
            await laterSaved!.WaitAsync(Limit);
            (await StoredIdsAsync(store.Object)).Should().Equal("placeholder", "later");
            saver.Waiting.Should().Be(0);
        }
        finally
        {
            firstAttempt.TrySetResult();
            secondAttempt.TrySetResult();
        }
    }

    [Fact]
    public async Task ARowLeavingTheQueue_IsAlreadyDurable_ForAnyoneWhoSeesItGone()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ScriptedStore((_, _) => release.Task);
        var saver = new OrderedRowSaver(store.Object, Thread, NullLogger.Instance);
        var saved = saver.Enqueue(Row("call"));
        Task<bool>? reader = null;
        saver.RowLeftQueueForTest = () =>
        {
            // A reader on another thread, between the row leaving the queue and its waiters being told. It
            // must not be able to see the queue empty while the row still reads as not durable.
            reader = Task.Run(() => saver.Waiting == 0 && saved.IsCompleted);
            _ = reader.Wait(TimeSpan.FromMilliseconds(500));
        };

        release.SetResult();

        await saved.WaitAsync(Limit);
        (await reader!.WaitAsync(Limit)).Should().BeTrue();
    }

    [Fact]
    public async Task SaveNow_ReturnsOnceItsRowIsDurable_WithoutWaitingForRowsQueuedAfterIt()
    {
        var placeholderGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var laterGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ScriptedStore(
            (row, _) =>
                row.Id == "placeholder" ? placeholderGate.Task
                : row.Id == "later" ? laterGate.Task
                : null
        );
        var saver = new OrderedRowSaver(store.Object, Thread, NullLogger.Instance);
        try
        {
            var save = saver.SaveNowAsync(Row("placeholder"), CancellationToken.None);
            await store.FirstAttemptStarted.WaitAsync(Limit);
            var laterSaved = saver.Enqueue(Row("later"));
            placeholderGate.SetResult();

            await save.WaitAsync(Limit);
            laterSaved.IsCompleted.Should().BeFalse("the later row's append is still held");
            (await StoredIdsAsync(store.Object)).Should().Equal("placeholder");
        }
        finally
        {
            laterGate.TrySetResult();
        }
    }

    [Fact]
    public async Task Flush_WhenTheStoreStaysLocked_ReportsTheRowsAtError_AndReturns()
    {
        var store = new ScriptedStore((_, _) => Task.FromException(new InvalidOperationException("locked")));
        var logger = new RecordingLogger();
        var saver = new OrderedRowSaver(store.Object, Thread, logger);
        _ = saver.Enqueue(Row("call"));

        await saver.FlushAsync().WaitAsync(Limit);

        store.Attempts("call").Should().BeGreaterThan(1, "disposal is the last chance, so it tries more than once");
        saver.Waiting.Should().Be(1);
        logger
            .At(LogLevel.Error)
            .Should()
            .Equal("1 rows for thread thread-saver could not be saved before shutdown; the first is call");
    }

    [Fact]
    public async Task Flush_WhenAnAppendNeverReturns_StopsAtItsBudget_AndReportsTheRows()
    {
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ScriptedStore((_, _) => held.Task);
        var logger = new RecordingLogger();
        var saver = new OrderedRowSaver(store.Object, Thread, logger, flushBudget: TimeSpan.FromMilliseconds(300));
        try
        {
            _ = saver.Enqueue(Row("call"));
            _ = saver.Enqueue(Row("later"));
            await store.FirstAttemptStarted.WaitAsync(Limit);

            // Disposal must end even when the store hangs: the budget, not the append, decides when it stops.
            await saver.FlushAsync().WaitAsync(Limit);

            saver.Waiting.Should().Be(2);
            logger
                .At(LogLevel.Error)
                .Should()
                .Equal("2 rows for thread thread-saver could not be saved before shutdown; the first is call");
        }
        finally
        {
            held.TrySetResult();
        }
    }
}
