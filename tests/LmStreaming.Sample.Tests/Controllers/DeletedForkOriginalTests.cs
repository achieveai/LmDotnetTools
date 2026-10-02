using System.Collections.Immutable;
using System.Text;
using AchieveAi.LmDotnetTools.LmCore.Identity;
using LmStreaming.Sample.Identity;
using LmStreaming.Sample.Tests.TestDoubles;
using Microsoft.AspNetCore.Http;

namespace LmStreaming.Sample.Tests.Controllers;

/// <summary>
/// A deleted original that forks still read is kept in the store, hidden. To the user it is gone:
/// no route may read it or write to it, only its forks read its shared messages.
/// </summary>
/// <remarks>
/// Found by hand: after deleting an original, a stale tab's chat socket still ran a turn in it, and
/// its messages still loaded - writes landed in a conversation no list showed.
/// </remarks>
public sealed class DeletedForkOriginalTests
{
    private const string Source = "thread-source";

    private readonly InMemoryConversationStore _store = new();

    [Fact]
    public async Task ADeletedOriginal_IsUnknownToItsRoutes_WhileItsForkStillReadsIt()
    {
        await using var pool = ConversationsControllerTests.CreatePool();
        var controller = ConversationsControllerTests.CreateController(
            _store,
            pool,
            ConversationsControllerTests.ModeStoreResolvingSystemModes()
        );
        var fork = await SeedDeletedOriginalWithForkAsync(controller);

        Status(await controller.GetMessages(Source)).Should().Be((404, "unknown_thread"));
        Status(await controller.SendMessage(Source, new SendMessageRequest { Text = "after delete" }))
            .Should()
            .Be((404, "unknown_thread"));
        Status(await controller.UpdateMetadata(Source, new ConversationMetadataUpdate { Title = "back" }))
            .Should()
            .Be((404, "unknown_thread"));
        Status(await controller.GetBranches(Source)).Should().Be((404, "unknown_thread"));
        Status(await controller.GetStatus(Source, runId: "run-1")).Should().Be((404, "unknown_thread"));
        Status(await controller.RequestCompaction(Source, request: null)).Should().Be((404, "unknown_thread"));

        (await _store.LoadMessagesAsync(Source)).Should().HaveCount(2, "nothing was written to it");
        Assert.IsType<OkObjectResult>(await controller.GetMessages(fork));
    }

    /// <summary>The chat socket is where the write actually happened, and it gates with enforcement off.</summary>
    [Fact]
    public async Task TheChatSocket_RefusesADeletedOriginal_EvenWithAuthorizationOff()
    {
        await using var pool = ConversationsControllerTests.CreatePool();
        _ = await SeedDeletedOriginalWithForkAsync(
            ConversationsControllerTests.CreateController(
                _store,
                pool,
                ConversationsControllerTests.ModeStoreResolvingSystemModes()
            )
        );
        var gate = new WebSocketConversationGate(
            TestAuthorizers.Disabled(),
            _store,
            NullLogger<WebSocketConversationGate>.Instance
        );

        var refused = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        var admitted = await gate.AdmitAsync(refused, Source, AccessAction.Write);

        admitted.Should().BeFalse();
        refused.Response.StatusCode.Should().Be(404);
        refused.Response.Headers[IdentityMiddleware.RefusalCodeHeader].ToString().Should().Be("unknown_thread");
        Encoding
            .UTF8.GetString(((MemoryStream)refused.Response.Body).ToArray())
            .Should()
            .Be(JsonSerializer.Serialize(UnknownThreadRefusal.Body(Source)));

        (await gate.AdmitAsync(new DefaultHttpContext(), "thread-new", AccessAction.Write))
            .Should()
            .BeTrue("a new conversation's first socket still opens it");
    }

    /// <summary>Source <c>u1 a1</c> (run-1), forked after run-1, then the source deleted.</summary>
    private async Task<string> SeedDeletedOriginalWithForkAsync(ConversationsController controller)
    {
        await _store.SaveMetadataAsync(
            Source,
            new ThreadMetadata
            {
                ThreadId = Source,
                LastUpdated = 1,
                Properties = ImmutableDictionary<string, object>
                    .Empty.Add("title", "Plan")
                    .Add(MultiTurnAgentPool.ProviderPropertyKey, "test"),
            }
        );
        foreach (var (id, role) in new[] { ("u1", Role.User), ("a1", Role.Assistant) })
        {
            var row = MessagePersistenceConverter.ToPersistedMessage(
                new TextMessage
                {
                    Text = id,
                    Role = role,
                    RunId = "run-1",
                },
                Source,
                "run-1"
            ) with
            {
                Id = id,
            };
            await _store.AppendMessagesAsync(Source, [row]);
        }

        var forked = await controller.Fork(Source, new ForkConversationRequest { AfterRunId = "run-1" });
        var fork = ((ForkConversationResponse)Assert.IsType<ObjectResult>(forked).Value!).ThreadId;
        (await controller.Delete(Source)).Should().BeOfType<NoContentResult>();
        (await _store.LoadMetadataAsync(Source)).Should().NotBeNull("the fork keeps it");
        return fork;
    }

    private static (int Status, string? Code) Status(IActionResult result)
    {
        var refused = Assert.IsAssignableFrom<ObjectResult>(result);
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(refused.Value));
        return (
            refused.StatusCode ?? 0,
            body.RootElement.ValueKind == JsonValueKind.Object && body.RootElement.TryGetProperty("code", out var code)
                ? code.GetString()
                : null
        );
    }
}
