using System.Text.Json;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Agents;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Auth;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.GithubCopilotProvider.Tests.Agents;

/// <summary>
///     Verifies the WebSocket Responses transport: <c>response.create</c> framing, event parsing
///     via the shared <see cref="ResponseEventParser"/>, socket reuse across turns, and NO automatic
///     <c>previous_response_id</c> chaining (the agent already sends the full history).
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
    public async Task Full_history_turn_is_not_chained_to_the_previous_response()
    {
        // The agent resends the whole conversation every turn. If the client also chained
        // previous_response_id, the server would prepend turn 1's context to that full history and
        // bill every earlier reply again as uncached input (measured live: +390 tokens per prior turn).
        var turn = 0;
        var fake = new FakeSocket(sent => ScriptedTurn(sent, turn++));
        using var client = new CopilotResponsesWebSocketClient(
            new Uri("wss://host/responses"),
            new StubTokenProvider(),
            new CopilotSessionContext("m", "s"),
            socketFactory: () => fake
        );

        var first = Request();
        _ = await CollectAsync(client.StreamResponseAsync(first));
        var second = first with
        {
            Input =
            [
                .. first.Input,
                new ResponseInputItem
                {
                    Role = "assistant",
                    Content = [new ResponseInputContent { Type = "output_text", Text = "hello" }],
                },
                new ResponseInputItem { Role = "user", Content = [new ResponseInputContent { Text = "more" }] },
            ],
        };
        _ = await CollectAsync(client.StreamResponseAsync(second));

        foreach (var sent in fake.Sent)
        {
            JsonDocument.Parse(sent).RootElement.TryGetProperty("previous_response_id", out _).Should().BeFalse();
        }

        var secondFrame = JsonDocument.Parse(fake.Sent[1]).RootElement;
        secondFrame.GetProperty("input").GetArrayLength().Should().Be(3, "the full history is sent as-is");

        // The socket is reused across turns (connected once).
        fake.Connects.Should().ContainSingle();
    }

    [Fact]
    public async Task Caller_supplied_previous_response_id_is_passed_through()
    {
        var turn = 0;
        var fake = new FakeSocket(sent => ScriptedTurn(sent, turn++));
        using var client = new CopilotResponsesWebSocketClient(
            new Uri("wss://host/responses"),
            new StubTokenProvider(),
            new CopilotSessionContext("m", "s"),
            socketFactory: () => fake
        );

        _ = await CollectAsync(client.StreamResponseAsync(Request() with { PreviousResponseId = "resp-explicit" }));

        JsonDocument
            .Parse(fake.Sent.Single())
            .RootElement.GetProperty("previous_response_id")
            .GetString()
            .Should()
            .Be("resp-explicit");
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
