using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Http;
using AchieveAi.LmDotnetTools.LmTestUtils;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.GithubCopilotProvider.Tests.Http;

public sealed class CopilotChatCompletionsDialectHandlerTests
{
    [Fact]
    public void RewriteRequestBody_sends_assistant_reasoning_back_in_copilot_fields()
    {
        const string body = """
            {"messages":[
              {"role":"user","content":"hi","reasoning":"user field stays"},
              {"role":"assistant","content":"","reasoning":"PLAIN",
               "reasoning_details":[{"type":"reasoning.encrypted","data":"SIG"}],
               "tool_calls":[{"id":"c1","type":"function","function":{"name":"view","arguments":"{}"}}]}
            ]}
            """;

        var messages = JsonNode.Parse(CopilotChatCompletionsDialectHandler.RewriteRequestBody(body))!["messages"]!;

        messages[0]!["reasoning"]!.GetValue<string>().Should().Be("user field stays");
        var assistant = messages[1]!.AsObject();
        assistant["reasoning_opaque"]!.GetValue<string>().Should().Be("SIG");
        assistant["reasoning_text"]!.GetValue<string>().Should().Be("PLAIN");
        assistant.Should().NotContainKeys("reasoning", "reasoning_details");
        assistant["tool_calls"]!.AsArray().Should().ContainSingle();
    }

    [Fact]
    public void RewriteRequestBody_returns_the_same_body_when_there_is_nothing_to_translate()
    {
        const string body = """{"messages":[{"role":"assistant","content":"done"}]}""";

        CopilotChatCompletionsDialectHandler.RewriteRequestBody(body).Should().BeSameAs(body);
    }

    [Fact]
    public async Task Streamed_events_are_forwarded_before_the_upstream_stream_ends()
    {
        var upstream = new Pipe();
        using var invoker = new HttpMessageInvoker(
            new CopilotChatCompletionsDialectHandler(
                new FakeHttpMessageHandler(
                    (_, _) =>
                    {
                        var content = new StreamContent(upstream.Reader.AsStream());
                        content.Headers.ContentType = new("text/event-stream");
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
                    }
                )
            )
        );
        using var response = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "https://copilot.test/chat/completions"),
            CancellationToken.None
        );
        await using var reader = await response.Content.ReadAsStreamAsync();

        await upstream.Writer.WriteAsync(
            Encoding.UTF8.GetBytes("""data: {"choices":[{"index":0,"delta":{"content":"Hi"}}]}""" + "\n\n")
        );
        var buffer = new byte[1024];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var read = await reader.ReadAsync(buffer, timeout.Token);

        // The upstream is still open: this event reached the reader without waiting for the end.
        Encoding.UTF8.GetString(buffer, 0, read).Should().StartWith("data: ").And.Contain("\"Hi\"");
        await upstream.Writer.CompleteAsync();
    }
}
