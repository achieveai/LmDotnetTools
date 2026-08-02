using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Tests.Agents;
using LmStreaming.Sample.Tests.TestDoubles;
using Microsoft.AspNetCore.Http;

namespace LmStreaming.Sample.Tests.Controllers;

/// <summary>
/// Security tests for the RAW thread routes — <c>GET</c>/<c>POST /api/conversations/{threadId}/messages</c>
/// — when the thread is one an AGENT owns (<c>subagent-*</c>/<c>workflow-*</c>) rather than a conversation
/// a human started (#244).
/// </summary>
/// <remarks>
/// <para>
/// These routes predate collaboration and read/write a transcript by id alone. That makes them the obvious
/// way around <see cref="ConversationsController.GetAgentTranscript"/>: an agent denied by the policy could
/// simply ask the raw route for the same thread and get the whole transcript, reasoning included. The guard
/// is therefore about WHO is asking — a caller presenting an agent or service identity is sent to the
/// checked route; the conversation's own browser client, which presents none, keeps the exact legacy read
/// its sub-agent tab has always made.
/// </para>
/// <para>
/// The write side has no such split: nothing may speak into an agent's thread through this route, so it is
/// refused outright. Ordinary root conversations must be untouched on both verbs, which is asserted here
/// too — a guard that quietly narrowed the primary chat surface would be a worse bug than the hole it closes.
/// </para>
/// </remarks>
public sealed class AgentOwnedThreadRouteTests
{
    private const string SubAgentThread = "subagent-alpha";
    private const string WorkflowThread = "workflow-w1-thread-root";
    private const string RootThread = "thread-root";

    /// <summary>The same options the controller normalizes persisted messages with.</summary>
    private static readonly JsonSerializerOptions MessageJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new IMessageJsonConverter() },
    };

    [Theory]
    [InlineData(SubAgentThread)]
    [InlineData(WorkflowThread)]
    public async Task GetMessages_RefusesAnAgentOwnedThread_ForACallerThatNamesItsAgent(string threadId)
    {
        // The bypass: a viewer that GetAgentTranscript would evaluate (and possibly refuse) asks the raw
        // route for the same transcript instead. Naming a viewer is what makes a caller an agent, so this
        // read never happens here — it happens on the route that applies the policy.
        var store = await StoreWithTranscriptAsync(threadId);
        await using var pool = CreateFakeAgentPool();
        var controller = CreateController(store, pool);

        var result = await controller.GetMessages(threadId, viewer: "alpha");

        AssertForbidden(result, ConversationsController.AgentOwnedThreadReadCode);
    }

    [Theory]
    [InlineData(InboundS2SAuthAttribute.HeaderName)]
    [InlineData(SandboxCredential.AppIdHeader)]
    public async Task GetMessages_RefusesAnAgentOwnedThread_ForAServiceCaller(string header)
    {
        // A machine caller does not have to name a viewer to be one: presenting a service/caller-credential
        // header already says "I am not the conversation's browser". Both markers are the same ones the
        // inbound S2S guard keys off, so the two agree on what a service request looks like.
        var store = await StoreWithTranscriptAsync(SubAgentThread);
        await using var pool = CreateFakeAgentPool();
        var controller = CreateController(store, pool);
        SetRequestHeaders(controller, new Dictionary<string, string> { [header] = "anything" });

        var result = await controller.GetMessages(SubAgentThread);

        AssertForbidden(result, ConversationsController.AgentOwnedThreadReadCode);
    }

    [Fact]
    public async Task GetMessages_StillServesAnAgentOwnedThread_ToTheConversationsOwnClient()
    {
        // The sub-agent tab loads its history from exactly this call, with no viewer and no headers, and
        // renders the child's thinking. Narrowing it would break the panel and silently drop reasoning from
        // a reloaded transcript that the live socket shows — so the human's path stays byte-for-byte legacy.
        var store = await StoreWithTranscriptAsync(SubAgentThread);
        await using var pool = CreateFakeAgentPool();
        var controller = CreateController(store, pool);

        var result = await controller.GetMessages(SubAgentThread);

        var ok = Assert.IsType<OkObjectResult>(result);
        var messages = Assert.IsAssignableFrom<IReadOnlyList<PersistedMessage>>(ok.Value);
        messages.Should().HaveCount(2);
        messages.Select(m => m.MessageType).Should().Contain(
            nameof(ReasoningMessage),
            "the owning client's view of its own sub-agent is unchanged, reasoning included");
    }

    [Fact]
    public async Task GetMessages_LeavesAnOrdinaryConversationOpen_ToEveryCaller()
    {
        // Legacy root routes are explicitly out of scope for the guard: a headless S2S client naming a
        // viewer still reads a root conversation exactly as before.
        var store = await StoreWithTranscriptAsync(RootThread);
        await using var pool = CreateFakeAgentPool();
        var controller = CreateController(store, pool);
        SetRequestHeaders(
            controller,
            new Dictionary<string, string> { [InboundS2SAuthAttribute.HeaderName] = "secret" });

        var result = await controller.GetMessages(RootThread, viewer: "alpha");

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.IsAssignableFrom<IReadOnlyList<PersistedMessage>>(ok.Value).Should().HaveCount(2);
    }

    [Theory]
    [InlineData(SubAgentThread)]
    [InlineData(WorkflowThread)]
    public async Task SendMessage_RefusesAnAgentOwnedThread(string threadId)
    {
        // Posting here would put words in another agent's transcript AND, because the pool creates an agent
        // for whatever thread id it is handed, stand up a top-level agent bound to that transcript. The
        // refusal comes before either can happen, which is why the pool below is never allowed to create.
        var store = new InMemoryConversationStore();
        await SeedThreadMetadataAsync(store, threadId);
        await using var pool = CreateForbiddenAgentPool();
        var controller = CreateController(store, pool);

        var result = await controller.SendMessage(threadId, new SendMessageRequest { Text = "hello" });

        AssertForbidden(result, ConversationsController.AgentOwnedThreadWriteCode);
    }

    [Fact]
    public async Task SendMessage_StillReportsAnUnknownOrdinaryConversation()
    {
        // Proves the write guard did not swallow the ordinary path: a root thread id keeps reaching the
        // existing metadata lookup and its 404, rather than being refused by prefix.
        await using var pool = CreateForbiddenAgentPool();
        var controller = CreateController(new InMemoryConversationStore(), pool);

        var result = await controller.SendMessage("thread-that-never-existed", new SendMessageRequest { Text = "hi" });

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        JsonSerializer.Serialize(notFound.Value).Should().Contain("unknown_thread");
    }

    [Fact]
    public async Task GetStatus_RefusesAnAgentOwnedThread_ForAServiceCaller()
    {
        // GET /status is the OTHER way to read an agent's words: its body carries the run's final response
        // TEXT. A machine caller reading it would get exactly what GetAgentTranscript exists to gate,
        // without ever being evaluated by the policy.
        var store = new InMemoryConversationStore();
        await SeedThreadMetadataAsync(store, SubAgentThread);
        await using var pool = CreateFakeAgentPool();
        var controller = CreateController(store, pool);
        SetRequestHeaders(
            controller,
            new Dictionary<string, string> { [InboundS2SAuthAttribute.HeaderName] = "secret" });

        var result = await controller.GetStatus(SubAgentThread, runId: "run-1");

        AssertForbidden(result, ConversationsController.AgentOwnedThreadReadCode);
    }

    [Fact]
    public async Task GetStatus_StillServesAnAgentOwnedThread_ToTheConversationsOwnClient()
    {
        // No identity presented ⇒ the browser path, unchanged: the request reaches the existing argument
        // validation instead of being refused. (Neither id supplied is the route's own 400.)
        var store = new InMemoryConversationStore();
        await SeedThreadMetadataAsync(store, SubAgentThread);
        await using var pool = CreateFakeAgentPool();
        var controller = CreateController(store, pool);

        var result = await controller.GetStatus(SubAgentThread);

        _ = Assert.IsType<BadRequestObjectResult>(result);
    }

    /// <summary>
    /// The mutating raw routes. None of them is reachable by id alone for a caller holding an agent or
    /// service identity: renaming, deleting, or re-provisioning another agent's thread is not something
    /// the collaboration policy has any way to evaluate, so the answer is simply no.
    /// </summary>
    [Theory]
    [InlineData("metadata")]
    [InlineData("delete")]
    [InlineData("mode")]
    [InlineData("provider")]
    public async Task Mutation_RefusesAnAgentOwnedThread_ForAServiceCaller(string route)
    {
        var store = new InMemoryConversationStore();
        await SeedThreadMetadataAsync(store, SubAgentThread);
        await using var pool = CreateFakeAgentPool();
        var controller = CreateController(store, pool);
        SetRequestHeaders(
            controller,
            new Dictionary<string, string> { [SandboxCredential.AppIdHeader] = "some-caller" });

        var result = route switch
        {
            "metadata" => await controller.UpdateMetadata(
                SubAgentThread, new ConversationMetadataUpdate { Title = "mine now" }),
            "delete" => await controller.Delete(SubAgentThread),
            "mode" => await controller.SwitchMode(
                SubAgentThread, new SwitchModeRequest { ModeId = SystemChatModes.DefaultModeId }),
            _ => await controller.SwitchProvider(
                SubAgentThread, new SwitchProviderRequest { ProviderId = "test" }),
        };

        AssertForbidden(result, ConversationsController.AgentOwnedThreadWriteCode);

        // The refusal is a refusal, not a partial edit: the thread is exactly as it was.
        (await store.LoadMetadataAsync(SubAgentThread)).Should().NotBeNull();
    }

    [Fact]
    public async Task Delete_StillRemovesAnOrdinaryConversation_ForAServiceCaller()
    {
        // The guard is scoped to agent-owned threads: a headless client's housekeeping on a root
        // conversation is untouched, which is the whole reason it keys off the thread id and not the header.
        var store = new InMemoryConversationStore();
        await SeedThreadMetadataAsync(store, RootThread);
        await using var pool = CreateFakeAgentPool();
        var controller = CreateController(store, pool);
        SetRequestHeaders(
            controller,
            new Dictionary<string, string> { [InboundS2SAuthAttribute.HeaderName] = "secret" });

        _ = Assert.IsType<NoContentResult>(await controller.Delete(RootThread));
        (await store.LoadMetadataAsync(RootThread)).Should().BeNull();
    }

    [Fact]
    public async Task Delete_StillRemovesAnAgentOwnedThread_ForTheConversationsOwnClient()
    {
        // The human's client may still discard a finished sub-agent's thread; nothing about the legacy
        // browser path is narrowed by the guard.
        var store = new InMemoryConversationStore();
        await SeedThreadMetadataAsync(store, SubAgentThread);
        await using var pool = CreateFakeAgentPool();
        var controller = CreateController(store, pool);

        _ = Assert.IsType<NoContentResult>(await controller.Delete(SubAgentThread));
        (await store.LoadMetadataAsync(SubAgentThread)).Should().BeNull();
    }

    private static void AssertForbidden(IActionResult result, string expectedCode)
    {
        var forbidden = Assert.IsType<ObjectResult>(result);
        forbidden.StatusCode.Should().Be(StatusCodes.Status403Forbidden);

        var payload = JsonSerializer.Serialize(forbidden.Value);
        payload.Should().Contain(expectedCode);
        payload.Should().NotContain("secret sub-agent thinking", "a refusal must not carry the transcript");
    }

    /// <summary>A thread whose persisted transcript contains one answer and the reasoning behind it.</summary>
    private static async Task<InMemoryConversationStore> StoreWithTranscriptAsync(string threadId)
    {
        var store = new InMemoryConversationStore();
        await store.AppendMessagesAsync(
            threadId,
            [
                Persisted(threadId, "m-1", new ReasoningMessage { Reasoning = "secret sub-agent thinking" }),
                Persisted(threadId, "m-2", new TextMessage { Text = "the answer", Role = Role.Assistant }),
            ]);
        return store;
    }

    private static PersistedMessage Persisted(string threadId, string id, IMessage message) =>
        new()
        {
            Id = id,
            ThreadId = threadId,
            RunId = "run-1",
            Timestamp = 0,
            MessageType = message.GetType().Name,
            Role = "assistant",
            MessageJson = JsonSerializer.Serialize(message, message.GetType(), MessageJson),
        };

    private static Task SeedThreadMetadataAsync(InMemoryConversationStore store, string threadId) =>
        store.SaveMetadataAsync(
            threadId,
            new ThreadMetadata
            {
                ThreadId = threadId,
                LastUpdated = 1,
                Properties = ImmutableDictionary<string, object>.Empty
                    .SetItem(MultiTurnAgentPool.ModePropertyKey, SystemChatModes.DefaultModeId),
            });

    private static void SetRequestHeaders(ConversationsController controller, IDictionary<string, string> headers)
    {
        var httpContext = new DefaultHttpContext();
        foreach (var (key, value) in headers)
        {
            httpContext.Request.Headers[key] = value;
        }

        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    private static MultiTurnAgentPool CreateFakeAgentPool() =>
        new(
            (threadId, _, _) => new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(threadId)),
            NullLogger<MultiTurnAgentPool>.Instance);

    /// <summary>A pool that fails the test if the refused write ever reaches agent creation.</summary>
    private static MultiTurnAgentPool CreateForbiddenAgentPool() =>
        new(
            (threadId, _, _) => throw new InvalidOperationException(
                $"A refused request must not create an agent for '{threadId}'."),
            NullLogger<MultiTurnAgentPool>.Instance);

    private static ConversationsController CreateController(IConversationStore store, MultiTurnAgentPool pool) =>
        new(
            store,
            pool,
            Mock.Of<IChatModeStore>(),
            Mock.Of<IWorkspaceStore>(),
            new FakeProviderRegistry(defaultProviderId: "test", available: ["test"]).ToReal(),
            new ConversationStatusResolver(store, new InMemoryConversationStore()),
            new WorkflowRunRegistry(),
            NullLogger<ConversationsController>.Instance);
}
