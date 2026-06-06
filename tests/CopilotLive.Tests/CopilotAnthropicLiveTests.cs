using System.Text;
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
