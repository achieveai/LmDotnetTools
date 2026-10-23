using System.Collections.Immutable;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.Sqlite;
using TodoEval.Runner.Metrics;
using TodoEval.Runner.Sweep;

namespace TodoEval.Runner.Tests;

/// <summary>
/// The host keeps conversations in conversations.db, while the metrics, the redactor and every
/// archived baseline read the conversations/ folder layout. The harvest export joins the two, so it
/// must lose nothing the metrics read, and a host that left no store must fail the harvest.
/// </summary>
public class ConversationDbExportTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), $"todo-eval-export-{Guid.NewGuid():N}");

    public void Dispose()
    {
        SqliteConnectionFactory.ClearPool(Path.Combine(_temp, "conversations.db"));
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    // The fixtures captured from a real host. coordination-run is hand-written without the envelope
    // ids a store needs, so it cannot stand in for one.
    [Theory]
    [InlineData("complete-run")]
    [InlineData("no-tools-subagent")]
    [InlineData("retry-storm")]
    public async Task Export_of_a_hosts_database_reads_exactly_like_the_folder_it_came_from(string fixture)
    {
        var original = RepoPaths.FixtureConversations(fixture);
        var database = Path.Combine(_temp, "conversations.db");
        await ImportAsync(original, database);

        var exported = Path.Combine(_temp, "export");
        var count = await ConversationDbExport.ExportAsync(database, exported, CancellationToken.None);

        count.Should().Be(Directory.GetDirectories(original).Length);
        var expected = ConversationStoreReader.LoadAllThreads(original);
        expected.Should().NotBeEmpty("the fixture must exercise the reader");
        JsonSerializer
            .Serialize(ConversationStoreReader.LoadAllThreads(exported))
            .Should()
            .Be(JsonSerializer.Serialize(expected));
    }

    [Fact]
    public async Task Export_keeps_the_metadata_that_groups_sub_agents_and_holds_the_board()
    {
        // No captured fixture has metadata, yet the sub-agent parent link and the board live only there.
        var database = Path.Combine(_temp, "conversations.db");
        Directory.CreateDirectory(_temp);
        await using (var sqlite = new SqliteConversationStore(database))
        {
            foreach (
                var (threadId, key, value) in new[]
                {
                    ("thread-root", "todo.board", """{"rows":[]}"""),
                    ("subagent-root-0001", "sample.subAgentOf", "thread-root"),
                }
            )
            {
                await sqlite.AppendMessagesAsync(threadId, [Message(threadId)]);
                await sqlite.SaveMetadataAsync(
                    threadId,
                    new ThreadMetadata
                    {
                        ThreadId = threadId,
                        LastUpdated = 1,
                        Properties = ImmutableDictionary<string, object>.Empty.Add(key, value),
                    }
                );
            }
        }

        var exported = Path.Combine(_temp, "export");
        await ConversationDbExport.ExportAsync(database, exported, CancellationToken.None);

        var threads = ConversationStoreReader.LoadAllThreads(exported).ToDictionary(t => t.ThreadId);
        threads["subagent-root-0001"].ParentThreadId.Should().Be("thread-root");
        threads["thread-root"].TodoBoardJson.Should().Be("""{"rows":[]}""");
    }

    [Fact]
    public async Task Export_fails_loudly_when_the_host_left_no_database()
    {
        var export = () =>
            ConversationDbExport.ExportAsync(
                Path.Combine(_temp, "conversations.db"),
                Path.Combine(_temp, "export"),
                CancellationToken.None
            );

        await export.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public void A_fresh_instance_never_copies_a_conversation_database_or_its_sidecars()
    {
        var publish = Path.Combine(_temp, "publish");
        Directory.CreateDirectory(publish);
        string[] state = ["conversations.db", "conversations.db-wal", "conversations.db-shm", "notify-waits.db-wal"];
        foreach (var name in state.Append("LmStreaming.Sample.dll"))
        {
            File.WriteAllText(Path.Combine(publish, name), name);
        }

        var instance = Path.Combine(_temp, "instance");
        Directory.CreateDirectory(instance);
        EvalHostProcess.CopyDirectory(publish, instance);

        Directory.GetFiles(instance).Select(Path.GetFileName).Should().BeEquivalentTo(["LmStreaming.Sample.dll"]);
    }

    // Loads a committed folder-layout fixture into a SQLite store, the way the host would have written it.
    private static async Task ImportAsync(string conversationsDir, string database)
    {
        var copy = database + ".source";
        CopyTree(conversationsDir, copy);
        var files = new FileConversationStore(copy);
        await using var sqlite = new SqliteConversationStore(database);
        foreach (var threadId in Directory.GetDirectories(copy).Select(Path.GetFileName))
        {
            await sqlite.AppendMessagesAsync(threadId!, await files.LoadMessagesAsync(threadId!));
            if (await files.LoadMetadataAsync(threadId!) is { } metadata)
            {
                await sqlite.SaveMetadataAsync(threadId!, metadata);
            }
        }
    }

    private static PersistedMessage Message(string threadId) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            ThreadId = threadId,
            RunId = "run-1",
            GenerationId = "g1",
            Timestamp = 1,
            MessageType = "TextMessage",
            Role = "Assistant",
            MessageJson = """{"$type":"text","text":"done"}""",
        };

    private static void CopyTree(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
