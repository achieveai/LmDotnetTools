using AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;

namespace LmStreaming.Sample.Tests.Controllers;

/// <summary>
/// The experimental elapsed-time notice is persisted for the model but must never reach the browser.
/// The live stream already skips it (the loop never publishes a user-role text); this covers the other
/// path, the reload through <c>GET /api/conversations/{threadId}/messages</c>.
/// </summary>
public class ConversationsControllerElapsedTimeNoticeTests
{
    private const string ThreadId = "thread-elapsed";

    [Fact]
    public async Task GetMessages_HidesTheElapsedTimeNotice_AndKeepsEverythingElse()
    {
        var store = new InMemoryConversationStore();
        var notice = ElapsedTimeNotice.Build(
            TimeSpan.FromSeconds(75),
            new DateTimeOffset(2026, 9, 15, 14, 3, 34, TimeSpan.Zero)
        );
        await store.AppendMessagesAsync(
            ThreadId,
            [
                MessagePersistenceConverter.ToPersistedMessage(
                    new TextMessage { Text = "hello", Role = Role.User },
                    ThreadId,
                    "run-1"
                ),
                MessagePersistenceConverter.ToPersistedMessage(
                    new TextMessage { Text = "working on it", Role = Role.Assistant },
                    ThreadId,
                    "run-1"
                ),
                MessagePersistenceConverter.ToPersistedMessage(notice, ThreadId, "run-1"),
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
        rows.Should().HaveCount(3, "only the notice is hidden");
        rows.Select(r => JsonDocument.Parse(r.MessageJson).RootElement.GetProperty("text").GetString())
            .Should()
            .Equal("hello", "working on it", "done");
        rows.Should().OnlyContain(r => !r.MessageJson.Contains(ElapsedTimeNotice.MetadataKey));

        // The row itself is still in the store: hidden from the browser, not deleted from the conversation.
        var persisted = await store.LoadMessagesAsync(ThreadId);
        persisted.Should().HaveCount(4);
    }
}
