using System.Net;
using System.Net.Http.Json;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Agents;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Tests;

/// <summary>
/// In-process tests for <see cref="OpenAiResponsesTestSseMessageHandler"/>: pair the handler
/// with <see cref="OpenAiResponsesClient"/> through an <see cref="HttpClient"/> and verify the
/// SSE pipe round-trips events with no out-of-band data.
/// </summary>
public sealed class OpenAiResponsesTestSseMessageHandlerTests
{
    [Fact]
    public async Task Handler_emits_completion_for_request_with_no_instruction_chain()
    {
        var handler = new OpenAiResponsesTestSseMessageHandler { ChunkDelayMs = 0 };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://mock.local/") };
        using var client = new OpenAiResponsesClient(http);

        var request = new ResponseCreateRequest
        {
            Model = "gpt-test",
            Stream = true,
            Input =
            [
                new ResponseInputItem
                {
                    Type = "message",
                    Role = "user",
                    Content = [new ResponseInputContent { Type = "input_text", Text = "say hi" }],
                },
            ],
        };

        var collected = new List<ResponseEvent>();
        await foreach (var ev in client.StreamResponseAsync(request))
        {
            collected.Add(ev);
        }

        collected.Should().NotBeEmpty();
        collected[0].Type.Should().Be(ResponseEventTypes.ResponseCreated);
        collected[^1].Type.Should().Be(ResponseEventTypes.ResponseCompleted);
        collected.OfType<ResponseOutputTextDeltaEvent>().Should().NotBeEmpty();
    }

    [Fact]
    public async Task Handler_executes_instruction_chain_in_order_across_turns()
    {
        var handler = new OpenAiResponsesTestSseMessageHandler { ChunkDelayMs = 0 };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://mock.local/") };
        using var client = new OpenAiResponsesClient(http);

        const string chainPrompt = """
            run this:
            <|instruction_start|>
            {"instruction_chain": [
                {"id_message":"first","messages":[{"text_message":{"length":3}}]},
                {"id_message":"second","messages":[{"text_message":{"length":4}}]}
            ]}
            <|instruction_end|>
            """;

        // First turn: only the user prompt with the chain → handler picks chain[0].
        var firstRequest = MakeRequest([
            new ResponseInputItem
            {
                Type = "message",
                Role = "user",
                Content = [new ResponseInputContent { Type = "input_text", Text = chainPrompt }],
            },
        ]);
        var first = await CollectAsync(client, firstRequest);
        var firstText = string.Concat(first.OfType<ResponseOutputTextDeltaEvent>().Select(d => d.Delta));
        firstText.Trim().Split(' ').Length.Should().Be(3);

        // Second turn: append an assistant turn so the handler advances to chain[1].
        var secondRequest = MakeRequest([
            new ResponseInputItem
            {
                Type = "message",
                Role = "user",
                Content = [new ResponseInputContent { Type = "input_text", Text = chainPrompt }],
            },
            new ResponseInputItem
            {
                Type = "message",
                Role = "assistant",
                Content = [new ResponseInputContent { Type = "output_text", Text = firstText }],
            },
        ]);
        var second = await CollectAsync(client, secondRequest);
        var secondText = string.Concat(second.OfType<ResponseOutputTextDeltaEvent>().Select(d => d.Delta));
        secondText.Trim().Split(' ').Length.Should().Be(4);
    }

    [Fact]
    public async Task Handler_returns_400_when_stream_false()
    {
        var handler = new OpenAiResponsesTestSseMessageHandler { ChunkDelayMs = 0 };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://mock.local/") };

        using var content = JsonContent.Create(new
        {
            model = "gpt-test",
            stream = false,
            input = Array.Empty<object>(),
        });

        using var response = await http.PostAsync("http://mock.local/v1/responses", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("stream=true");
    }

    [Fact]
    public async Task Handler_returns_404_for_non_responses_path()
    {
        var handler = new OpenAiResponsesTestSseMessageHandler { ChunkDelayMs = 0 };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://mock.local/") };

        using var content = JsonContent.Create(new { });
        using var response = await http.PostAsync("http://mock.local/v1/chat/completions", content);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Handler_emits_function_call_events_for_tool_call_instruction()
    {
        var handler = new OpenAiResponsesTestSseMessageHandler { ChunkDelayMs = 0 };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://mock.local/") };
        using var client = new OpenAiResponsesClient(http);

        const string toolPrompt = """
            <|instruction_start|>
            {"instruction_chain":[
                {"messages":[{"tool_call":[{"name":"lookup","args":{"q":"foo"}}]}]}
            ]}
            <|instruction_end|>
            """;

        var request = MakeRequest([
            new ResponseInputItem
            {
                Type = "message",
                Role = "user",
                Content = [new ResponseInputContent { Type = "input_text", Text = toolPrompt }],
            },
        ]);

        var events = await CollectAsync(client, request);

        var argsDone = events.OfType<ResponseFunctionCallArgumentsDoneEvent>().Single();
        argsDone.Arguments.Should().Contain("\"q\":\"foo\"");
    }

    private static ResponseCreateRequest MakeRequest(IReadOnlyList<ResponseInputItem> input) =>
        new()
        {
            Model = "gpt-test",
            Stream = true,
            Input = input,
        };

    private static async Task<List<ResponseEvent>> CollectAsync(
        OpenAiResponsesClient client,
        ResponseCreateRequest request)
    {
        var events = new List<ResponseEvent>();
        await foreach (var ev in client.StreamResponseAsync(request))
        {
            events.Add(ev);
        }

        return events;
    }
}
