using LmStreaming.Sample.Tests.Agents;
using LmStreaming.Sample.Tests.TestDoubles;

namespace LmStreaming.Sample.Tests.Controllers;

public sealed class ActionToolSuppressionTests
{
    [Theory]
    [InlineData("code-review-daemon", true)]
    [InlineData("codereview-daemon-mcqdb", true)]
    [InlineData("unknown", false)]
    [InlineData(null, false)]
    public async Task Publication_preflight_requires_the_request_callers_configured_route(string? appId, bool supported)
    {
        var store = new InMemoryConversationStore();
        await using var pool = new MultiTurnAgentPool(
            _ => new MultiTurnAgentPool.AgentCreationResult(new SpawnSuppressingFakeAgent("thread")),
            providerRegistry: null,
            conversationStore: store,
            NullLogger<MultiTurnAgentPool>.Instance
        );
        var controller = ConversationsControllerTests.CreateController(
            store,
            pool,
            ConversationsControllerTests.ModeStoreResolvingSystemModes(),
            workflowPublication: new LmStreaming.Sample.Configuration.WorkflowPublicationOptions
            {
                CallbackUrl = "https://legacy/publication",
                SharedSecret = "legacy-secret",
                Callbacks = new()
                {
                    ["code-review-daemon"] = new()
                    {
                        CallbackUrl = "https://primary/publication",
                        SharedSecret = "primary-secret",
                    },
                    ["codereview-daemon-mcqdb"] = new()
                    {
                        CallbackUrl = "https://ado/publication",
                        SharedSecret = "ado-secret",
                    },
                },
            }
        );
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext(),
        };
        if (appId is not null)
            controller.Request.Headers["X-Sbx-App-Id"] = appId;
        var result = Assert.IsType<ConversationCapabilitiesResponse>(
            Assert.IsType<OkObjectResult>(controller.GetCapabilities(modeId: "code-review-daemon")).Value
        );
        result.WorkflowPublication.Should().Be(supported);
    }

    [Theory]
    [InlineData("test", true)]
    [InlineData("claude", false)]
    public async Task Publication_provider_grant_excludes_cli_conversations(string provider, bool supported)
    {
        var store = new InMemoryConversationStore();
        await using var pool = new MultiTurnAgentPool(
            _ => new MultiTurnAgentPool.AgentCreationResult(new SpawnSuppressingFakeAgent("thread")),
            providerRegistry: null,
            conversationStore: store,
            NullLogger<MultiTurnAgentPool>.Instance
        );
        var controller = ConversationsControllerTests.CreateController(
            store,
            pool,
            ConversationsControllerTests.ModeStoreResolvingSystemModes(),
            providerRegistry: new FakeProviderRegistry(
                defaultProviderId: "test",
                available: ["test", "claude"]
            ).ToReal(),
            workflowPublication: new LmStreaming.Sample.Configuration.WorkflowPublicationOptions
            {
                CallbackUrl = "https://daemon/api/workflow/publication",
                SharedSecret = "secret",
            }
        );
        var result = Assert.IsType<ConversationCapabilitiesResponse>(
            Assert.IsType<OkObjectResult>(controller.GetCapabilities(provider, "code-review-daemon")).Value
        );
        result.WorkflowPublication.Should().Be(supported);
        result.WorkflowPublicationProviderId.Should().Be(supported ? provider : null);
        result.WorkflowPublicationModeId.Should().Be(supported ? "code-review-daemon" : null);
        var otherMode = Assert.IsType<ConversationCapabilitiesResponse>(
            Assert.IsType<OkObjectResult>(controller.GetCapabilities(provider, "workspace-agent")).Value
        );
        otherMode.WorkflowPublication.Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Publication_capability_requires_configured_forwarding(bool configured)
    {
        var store = new InMemoryConversationStore();
        await using var pool = new MultiTurnAgentPool(
            _ => new MultiTurnAgentPool.AgentCreationResult(new SpawnSuppressingFakeAgent("thread")),
            providerRegistry: null,
            conversationStore: store,
            NullLogger<MultiTurnAgentPool>.Instance
        );
        var options = new LmStreaming.Sample.Configuration.WorkflowPublicationOptions
        {
            CallbackUrl = "https://daemon/api/workflow/publication",
            SharedSecret = configured ? "test-secret" : null,
        };
        var controller = ConversationsControllerTests.CreateController(
            store,
            pool,
            ConversationsControllerTests.ModeStoreResolvingSystemModes(),
            workflowPublication: options
        );
        var result = Assert.IsType<ConversationCapabilitiesResponse>(
            Assert.IsType<OkObjectResult>(controller.GetCapabilities()).Value
        );
        result.WorkflowPublication.Should().Be(configured);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Durable_receipt_is_authoritative_on_first_send_and_duplicate(bool confirms)
    {
        const string thread = "thread-correction";
        var store = new InMemoryConversationStore();
        await store.SaveMetadataAsync(thread, new ThreadMetadata { ThreadId = thread, LastUpdated = 0 });
        var agent = new SpawnSuppressingFakeAgent(thread) { ConfirmsActionToolSuppression = confirms };
        await using var pool = new MultiTurnAgentPool(
            _ => new MultiTurnAgentPool.AgentCreationResult(agent),
            providerRegistry: null,
            conversationStore: store,
            NullLogger<MultiTurnAgentPool>.Instance
        );
        var controller = ConversationsControllerTests.CreateController(
            store,
            pool,
            ConversationsControllerTests.ModeStoreResolvingSystemModes()
        );
        var request = new SendMessageRequest
        {
            Text = "correct JSON",
            IdempotencyKey = "correction-1",
            SuppressActionTools = true,
        };
        var first = Assert.IsType<SendMessageResponse>(
            Assert.IsType<AcceptedResult>(await controller.SendMessage(thread, request)).Value
        );
        var repeated = Assert.IsType<SendMessageResponse>(
            Assert.IsType<AcceptedResult>(await controller.SendMessage(thread, request)).Value
        );
        agent.LastInput!.SuppressActionTools.Should().BeTrue();
        first.ActionToolsSuppressed.Should().Be(confirms);
        repeated.ActionToolsSuppressed.Should().Be(confirms);
        repeated.InputId.Should().Be(first.InputId);
        agent.SendCount.Should().Be(1);
        first.InputId.Should().Be(IdempotentInputId.Create("correction-1", false, true));
        (await store.GetAcceptanceAsync(thread, first.InputId))!.ActionToolsSuppressed.Should().Be(confirms);
    }

    [Fact]
    public async Task Unsupported_conversation_is_refused_before_enqueue()
    {
        const string thread = "thread-unsupported";
        var store = new InMemoryConversationStore();
        await store.SaveMetadataAsync(thread, new ThreadMetadata { ThreadId = thread, LastUpdated = 0 });
        var agent = new SpawnSuppressingFakeAgent(thread) { EnforcesActionToolSuppression = false };
        await using var pool = new MultiTurnAgentPool(
            _ => new MultiTurnAgentPool.AgentCreationResult(agent),
            providerRegistry: null,
            conversationStore: store,
            NullLogger<MultiTurnAgentPool>.Instance
        );
        var controller = ConversationsControllerTests.CreateController(
            store,
            pool,
            ConversationsControllerTests.ModeStoreResolvingSystemModes()
        );
        var response = await controller.SendMessage(
            thread,
            new SendMessageRequest { Text = "correct", SuppressActionTools = true }
        );
        Assert.IsType<BadRequestObjectResult>(response);
        agent.SendCount.Should().Be(0);
    }
}
