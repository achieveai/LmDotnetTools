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
        var saver = new OrderedRowSaver(store.Object, Thread, NullLogger.Instance);

        _ = saver.Enqueue(Row("call"));
        await saver.DrainAsync().WaitAsync(Limit);
        _ = saver.Enqueue(Row("result"));
        await saver.DrainAsync().WaitAsync(Limit);

        (await StoredIdsAsync(store.Object)).Should().BeEmpty("nothing may be stored ahead of the failed call");
        saver.Waiting.Should().Be(2);

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
        var saver = new OrderedRowSaver(store, Thread, NullLogger.Instance);

        // A tool result's id comes from its call id, so a second copy is refused by the primary key for good.
        _ = saver.Enqueue(Row("tcr:duplicate"));
        _ = saver.Enqueue(Row("later"));
        await saver.DrainAsync().WaitAsync(Limit);

        (await StoredIdsAsync(store)).Should().Equal("tcr:duplicate", "later");
        saver.Waiting.Should().Be(0);
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
    public async Task Flush_WhenTheStoreStaysLocked_ReportsTheRowsAtError_AndReturns()
    {
        var store = new ScriptedStore((_, _) => Task.FromException(new InvalidOperationException("locked")));
        var logger = new Mock<ILogger>();
        var saver = new OrderedRowSaver(store.Object, Thread, logger.Object);
        _ = saver.Enqueue(Row("call"));

        await saver.FlushAsync().WaitAsync(Limit);

        store.Attempts("call").Should().BeGreaterThan(1, "disposal is the last chance, so it tries more than once");
        saver.Waiting.Should().Be(1);
        logger.Verify(
            l =>
                l.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.IsAny<It.IsAnyType>(),
                    It.IsAny<Exception?>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()
                ),
            Times.Once
        );
    }

    /// <summary>An in-memory store whose saves can be held open or failed, row by row.</summary>
    private sealed class ScriptedStore
    {
        private readonly InMemoryConversationStore _inner = new();
        private readonly Func<PersistedMessage, int, Task?> _script;
        private readonly TaskCompletionSource _firstAttempt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Dictionary<string, int> _attempts = new(StringComparer.Ordinal);
        private int _running;

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

        public IConversationStore Object { get; }

        public Task FirstAttemptStarted => _firstAttempt.Task;

        public int MaxConcurrent { get; private set; }

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
}
