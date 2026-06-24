using System.Net;
using System.Text;
using AchieveAi.LmDotnetTools.AnthropicProvider.Models;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using FluentAssertions;
using Xunit.Abstractions;

namespace AchieveAi.LmDotnetTools.CopilotLive.Tests;

/// <summary>
///     Live tests for the Anthropic Messages API (<c>POST /v1/messages</c>) routed through GitHub
///     Copilot. Skipped automatically when no Copilot credential is present.
/// </summary>
[Collection(CopilotLiveCollection.Name)]
public sealed class CopilotAnthropicLiveTests
{
    private readonly CopilotLiveFixture _fixture;
    private readonly ITestOutputHelper _output;

    public CopilotAnthropicLiveTests(CopilotLiveFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [SkippableFact]
    public async Task Messages_non_streaming_returns_assistant_text()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var cancellationToken = cts.Token;

        var model = await _fixture.ResolveAnthropicModelAsync(cancellationToken);
        _output.WriteLine($"Anthropic model: {model}");

        var agent = CopilotAnthropicAgentFactory.Create(
            "copilot-anthropic-live",
            _fixture.TokenProvider,
            _fixture.Session,
            _fixture.Options
        );

        var reply = await agent.GenerateReplyAsync(
            [new TextMessage { Role = Role.User, Text = "Reply with the single word: READY" }],
            new GenerateReplyOptions { ModelId = model, MaxToken = 64, Temperature = 0 },
            cancellationToken
        );

        var text = ExtractText(reply);
        _output.WriteLine($"Reply: {text}");
        text.Should().NotBeNullOrWhiteSpace();
    }

    [SkippableFact]
    public async Task Messages_streaming_yields_text()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var cancellationToken = cts.Token;

        var model = await _fixture.ResolveAnthropicModelAsync(cancellationToken);

        var agent = CopilotAnthropicAgentFactory.Create(
            "copilot-anthropic-live-stream",
            _fixture.TokenProvider,
            _fixture.Session,
            _fixture.Options
        );

        var stream = await agent.GenerateReplyStreamingAsync(
            [new TextMessage { Role = Role.User, Text = "Count from 1 to 5, separated by spaces." }],
            new GenerateReplyOptions { ModelId = model, MaxToken = 128, Temperature = 0 },
            cancellationToken
        );

        var builder = new StringBuilder();
        var sawUpdate = false;
        await foreach (var message in stream.WithCancellation(cancellationToken))
        {
            if (message is TextUpdateMessage update)
            {
                sawUpdate = true;
                _ = builder.Append(update.Text);
            }
            else if (message is TextMessage final && builder.Length == 0)
            {
                _ = builder.Append(final.Text);
            }
        }

        _output.WriteLine($"Streamed text: {builder}");
        sawUpdate.Should().BeTrue("the streaming endpoint should emit incremental text updates");
        builder.ToString().Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    ///     Documents WHY the Copilot-backed Claude providers (sonnet/haiku) cannot use server-side
    ///     web search: unlike api.anthropic.com (which accepts the <c>web_search_20250305</c> tool),
    ///     the GitHub Copilot proxy rejects it with HTTP 400 and body
    ///     <c>{"error":{"message":"The use of the web search tool is not supported.","code":"unsupported_value"}}</c>.
    ///     This is the basis for excluding sonnet/haiku from
    ///     <c>GetBuiltInToolsForProvider</c> in LmStreaming.Sample. If this test ever starts FAILING
    ///     (i.e. Copilot stops rejecting web search), that is the signal to re-enable it there.
    /// </summary>
    [SkippableFact]
    public async Task WebSearch_built_in_tool_is_rejected_by_copilot_backend()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var cancellationToken = cts.Token;

        var model = await _fixture.ResolveAnthropicModelAsync(cancellationToken);
        _output.WriteLine($"Anthropic model: {model}");

        var agent = CopilotAnthropicAgentFactory.Create(
            "copilot-anthropic-websearch",
            _fixture.TokenProvider,
            _fixture.Session,
            _fixture.Options
        );

        // Same request as a normal chat turn, but with the Anthropic server-side web_search tool
        // enabled via BuiltInTools — the one thing the Copilot backend refuses.
        var exception = await Record.ExceptionAsync(() => agent.GenerateReplyAsync(
            [new TextMessage { Role = Role.User, Text = "What is today's top news headline? Use web search." }],
            new GenerateReplyOptions
            {
                ModelId = model,
                MaxToken = 256,
                Temperature = 0,
                BuiltInTools = [new AnthropicWebSearchTool()],
            },
            cancellationToken
        ));

        _output.WriteLine($"Rejection: {exception}");
        exception.Should()
            .NotBeNull("the Copilot backend rejects the Anthropic server-side web_search tool");

        var httpException = exception as HttpRequestException;
        httpException.Should()
            .NotBeNull("the rejection surfaces as an HttpRequestException; actual: {0}", exception?.GetType().Name);
        httpException!.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // The human-readable Copilot error; assert loosely so wording drift doesn't break the canary.
        exception!.Message.Should().MatchRegex("(?i)web search.*not supported|not supported.*web search|unsupported");
    }

    private static string ExtractText(IEnumerable<IMessage> messages)
    {
        var builder = new StringBuilder();
        foreach (var message in messages)
        {
            if (message is TextMessage text)
            {
                _ = builder.Append(text.Text);
            }
        }

        return builder.ToString();
    }
}
