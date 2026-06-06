using System.Text.Json;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Agents;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Auth;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.GithubCopilotProvider.Tests.Agents;

/// <summary>
///     Verifies the WebSocket Responses transport: <c>response.create</c> framing, event parsing
///     via the shared <see cref="ResponseEventParser"/>, and automatic <c>previous_response_id</c>
///     chaining across turns over a single socket.
/// </summary>
public sealed class CopilotResponsesWebSocketClientTests
{
    private sealed class StubTokenProvider : ICopilotTokenProvider
    {
        public Task<string> GetTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult("gho_test");
    }

    private sealed class FakeSocket : ICopilotResponsesSocket
    {
        private readonly Func<string, IEnumerable<string>> _respond;
        private readonly Queue<string> _incoming = new();

        public FakeSocket(Func<string, IEnumerable<string>> respond) => _respond = respond;

        public List<string> Sent { get; } = [];
        public List<IReadOnlyDictionary<string, string>> Connects { get; } = [];
        public bool IsConnected { get; private set; }

        public Task ConnectAsync(Uri endpoint, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
        {
            Connects.Add(headers);
            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task SendTextAsync(string text, CancellationToken ct)
        {
            Sent.Add(text);
            foreach (var frame in _respond(text))
            {
                _incoming.Enqueue(frame);
            }

            return Task.CompletedTask;
        }

        public Task<string?> ReceiveTextAsync(CancellationToken ct) =>
            Task.FromResult(_incoming.Count > 0 ? _incoming.Dequeue() : null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static IEnumerable<string> ScriptedTurn(string sentFrame, int turnIndex)
    {
        var responseId = "resp-" + turnIndex;
        yield return "{\"type\":\"response.created\",\"sequence_number\":0,\"response\":{\"id\":\""
            + responseId
            + "\"}}";
        yield return "{\"type\":\"response.output_text.delta\",\"sequence_number\":1,\"item_id\":\"i\",\"output_index\":0,\"content_index\":0,\"delta\":\"hello\"}";
        yield return "{\"type\":\"response.completed\",\"sequence_number\":2,\"response\":{\"id\":\""
            + responseId
            + "\"}}";
    }

    private static ResponseCreateRequest Request() =>
        new()
        {
            Model = "gpt-5.5",
            Input = [new ResponseInputItem { Role = "user", Content = [new ResponseInputContent { Text = "hi" }] }],
        };

    [Fact]
    public async Task Sends_response_create_frame_with_copilot_headers_on_connect()
    {
        var turn = 0;
        var fake = new FakeSocket(sent => ScriptedTurn(sent, turn++));
        using var client = new CopilotResponsesWebSocketClient(
            new Uri("wss://api.enterprise.githubcopilot.com/responses"),
            new StubTokenProvider(),
            new CopilotSessionContext("machine-1", "session-1"),
            options: null,
            logger: null,
            socketFactory: () => fake
        );

        _ = await CollectAsync(client.StreamResponseAsync(Request()));

        var sentFrame = JsonDocument.Parse(fake.Sent.Single()).RootElement;
        sentFrame.GetProperty("type").GetString().Should().Be(ResponseEventTypes.ClientResponseCreate);
        sentFrame.GetProperty("model").GetString().Should().Be("gpt-5.5");

        var headers = fake.Connects.Single();
        headers["Authorization"].Should().Be("Bearer gho_test");
        headers["copilot-integration-id"].Should().Be("copilot-developer-cli");
        headers["x-client-session-id"].Should().Be("session-1");
    }

    [Fact]
    public async Task Yields_parsed_events_until_completed()
    {
        var turn = 0;
        var fake = new FakeSocket(sent => ScriptedTurn(sent, turn++));
        using var client = new CopilotResponsesWebSocketClient(
            new Uri("wss://host/responses"),
            new StubTokenProvider(),
            new CopilotSessionContext("m", "s"),
            socketFactory: () => fake
        );

        var events = await CollectAsync(client.StreamResponseAsync(Request()));

        events
            .Select(e => e.Type)
            .Should()
            .ContainInOrder(
                ResponseEventTypes.ResponseCreated,
                ResponseEventTypes.OutputTextDelta,
                ResponseEventTypes.ResponseCompleted
            );
        events.Last().Should().BeOfType<ResponseLifecycleEvent>();
    }

    [Fact]
    public async Task Chains_previous_response_id_across_turns()
    {
        var turn = 0;
        var fake = new FakeSocket(sent => ScriptedTurn(sent, turn++));
        using var client = new CopilotResponsesWebSocketClient(
            new Uri("wss://host/responses"),
            new StubTokenProvider(),
            new CopilotSessionContext("m", "s"),
            socketFactory: () => fake
        );

        _ = await CollectAsync(client.StreamResponseAsync(Request()));
        _ = await CollectAsync(client.StreamResponseAsync(Request()));

        // First frame has no previous_response_id; second chains off the first turn's completed id.
        var firstFrame = JsonDocument.Parse(fake.Sent[0]).RootElement;
        firstFrame.TryGetProperty("previous_response_id", out _).Should().BeFalse();

        var secondFrame = JsonDocument.Parse(fake.Sent[1]).RootElement;
        secondFrame.GetProperty("previous_response_id").GetString().Should().Be("resp-0");

        // The socket is reused across turns (connected once).
        fake.Connects.Should().ContainSingle();
    }

    private static async Task<List<ResponseEvent>> CollectAsync(IAsyncEnumerable<ResponseEvent> stream)
    {
        var list = new List<ResponseEvent>();
        await foreach (var ev in stream)
        {
            list.Add(ev);
        }

        return list;
    }
}
