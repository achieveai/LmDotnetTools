using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.Sqlite;
using AchieveAi.LmDotnetTools.LmTestUtils.Persistence;
using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// The conversation's record of its sandboxes lives in its metadata beside every other property, written by other
/// writers at any time. Over the production store (SQLite) and the file store the composition tests run on.
/// </summary>
public sealed class ConversationOwnedSandboxesTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "lmstreaming-owned-sandboxes",
        Guid.NewGuid().ToString("N")
    );

    public ConversationOwnedSandboxesTests() => _ = Directory.CreateDirectory(_root);

    public void Dispose() => DetachedStoreTeardown.Purge(_root);

    /// <summary>
    /// A record write replaces only its own key: writers of other properties racing it keep theirs, and it keeps all
    /// of its own.
    /// </summary>
    [Theory]
    [InlineData("sqlite")]
    [InlineData("file")]
    public async Task Record_writes_racing_other_property_writers_keep_every_property_and_every_record(string kind)
    {
        var store = CreateStore(kind);
        try
        {
            var records = new ConversationOwnedSandboxes(store);
            const string threadId = "review-1";
            await store.UpdateMetadataAsync(
                threadId,
                _ => new ThreadMetadata
                {
                    ThreadId = threadId,
                    LastUpdated = 0,
                    Properties = ImmutableDictionary<string, object>.Empty.Add("title", "Review r-7"),
                }
            );

            const int writers = 20;
            await Task.WhenAll(
                Enumerable
                    .Range(0, writers)
                    .Select(i =>
                        i % 2 == 0
                            ? records.UpdateAsync(threadId, current => [.. current, Record($"sess-{i}")])
                            : store.UpdateMetadataAsync(
                                threadId,
                                existing =>
                                    existing! with
                                    {
                                        Properties = existing.Properties!.SetItem($"other.{i}", $"value-{i}"),
                                    }
                            )
                    )
                    .Select(write => Task.Run(() => write))
            );

            var metadata = (await store.LoadMetadataAsync(threadId))!;
            metadata.Properties!["title"].ToString().Should().Be("Review r-7");
            for (var i = 1; i < writers; i += 2)
            {
                metadata.Properties.Should().ContainKey($"other.{i}");
            }

            (await records.LoadAsync(threadId))
                .Select(record => record.SessionId)
                .Should()
                .BeEquivalentTo(Enumerable.Range(0, writers).Where(i => i % 2 == 0).Select(i => $"sess-{i}"));
        }
        finally
        {
            await DisposeAsync(store);
        }
    }

    /// <summary>A record write for a conversation that is gone writes nothing: it never brings one back.</summary>
    [Theory]
    [InlineData("sqlite")]
    [InlineData("file")]
    public async Task A_record_write_for_a_deleted_conversation_does_not_bring_it_back(string kind)
    {
        var store = CreateStore(kind);
        try
        {
            var records = new ConversationOwnedSandboxes(store);

            await records.UpdateAsync("gone", current => [.. current, Record("sess-1")]);

            (await store.LoadMetadataAsync("gone")).Should().BeNull();
        }
        finally
        {
            await DisposeAsync(store);
        }
    }

    /// <summary>
    /// A record write that already read the conversation, racing its delete over the production composition (the
    /// index over SQLite): the delete waits for the write, then removes the conversation. It never comes back.
    /// </summary>
    [Fact]
    public async Task A_record_write_that_read_the_conversation_before_its_delete_does_not_bring_it_back()
    {
        var sqlite = new SqliteConversationStore(Path.Combine(_root, "conversations.db"));
        try
        {
            var store = new IndexedConversationStore(sqlite, Path.Combine(_root, "conversations.index.json"));
            var records = new ConversationOwnedSandboxes(store);
            const string threadId = "review-1";
            await store.UpdateMetadataAsync(threadId, _ => new ThreadMetadata { ThreadId = threadId, LastUpdated = 0 });
            var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var hold = new ManualResetEventSlim();

            var write = Task.Run(() =>
                records.UpdateAsync(
                    threadId,
                    current =>
                    {
                        read.SetResult();
                        hold.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("the test releases the write");
                        return [.. current, Record("sess-1")];
                    }
                )
            );
            await read.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var delete = Task.Run(() => store.DeleteThreadAsync(threadId));
            hold.Set();

            // Separately: the delete queues behind the write, so neither may wait on the other's completion here.
            await write;
            await delete;

            (await store.LoadMetadataAsync(threadId)).Should().BeNull();
            (await records.LoadAsync(threadId)).Should().BeEmpty();
        }
        finally
        {
            await DisposeAsync(sqlite);
        }
    }

    /// <summary>
    /// A sandbox two conversations record is listed once for each, so neither owner is hidden; a record repeated
    /// within one conversation is listed once. Records on another workspace or under another app are left out.
    /// </summary>
    [Theory]
    [InlineData("sqlite")]
    [InlineData("file")]
    public async Task A_sandbox_two_conversations_record_is_listed_for_each_and_once_per_conversation(string kind)
    {
        var store = CreateStore(kind);
        try
        {
            var records = new ConversationOwnedSandboxes(store);
            foreach (var threadId in new[] { "review-a", "review-b" })
            {
                await store.UpdateMetadataAsync(
                    threadId,
                    _ => new ThreadMetadata { ThreadId = threadId, LastUpdated = 0 }
                );
            }

            await records.UpdateAsync(
                "review-a",
                _ =>
                    [
                        Record("sess-shared"),
                        Record("sess-a"),
                        Record("sess-shared") with
                        {
                            Retired = true,
                        },
                        Record("sess-elsewhere") with
                        {
                            GatewayWorkspace = "other",
                        },
                        Record("sess-other-app") with
                        {
                            AppId = "app-b",
                        },
                    ]
            );
            await records.UpdateAsync("review-b", _ => [Record("sess-shared")]);

            var listed = await records.ListOnWorkspaceAsync("reviews", "app-a");

            (string ThreadId, string SessionId)[] expected =
            [
                ("review-a", "sess-shared"),
                ("review-a", "sess-a"),
                ("review-b", "sess-shared"),
            ];
            listed.Select(entry => (entry.ThreadId, entry.Record.SessionId)).Should().BeEquivalentTo(expected);
        }
        finally
        {
            await DisposeAsync(store);
        }
    }

    private static OwnedSandboxRecord Record(string sessionId) =>
        new(sessionId, "ws-id", "reviews", "app-a", ReadOnlyEgress: false, "PRs/r-7", Retired: false);

    private IConversationStore CreateStore(string kind) =>
        kind == "sqlite"
            ? new SqliteConversationStore(Path.Combine(_root, "conversations.db"))
            : new FileConversationStore(Path.Combine(_root, "conversations"));

    private async Task DisposeAsync(IConversationStore store)
    {
        if (store is SqliteConversationStore sqlite)
        {
            await sqlite.DisposeAsync();
            // Releases this file's pooled handles, so the teardown can take the directory.
            SqliteConnectionFactory.ClearPool(Path.Combine(_root, "conversations.db"));
        }
    }
}
