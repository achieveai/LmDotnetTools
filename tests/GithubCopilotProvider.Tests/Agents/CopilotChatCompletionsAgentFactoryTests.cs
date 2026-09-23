using System.Net;
using System.Text;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Agents;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Auth;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmTestUtils;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.GithubCopilotProvider.Tests.Agents;

/// <summary>
///     Drives the real Copilot Chat Completions pipeline (headers + dialect handler + stock OpenClient)
///     over a Gemini stream recorded from Copilot: zero-token usage on every chunk, <c>content: null</c>,
///     <c>reasoning_text</c>, and <c>reasoning_opaque</c> on the first of two parallel tool-call chunks.
/// </summary>
public sealed class CopilotChatCompletionsAgentFactoryTests
{
    private sealed class StubTokenProvider : ICopilotTokenProvider
    {
        public Task<string> GetTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("gho_test_token");
    }

    private static string RecordedGeminiStream =>
        File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "copilot-gemini-parallel-toolcall-stream.sse")
        );

    [Fact]
    public async Task Streams_text_reasoning_signature_and_parallel_tool_calls_from_recorded_gemini_response()
    {
        HttpRequestMessage? sent = null;
        var transport = new FakeHttpMessageHandler(
            (request, _) =>
            {
                sent = request;
                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(RecordedGeminiStream, Encoding.UTF8, "text/event-stream"),
                    }
                );
            }
        );
        using var agent = CopilotChatCompletionsAgentFactory.Create(
            "gemini",
            new StubTokenProvider(),
            timeout: null,
            session: new CopilotSessionContext("machine-1", "session-1"),
            options: null,
            logger: null,
            retryOptions: null,
            innerHandler: transport
        );

        var stream = await agent.GenerateReplyStreamingAsync(
            [new TextMessage { Role = Role.User, Text = "Read hello.txt and hello2.txt" }],
            new GenerateReplyOptions { ModelId = "gemini-3.8-flash" }
        );
        var messages = new List<IMessage>();
        await foreach (var message in stream)
        {
            messages.Add(message);
        }

        sent!.RequestUri!.AbsolutePath.Should().Be("/chat/completions");
        sent.Headers.GetValues("openai-intent").Should().Equal("conversation-agent");

        messages
            .OfType<ReasoningUpdateMessage>()
            .Should()
            .ContainSingle()
            .Which.Reasoning.Should()
            .StartWith("**Reading Files**");
        messages
            .OfType<ReasoningMessage>()
            .Should()
            .ContainSingle(r => r.Visibility == ReasoningVisibility.Encrypted)
            .Which.Reasoning.Should()
            .HaveLength(40);
        messages
            .OfType<ToolsCallUpdateMessage>()
            .SelectMany(m => m.ToolCallUpdates)
            .Select(u => u.FunctionName)
            .Should()
            .Equal("view", "view");
        string.Concat(messages.OfType<TextUpdateMessage>().Select(t => t.Text)).Should().StartWith("I'll read both");
    }

    [Fact]
    public void Disposing_the_agent_disposes_the_factory_built_http_pipeline_exactly_once()
    {
        var transport = new DisposeCountingHandler();
        var agent = CopilotChatCompletionsAgentFactory.Create(
            "gemini",
            new StubTokenProvider(),
            timeout: null,
            session: null,
            options: null,
            logger: null,
            retryOptions: null,
            innerHandler: transport
        );

        transport.DisposeCount.Should().Be(0);

        agent.Dispose();
        agent.Dispose();

        transport.DisposeCount.Should().Be(1);
    }

    private sealed class DisposeCountingHandler : HttpMessageHandler
    {
        public int DisposeCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException("No request is expected.");

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCount++;
            }

            base.Dispose(disposing);
        }
    }
}
