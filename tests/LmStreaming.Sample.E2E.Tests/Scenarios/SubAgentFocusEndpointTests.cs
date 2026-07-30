using System.Net;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.E2E.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace LmStreaming.Sample.E2E.Tests.Scenarios;

/// <summary>
/// Route-level E2E coverage for the FOCUSED sub-agent WebSocket endpoint <c>/ws/subagent</c>
/// (WI #194, presentation-only sub-agent switching). These tests prove the <c>/ws/subagent</c>
/// route → <see cref="LmStreaming.Sample.WebSocket.ChatWebSocketManager.HandleSubAgentConnectionAsync"/>
/// wiring end-to-end through the real ASP.NET Core pipeline (TestServer), WITHOUT touching the
/// parent <c>/ws</c> route or handler. Three coexistence facts are asserted here:
/// <list type="bullet">
///   <item>An unknown <c>agentId</c> (no live stream AND no persisted history) yields the handler's
///   structured <c>{"$type":"error","code":"subagent_unavailable",…}</c> frame and then the socket
///   closes.</item>
///   <item>Missing required query params (<c>parentThreadId</c>/<c>agentId</c>) are rejected by the
///   route with <c>400 Bad Request</c> before any WebSocket is accepted.</item>
///   <item>An <c>agentId</c> with NO live stream but a PERSISTED transcript (the "completed tab after
///   the parent loop was evicted" state) replays read-only: the handler settles the client with a lone
///   <c>{"$type":"done"}</c> sentinel, emits NO <c>subagent_unavailable</c> error, and holds the socket
///   open (the transcript itself renders from REST).</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Live-focus streaming is intentionally NOT covered here.</b> A "happy path" that connects to a
/// still-running child and observes its streamed frames cannot be made deterministic through the
/// scripted WS harness: the runtime <c>agent_id</c> is a GUID minted at spawn time and a scripted
/// background child completes near-instantly, so racing a fresh <c>/ws/subagent</c> connection
/// against the child's lifetime is inherently flaky. The LIVE streaming contract of
/// <c>HandleSubAgentConnectionAsync</c> is already covered deterministically at the unit level by
/// <c>ChatWebSocketManagerSubAgentTests.HandleSubAgentConnectionAsync_StreamsChildSubscribeAsyncOutput_ToClient</c>
/// (a gated child over an in-memory <c>FakeWebSocket</c>), and the browser-observable focus/switch UX
/// is the remit of the Task 9 browser E2E suite. This class therefore keeps the three deterministic
/// route-contract facts (including the completed-tab persisted replay, which needs no live LLM) and
/// defers live-focus streaming to Task 9.
/// </para>
/// </remarks>
public sealed class SubAgentFocusEndpointTests
{
    private const string WorkerMarker = "You are the focus worker sub-agent";

    [Fact]
    public async Task ConnectingToWsSubagent_WithUnknownAgentId_ReceivesStructuredErrorAndClose()
    {
        using var factory = CreateFactory();

        // A parent agent must exist in the pool: HandleSubAgentConnectionAsync resolves it via
        // _agentPool.TryGet (which does NOT create). Open a normal parent /ws connection and send one
        // message so the parent MultiTurnAgentLoop is created + pooled, then reuse that threadId as
        // parentThreadId. Keep the parent socket open across the sub-agent connect so the pooled agent
        // is unambiguously present.
        var parentThreadId = $"subagent-focus-{Guid.NewGuid():N}";
        var parentSocket = await factory.ConnectWebSocketAsync(parentThreadId);
        await using var parentClient = new WebSocketTestClient(parentSocket);

        await parentClient.SendUserMessageAsync("hello parent");
        using (var parentFrames = await parentClient.CollectUntilDoneAsync(TimeSpan.FromSeconds(15)))
        {
            parentFrames.ConcatText().Should().Contain("Parent ready");
        }

        const string UnknownAgentId = "does-not-exist";
        var childSocket = await factory.ConnectSubAgentWebSocketAsync(parentThreadId, UnknownAgentId);
        await using var childClient = new WebSocketTestClient(childSocket);

        // The handler emits a single structured error frame then closes the socket. CollectUntilDoneAsync
        // returns when the server sends the close frame (the error frame is not the 'done' sentinel).
        using var childFrames = await childClient.CollectUntilDoneAsync(TimeSpan.FromSeconds(15));

        var errorFrame = childFrames.SingleOrDefault(IsSubAgentUnavailableError);
        errorFrame.Should().NotBeNull(
            "the route must invoke HandleSubAgentConnectionAsync, which answers an unknown agentId "
            + "with a structured subagent_unavailable error");

        var root = errorFrame!.RootElement;
        root.GetProperty("$type").GetString().Should().Be("error");
        root.GetProperty("code").GetString().Should().Be("subagent_unavailable");
        root.GetProperty("agentId").GetString().Should().Be(UnknownAgentId);
        root.GetProperty("message").GetString().Should().Contain(UnknownAgentId);

        // The socket must be closed by the server after the structured error (no lingering Open state).
        childSocket.State.Should().NotBe(System.Net.WebSockets.WebSocketState.Open);
    }

    [Fact]
    public async Task ConnectingToWsSubagent_NoLiveStreamButPersistedHistory_ReplaysReadOnly_NoErrorFrame()
    {
        using var factory = CreateFactory();

        // Same parent setup as the unknown-agent test: the handler resolves the parent via
        // _agentPool.TryGet, so open a parent /ws connection and send one message to create + pool the
        // parent MultiTurnAgentLoop, then keep it open across the sub-agent connect.
        var parentThreadId = $"subagent-replay-{Guid.NewGuid():N}";
        var parentSocket = await factory.ConnectWebSocketAsync(parentThreadId);
        await using var parentClient = new WebSocketTestClient(parentSocket);

        await parentClient.SendUserMessageAsync("hello parent");
        using (var parentFrames = await parentClient.CollectUntilDoneAsync(TimeSpan.FromSeconds(15)))
        {
            parentFrames.ConcatText().Should().Contain("Parent ready");
        }

        // Seed a COMPLETED sub-agent transcript under "subagent-{agentId}" in the SAME IConversationStore
        // singleton the handler reads. The agentId is NEVER spawned as a live child of this parent, so the
        // handler's live-stream resolution (parent loop → SubAgentManager → TryGetAgent) fails and
        // `stream` is null — the exact "parent loop evicted / completed tab" state the replay fix targets.
        var completedAgentId = $"completed-{Guid.NewGuid():N}";
        var persistedThreadId = $"subagent-{completedAgentId}";
        var store = factory.Services.GetRequiredService<IConversationStore>();
        var persisted = MessagePersistenceConverter.ToPersistedMessage(
            new TextMessage { Role = Role.Assistant, Text = "persisted-child-answer" },
            persistedThreadId,
            runId: "run-1");
        await store.AppendMessagesAsync(persistedThreadId, [persisted]);

        var childSocket = await factory.ConnectSubAgentWebSocketAsync(parentThreadId, completedAgentId);
        await using var childClient = new WebSocketTestClient(childSocket);

        // The handler settles the focused-streaming client with a lone `done` sentinel (no content, no
        // error frame) so the client stops spinning; the transcript itself renders from REST. Because no
        // WS content is streamed, there is no merge-key / duplicate-pill risk.
        using var childFrames = await childClient.CollectUntilDoneAsync(TimeSpan.FromSeconds(15));

        childFrames.Any(IsSubAgentUnavailableError).Should().BeFalse(
            "a completed sub-agent WITH persisted history must replay, not answer with the scary "
            + "subagent_unavailable error");
        childFrames.OfMessageType("done").Should().ContainSingle(
            "the handler settles the focused-streaming client with the done sentinel so it stops spinning");

        // Read-only replay holds the socket OPEN (mirroring a completed shared-provider tab); only the
        // genuinely-missing-agent path closes it. This distinguishes replay from the unavailable-error case.
        childSocket.State.Should().Be(System.Net.WebSockets.WebSocketState.Open);
    }

    [Fact]
    public async Task ConnectingToWsSubagent_MissingParams_ReturnsBadRequest()
    {
        using var factory = CreateFactory();

        // 1) A plain HTTP GET (no WebSocket upgrade) is rejected by the route with 400 before any
        //    handler runs — proving /ws/subagent is mapped and guards the connection contract.
        using var httpClient = factory.CreateClient();
        using var response = await httpClient.GetAsync("/ws/subagent");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // 2) A genuine WebSocket upgrade that omits the required agentId must fail the handshake: the
        //    route reaches the missing-params branch and returns 400, so the WS connect cannot complete.
        var wsClient = factory.Server.CreateWebSocketClient();
        var uri = new UriBuilder(factory.Server.BaseAddress)
        {
            Scheme = "ws",
            Path = "/ws/subagent",
            Query = "parentThreadId=some-parent",
        }.Uri;

        Func<Task> connectMissingAgentId = () => wsClient.ConnectAsync(uri, CancellationToken.None);
        await connectMissingAgentId.Should().ThrowAsync<Exception>(
            "the route rejects a WebSocket upgrade missing the required agentId query param");
    }

    private static E2EWebAppFactory CreateFactory()
    {
        var responder = ScriptedSseResponder.New()
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
                .Turn(t => t.Text("Parent ready."))
            .Build();

        // Give the parent a SubAgentManager (a template) so it is a fully-formed focus-capable parent,
        // mirroring the real switching flow; the error path is reached because the requested child id is
        // simply absent from that manager.
        var builder = new ScriptedBuilder(
            responder,
            subAgentFactory: (_, providerAgentFactory) => new SubAgentOptions
            {
                Templates = new Dictionary<string, SubAgentTemplate>
                {
                    ["worker"] = new SubAgentTemplate
                    {
                        Name = "FocusWorker",
                        SystemPrompt = WorkerMarker,
                        AgentFactory = providerAgentFactory,
                        MaxTurnsPerRun = 5,
                    },
                },
                MaxConcurrentSubAgents = 5,
            });

        return new E2EWebAppFactory("test", builder);
    }

    private static bool IsSubAgentUnavailableError(JsonDocument frame)
    {
        var root = frame.RootElement;
        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("$type", out var type)
            && type.ValueKind == JsonValueKind.String
            && string.Equals(type.GetString(), "error", StringComparison.Ordinal)
            && root.TryGetProperty("code", out var code)
            && code.ValueKind == JsonValueKind.String
            && string.Equals(code.GetString(), "subagent_unavailable", StringComparison.Ordinal);
    }
}
