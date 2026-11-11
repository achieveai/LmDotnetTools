namespace LmStreaming.Sample.Tests.Controllers;

/// <summary>A replayable reasoning blob stays on the server: the history a browser reloads never carries it.</summary>
public class ConversationsControllerReasoningBlobTests
{
    private const string ThreadId = "thread-blob";

    [Fact]
    public async Task History_omits_tagged_reasoning_row()
    {
        var store = new InMemoryConversationStore();
        await store.AppendMessagesAsync(
            ThreadId,
            [
                MessagePersistenceConverter.ToPersistedMessage(
                    new TextMessage { Text = "hello", Role = Role.User },
                    ThreadId,
                    "run-1"
                ),
                MessagePersistenceConverter.ToPersistedMessage(
                    new ReasoningMessage
                    {
                        Reasoning = "BLOB",
                        Visibility = ReasoningVisibility.Encrypted,
                        Role = Role.Assistant,
                        EncryptedOrigin = "responses:m",
                    },
                    ThreadId,
                    "run-1"
                ),
                MessagePersistenceConverter.ToPersistedMessage(
                    new TextMessage { Text = "done", Role = Role.Assistant },
                    ThreadId,
                    "run-1"
                ),
            ]
        );
        await using var pool = ConversationsControllerTests.CreatePool();
        var controller = ConversationsControllerTests.CreateController(
            store,
            pool,
            ConversationsControllerTests.ModeStoreResolvingSystemModes()
        );

        var result = await controller.GetMessages(ThreadId);

        var rows = result
            .Should()
            .BeOfType<OkObjectResult>()
            .Which.Value.Should()
            .BeAssignableTo<IEnumerable<PersistedMessage>>()
            .Subject.ToList();
        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(r => !r.MessageJson.Contains("BLOB"));

        // Hidden from the browser, not deleted: the next request still needs it.
        (await store.LoadMessagesAsync(ThreadId))
            .Should()
            .HaveCount(3);
    }
}
