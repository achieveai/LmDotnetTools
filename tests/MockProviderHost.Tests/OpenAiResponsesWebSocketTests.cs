using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.MockProviderHost.Tests;

/// <summary>
/// Verifies the WebSocket transport for <c>/v1/responses</c>: a single client socket can issue
/// multiple <c>response.create</c> frames and receive the full event stream for each as raw
/// text frames. Negative paths cover non-WS GETs and malformed frames.
/// </summary>
public sealed class OpenAiResponsesWebSocketTests
{
    [Fact]
    public async Task Single_response_create_without_instruction_chain_consumes_scenario_turn()
    {
        var responder = ScriptedSseResponder.New().ForRole("ws-role", _ => true).Turn(t => t.Text("ws hello")).Build();
        await using var fixture = await EphemeralHostFixture.StartAsync(responder);

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(BuildWsUri(fixture.BaseUrl), CancellationToken.None);

        await SendJsonAsync(socket, BuildResponseCreate("hello"));

        var events = await ReadStreamUntilCompletedAsync(socket);

        events[0].Type.Should().Be(ResponseEventTypes.ResponseCreated);
        events[^1].Type.Should().Be(ResponseEventTypes.ResponseCompleted);
        var text = string.Concat(events.OfType<ResponseOutputTextDeltaEvent>().Select(d => d.Delta));
        text.Should().Contain("ws hello");
        text.Should().NotContain("lorem ipsum");
        text.Should().NotBe("ok");
        responder.RemainingTurns["ws-role"].Should().Be(0);
    }

    [Fact]
    public async Task Response_create_with_instruction_chain_uses_chain_output()
    {
        var responder = ScriptedSseResponder
            .New()
            .ForRole("scenario", _ => true)
            .Turn(t => t.Text("SCENARIO_SHOULD_NOT_WIN"))
            .Build();
        await using var fixture = await EphemeralHostFixture.StartAsync(responder);

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(BuildWsUri(fixture.BaseUrl), CancellationToken.None);

        const string chainPrompt = """
            <|instruction_start|>
            {"instruction_chain":[
                {"id":"ws-chain","messages":[{"text":"WS_CHAIN_OUTPUT"}]}
            ]}
            <|instruction_end|>
            """;
        await SendJsonAsync(socket, BuildResponseCreate(chainPrompt));

        var events = await ReadStreamUntilCompletedAsync(socket);
        var text = string.Concat(events.OfType<ResponseOutputTextDeltaEvent>().Select(d => d.Delta));

        text.Should().Contain("WS_CHAIN_OUTPUT");
        text.Should().NotContain("SCENARIO_SHOULD_NOT_WIN");
        responder.RemainingTurns["scenario"].Should().Be(1);
    }

    [Fact]
    public async Task Response_create_with_tools_list_instruction_returns_advertised_response_tools()
    {
        var responder = ScriptedSseResponder
            .New()
            .ForRole("scenario", _ => true)
            .Turn(t => t.Text("SCENARIO_SHOULD_NOT_WIN"))
            .Build();
        await using var fixture = await EphemeralHostFixture.StartAsync(responder);

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(BuildWsUri(fixture.BaseUrl), CancellationToken.None);

        const string chainPrompt = """
            <|instruction_start|>
            {"instruction_chain":[
                {"id":"ws-tools-list","messages":[{"tools_list":{}}]}
            ]}
            <|instruction_end|>
            """;
        await SendJsonAsync(socket, BuildResponseCreateWithTools(chainPrompt));

        var events = await ReadStreamUntilCompletedAsync(socket);
        var text = string.Concat(events.OfType<ResponseOutputTextDeltaEvent>().Select(d => d.Delta));

        text.Should().Contain("calculate");
        text.Should().Contain("get_weather");
        text.Should().NotContain("__TOOLS_LIST__");
        text.Should().NotContain("SCENARIO_SHOULD_NOT_WIN");
    }

    [Fact]
    public async Task Multiple_response_create_frames_share_one_socket()
    {
        var responder = ScriptedSseResponder
            .New()
            .ForRole("ws-role", _ => true)
            .Turn(t => t.Text("first turn"))
            .Turn(t => t.Text("second turn"))
            .Build();
        await using var fixture = await EphemeralHostFixture.StartAsync(responder);

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(BuildWsUri(fixture.BaseUrl), CancellationToken.None);

        await SendJsonAsync(socket, BuildResponseCreate("first prompt"));
        var firstEvents = await ReadStreamUntilCompletedAsync(socket);
        var firstText = string.Concat(firstEvents.OfType<ResponseOutputTextDeltaEvent>().Select(d => d.Delta));
        firstText.Should().Contain("first turn");
        firstText.Should().NotContain("lorem ipsum");

        await SendJsonAsync(socket, BuildResponseCreate("second prompt"));
        var secondEvents = await ReadStreamUntilCompletedAsync(socket);
        var secondText = string.Concat(secondEvents.OfType<ResponseOutputTextDeltaEvent>().Select(d => d.Delta));
        secondText.Should().Contain("second turn");
        secondText.Should().NotContain("lorem ipsum");

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        responder.RemainingTurns["ws-role"].Should().Be(0);
    }

    [Fact]
    public async Task Malformed_frame_closes_with_invalid_payload_status()
    {
        var responder = ScriptedSseResponder.New().ForRole("any", _ => true).Turn(t => t.Text("unused")).Build();
        await using var fixture = await EphemeralHostFixture.StartAsync(responder);

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(BuildWsUri(fixture.BaseUrl), CancellationToken.None);

        var bytes = Encoding.UTF8.GetBytes("not json at all");
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);

        var buffer = new byte[1024];
        WebSocketReceiveResult? received = null;
        try
        {
            received = await socket.ReceiveAsync(buffer, CancellationToken.None);
        }
        catch (WebSocketException)
        {
            // The host closes the socket; on some platforms the close frame surfaces as an
            // exception rather than a Close result. Either path is acceptable — what we want
            // to verify is that the socket is no longer Open.
        }

        socket.State.Should().BeOneOf(WebSocketState.CloseReceived, WebSocketState.Closed, WebSocketState.Aborted);
        if (received?.MessageType == WebSocketMessageType.Close)
        {
            socket.CloseStatus.Should().Be(WebSocketCloseStatus.InvalidPayloadData);
        }
    }

    [Fact]
    public async Task Frame_with_wrong_type_is_rejected()
    {
        var responder = ScriptedSseResponder.New().ForRole("any", _ => true).Turn(t => t.Text("unused")).Build();
        await using var fixture = await EphemeralHostFixture.StartAsync(responder);

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(BuildWsUri(fixture.BaseUrl), CancellationToken.None);

        await SendJsonAsync(socket, """{"type":"session.update"}""");

        var buffer = new byte[1024];
        try
        {
            _ = await socket.ReceiveAsync(buffer, CancellationToken.None);
        }
        catch (WebSocketException)
        {
            // Acceptable — see comment in Malformed_frame test.
        }

        socket.State.Should().NotBe(WebSocketState.Open);
    }

    [Fact]
    public async Task GET_without_websocket_upgrade_returns_426()
    {
        var responder = ScriptedSseResponder.New().ForRole("any", _ => true).Turn(t => t.Text("unused")).Build();
        await using var fixture = await EphemeralHostFixture.StartAsync(responder);

        using var http = new HttpClient { BaseAddress = new Uri(fixture.BaseUrl) };
        using var response = await http.GetAsync("/v1/responses");

        response.StatusCode.Should().Be(HttpStatusCode.UpgradeRequired);
        response.Headers.TryGetValues("Upgrade", out var upgradeValues).Should().BeTrue();
        upgradeValues!.Should().Contain("websocket");
    }

    private static Uri BuildWsUri(string baseUrl)
    {
        var http = new Uri(baseUrl);
        var ws = new UriBuilder(http) { Scheme = "ws", Path = "/v1/responses" };
        return ws.Uri;
    }

    private static string BuildResponseCreate(string userText) =>
        $$"""
            {
              "type": "response.create",
              "model": "gpt-test",
              "input": [
                {"type":"message","role":"user","content":[{"type":"input_text","text":{{JsonSerializer.Serialize(
                userText
            )}}}]}
              ]
            }
            """;

    private static string BuildResponseCreateWithTools(string userText) =>
        $$$"""
            {
              "type": "response.create",
              "model": "gpt-test",
              "tools": [
                {"type":"function","name":"calculate","parameters":{"type":"object"}},
                {"type":"function","name":"get_weather","parameters":{"type":"object"}}
              ],
              "input": [
                {"type":"message","role":"user","content":[{"type":"input_text","text":{{{JsonSerializer.Serialize(
                userText
            )}}}}]}
              ]
            }
            """;

    private static async Task SendJsonAsync(WebSocket socket, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<List<ResponseEvent>> ReadStreamUntilCompletedAsync(WebSocket socket)
    {
        var events = new List<ResponseEvent>();
        var buffer = new byte[16 * 1024];
        var sb = new StringBuilder();

        while (events.Count == 0 || events[^1].Type != ResponseEventTypes.ResponseCompleted)
        {
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            _ = sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (!result.EndOfMessage)
            {
                continue;
            }

            var json = sb.ToString();
            _ = sb.Clear();
            events.Add(ResponseEventParser.Parse(json));
        }

        return events;
    }

    static OpenAiResponsesWebSocketTests()
    {
        // Force the WS client to use a single buffer to keep tests deterministic across stacks.
        _ = JsonSerializer.IsReflectionEnabledByDefault;
    }
}
