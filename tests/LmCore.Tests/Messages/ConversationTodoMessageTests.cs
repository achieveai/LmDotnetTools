using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Models;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Messages;

/// <summary>
///     Coverage for the live todo-board push frame (#583, PR 2): the flat-on-message wire shape with the
///     <c>conversation_todo</c> discriminator, the camelCase-pinned task rows, the enum-name status
///     strings, and the snapshot → frame flattening — all serialized through the production message
///     converter with NO naming policy, because that is exactly what the WebSocket channel uses. A test
///     that passed only under a camelCase policy would prove nothing about the wire.
/// </summary>
public class ConversationTodoMessageTests
{
    private static TodoBoardSnapshot BuildSnapshot(string threadId = "conv-1", int schemaVersion = 1)
    {
        return new TodoBoardSnapshot
        {
            ThreadId = threadId,
            SchemaVersion = schemaVersion,
            CapturedAtUtc = new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero),
            Tasks =
            [
                new TodoTaskNode
                {
                    Id = "1",
                    Status = TodoTaskStatus.InProgress,
                    Title = "Wire the SSE endpoint",
                    Notes = ["waiting on schema"],
                    SubTasks =
                    [
                        new TodoTaskNode
                        {
                            Id = "1.1",
                            Status = TodoTaskStatus.Completed,
                            Title = "Add the map",
                        },
                    ],
                },
                new TodoTaskNode
                {
                    Id = "2",
                    Status = TodoTaskStatus.NotStarted,
                    Title = "Vitest coverage",
                },
            ],
        };
    }

    [Fact]
    public void FromSnapshot_StampsCallerThreadId_NotTheSnapshotsOwn()
    {
        // The publishing loop's id wins, exactly as ConversationUsageMessage.FromAggregate stamps the
        // usage frame. Mutation that must go red: FromSnapshot copying snapshot.ThreadId instead of the
        // parameter.
        var snapshot = BuildSnapshot(threadId: "some-other-thread");

        var frame = ConversationTodoMessage.FromSnapshot(snapshot, "loop-thread");

        Assert.Equal("loop-thread", frame.ThreadId);
    }

    [Fact]
    public void FromSnapshot_CarriesSchemaVersionThrough_Unchanged()
    {
        // SchemaVersion is load-bearing on the read path (newer reads as absent), so the frame must not
        // claim a different one. A non-default version in the fixture is what makes the "always stamp 1"
        // mutation go red rather than pass vacuously.
        var frame = ConversationTodoMessage.FromSnapshot(BuildSnapshot(schemaVersion: 3), "conv-1");

        Assert.Equal(3, frame.SchemaVersion);
    }

    [Fact]
    public void FromSnapshot_CarriesTasksAndCaptureInstant()
    {
        var snapshot = BuildSnapshot();

        var frame = ConversationTodoMessage.FromSnapshot(snapshot, "conv-1");

        Assert.Same(snapshot.Tasks, frame.Tasks);
        Assert.Equal(snapshot.CapturedAtUtc, frame.CapturedAtUtc);
    }

    [Fact]
    public void IsTransient_SoItNeverEntersHistoryReplayOrPersistence()
    {
        Assert.True(typeof(ITransientMessage).IsAssignableFrom(typeof(ConversationTodoMessage)));
    }

    [Fact]
    public void Serialize_AsIMessage_FlatFields_ConversationTodoDiscriminator_NoWrapperProperty()
    {
        IMessage frame = ConversationTodoMessage.FromSnapshot(BuildSnapshot(), "conv-1");
        var options = JsonSerializerOptionsFactory.CreateForProduction();

        var json = JsonSerializer.Serialize(frame, options);
        var root = JsonDocument.Parse(json).RootElement;

        Assert.Equal("conversation_todo", root.GetProperty("$type").GetString());

        // Snapshot fields sit FLAT on the message — the client parses them at the root, and a nested
        // wrapper property (e.g. "board" / "snapshot") is exactly the shape drift the pinned contract
        // forbids.
        Assert.Equal("conv-1", root.GetProperty("threadId").GetString());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.True(root.TryGetProperty("capturedAtUtc", out _));
        Assert.Equal(JsonValueKind.Array, root.GetProperty("tasks").ValueKind);
        Assert.False(root.TryGetProperty("board", out _));
        Assert.False(root.TryGetProperty("snapshot", out _));
    }

    [Fact]
    public void Serialize_TaskRows_AreCamelCase_WithEnumNameStatus_EvenWithoutANamingPolicy()
    {
        // CreateForProduction applies NO naming policy, so every one of these names is proven to come
        // from the [JsonPropertyName] pins on TodoTaskNode — remove a pin and its assertion goes red
        // with the PascalCase name in its place.
        IMessage frame = ConversationTodoMessage.FromSnapshot(BuildSnapshot(), "conv-1");
        var options = JsonSerializerOptionsFactory.CreateForProduction();

        var json = JsonSerializer.Serialize(frame, options);
        var task = JsonDocument.Parse(json).RootElement.GetProperty("tasks")[0];

        Assert.Equal("1", task.GetProperty("id").GetString());
        Assert.Equal("InProgress", task.GetProperty("status").GetString());
        Assert.Equal("Wire the SSE endpoint", task.GetProperty("title").GetString());
        Assert.Equal("waiting on schema", task.GetProperty("notes")[0].GetString());

        var subTask = task.GetProperty("subTasks")[0];
        Assert.Equal("1.1", subTask.GetProperty("id").GetString());
        Assert.Equal("Completed", subTask.GetProperty("status").GetString());
    }

    [Fact]
    public void Serialize_LeafRow_StillCarriesEmptyNotesAndSubTasks_NeverNull()
    {
        // The client indexes into notes/subTasks without a null guard; the contract is empty array,
        // never null, never omitted.
        IMessage frame = ConversationTodoMessage.FromSnapshot(BuildSnapshot(), "conv-1");
        var options = JsonSerializerOptionsFactory.CreateForProduction();

        var json = JsonSerializer.Serialize(frame, options);
        var leaf = JsonDocument.Parse(json).RootElement.GetProperty("tasks")[1];

        Assert.Equal(JsonValueKind.Array, leaf.GetProperty("notes").ValueKind);
        Assert.Equal(0, leaf.GetProperty("notes").GetArrayLength());
        Assert.Equal(JsonValueKind.Array, leaf.GetProperty("subTasks").ValueKind);
        Assert.Equal(0, leaf.GetProperty("subTasks").GetArrayLength());
    }

    [Fact]
    public void RoundTrips_ThroughIMessageConverter()
    {
        IMessage original = ConversationTodoMessage.FromSnapshot(BuildSnapshot(), "conv-1");
        var options = JsonSerializerOptionsFactory.CreateForProduction();

        var json = JsonSerializer.Serialize(original, options);
        var restored = JsonSerializer.Deserialize<IMessage>(json, options);

        var frame = Assert.IsType<ConversationTodoMessage>(restored);
        Assert.Equal("conv-1", frame.ThreadId);
        Assert.Equal(2, frame.Tasks.Count);
        Assert.Equal("1", frame.Tasks[0].Id);
        Assert.Equal(TodoTaskStatus.InProgress, frame.Tasks[0].Status);
        Assert.Equal("Add the map", frame.Tasks[0].SubTasks[0].Title);
        Assert.Equal(TodoTaskStatus.NotStarted, frame.Tasks[1].Status);
    }
}
