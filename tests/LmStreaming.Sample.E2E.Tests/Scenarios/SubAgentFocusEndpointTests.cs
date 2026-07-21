using System.Net;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.E2E.Tests.Scenarios;

/// <summary>
/// Route-level E2E coverage for the FOCUSED sub-agent WebSocket endpoint <c>/ws/subagent</c>
/// (WI #194, presentation-only sub-agent switching). These tests prove the <c>/ws/subagent</c>
/// route → <see cref="LmStreaming.Sample.WebSocket.ChatWebSocketManager.HandleSubAgentConnectionAsync"/>
/// wiring end-to-end through the real ASP.NET Core pipeline (TestServer), WITHOUT touching the
/// parent <c>/ws</c> route or handler. Two coexistence facts are asserted here:
/// <list type="bullet">
///   <item>An unknown <c>agentId</c> yields the handler's structured
///   <c>{"$type":"error","code":"subagent_unavailable",…}</c> frame and then the socket closes.</item>
///   <item>Missing required query params (<c>parentThreadId</c>/<c>agentId</c>) are rejected by the
///   route with <c>400 Bad Request</c> before any WebSocket is accepted.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Live-focus streaming is intentionally NOT covered here.</b> A "happy path" that connects to a
/// still-running child and observes its streamed frames cannot be made deterministic through the
/// scripted WS harness: the runtime <c>agent_id</c> is a GUID minted at spawn time and a scripted
/// background child completes near-instantly, so racing a fresh <c>/ws/subagent</c> connection
/// against the child's lifetime is inherently flaky. The live streaming + replay contract of
/// <c>HandleSubAgentConnectionAsync</c> is already covered deterministically at the unit level by
/// <c>ChatWebSocketManagerSubAgentTests.HandleSubAgentConnectionAsync_StreamsChildSubscribeAsyncOutput_ToClient</c>
/// (a gated child over an in-memory <c>FakeWebSocket</c>), and the browser-observable focus/switch UX
/// is the remit of the Task 9 browser E2E suite. This class therefore keeps the two deterministic
/// route-contract facts and defers live-focus streaming to Task 9.
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
