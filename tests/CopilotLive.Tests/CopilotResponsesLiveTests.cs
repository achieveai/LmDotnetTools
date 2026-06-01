using System.Text;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Agents;
using FluentAssertions;
using Xunit.Abstractions;

namespace AchieveAi.LmDotnetTools.CopilotLive.Tests;

/// <summary>
///     Live tests for the OpenAI Responses API (<c>/responses</c>) routed through GitHub Copilot,
///     over both the SSE (HTTP POST) and WebSocket transports. Skipped automatically when no Copilot
///     credential is present.
/// </summary>
[Collection(CopilotLiveCollection.Name)]
public sealed class CopilotResponsesLiveTests
{
    private readonly CopilotLiveFixture _fixture;
    private readonly ITestOutputHelper _output;

    public CopilotResponsesLiveTests(CopilotLiveFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [SkippableFact]
    public async Task Lists_available_models()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var models = await _fixture.GetModelsAsync(cts.Token);
        foreach (var id in models)
        {
            _output.WriteLine(id);
        }

        models.Should().NotBeEmpty("GET /models should return the catalog the account can access");
    }

    [SkippableFact]
    public async Task Responses_sse_returns_text()
    {
        await RunSingleTurnAsync(CopilotResponsesTransport.Sse);
    }

    [SkippableFact]
    public async Task Responses_websocket_returns_text()
    {
        await RunSingleTurnAsync(CopilotResponsesTransport.WebSocket);
    }

    [SkippableFact]
    public async Task Responses_websocket_multi_turn_chains_previous_response()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var cancellationToken = cts.Token;

        var model = await _fixture.ResolveOpenAiModelAsync(cancellationToken);
        _output.WriteLine($"OpenAI model: {model}");

        using var agent = CopilotResponsesAgentFactory.Create(
            "copilot-responses-live-ws-multi",
            _fixture.TokenProvider,
            CopilotResponsesTransport.WebSocket,
            _fixture.Session,
            _fixture.Options
        );

        var options = new GenerateReplyOptions { ModelId = model, MaxToken = 128 };

        var first = ExtractText(
            await agent.GenerateReplyAsync(
                [new TextMessage { Role = Role.User, Text = "My favourite colour is teal. Acknowledge in one short sentence." }],
                options,
                cancellationToken
            )
        );
        _output.WriteLine($"Turn 1: {first}");
        first.Should().NotBeNullOrWhiteSpace();

        // Second turn reuses the same open socket; the client chains previous_response_id automatically.
        var second = ExtractText(
            await agent.GenerateReplyAsync(
                [new TextMessage { Role = Role.User, Text = "What colour did I say was my favourite? Answer with one word." }],
                options,
                cancellationToken
            )
        );
        _output.WriteLine($"Turn 2: {second}");
        second.Should().NotBeNullOrWhiteSpace();
        second.Should().ContainEquivalentOf("teal", "server-side state should carry context from the prior turn");
    }

    private async Task RunSingleTurnAsync(CopilotResponsesTransport transport)
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var cancellationToken = cts.Token;

        var model = await _fixture.ResolveOpenAiModelAsync(cancellationToken);
        _output.WriteLine($"OpenAI model: {model} ({transport})");

        using var agent = CopilotResponsesAgentFactory.Create(
            $"copilot-responses-live-{transport}",
            _fixture.TokenProvider,
            transport,
            _fixture.Session,
            _fixture.Options
        );

        var reply = await agent.GenerateReplyAsync(
            [new TextMessage { Role = Role.User, Text = "Reply with the single word: READY" }],
            new GenerateReplyOptions { ModelId = model, MaxToken = 64 },
            cancellationToken
        );

        var text = ExtractText(reply);
        _output.WriteLine($"Reply: {text}");
        text.Should().NotBeNullOrWhiteSpace();
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
