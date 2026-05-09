using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Agents;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.MockProviderHost.Tests;

/// <summary>
/// End-to-end consumption: the <see cref="OpenAiResponsesClient"/> speaks real HTTP to the
/// mock host on its bound port, and the streamed scripted response decodes into the expected
/// <see cref="ResponseEvent"/> sequence. Validates the wire shape, not just the inner handler.
/// </summary>
public sealed class OpenAiResponsesProviderConsumptionTests
{
    [Fact]
    public async Task OpenAiResponsesClient_streams_scripted_text_through_HTTP_endpoint()
    {
        var responder = ScriptedSseResponder.New()
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
                .Turn(t => t.Text("hello from the mock host"))
            .Build();

        await using var fixture = await EphemeralHostFixture.StartAsync(responder);
        using var http = new HttpClient { BaseAddress = new Uri(fixture.BaseUrl) };
        using var client = new OpenAiResponsesClient(http);

        var request = new ResponseCreateRequest
        {
            Model = "gpt-test",
            Stream = true,
            Instructions = "You are a helpful assistant.",
            Input =
            [
                new ResponseInputItem
                {
                    Type = "message",
                    Role = "user",
                    Content = [new ResponseInputContent { Type = "input_text", Text = "say hello" }],
                },
            ],
        };

        var events = new List<ResponseEvent>();
        await foreach (var ev in client.StreamResponseAsync(request))
        {
            events.Add(ev);
        }

        events[0].Type.Should().Be(ResponseEventTypes.ResponseCreated);
        events[^1].Type.Should().Be(ResponseEventTypes.ResponseCompleted);

        var text = string.Concat(events.OfType<ResponseOutputTextDeltaEvent>().Select(d => d.Delta));
        text.Should().Contain("hello from the mock host");
        responder.RemainingTurns["parent"].Should().Be(0);
    }

    [Fact]
    public async Task HTTP_endpoint_propagates_responses_content_type()
    {
        var responder = ScriptedSseResponder.New()
            .ForRole("any", _ => true).Turn(t => t.Text("ok"))
            .Build();

        await using var fixture = await EphemeralHostFixture.StartAsync(responder);
        using var http = new HttpClient { BaseAddress = new Uri(fixture.BaseUrl) };

        var body = """
            {
              "model":"gpt-test",
              "stream":true,
              "input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"hi"}]}]
            }
            """;
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("/v1/responses", content);

        response.EnsureSuccessStatusCode();
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");
    }
}
