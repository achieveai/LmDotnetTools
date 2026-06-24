using System.Text;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Agents;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;
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

    /// <summary>
    ///     Canary: does the Copilot Responses backend RETURN reasoning summaries when asked? Requests
    ///     reasoning via <c>ExtraProperties["Reasoning"]</c> and checks for reasoning frames. If this
    ///     passes, the GPT-5.5 thinking pill works end-to-end (request → backend → parser → UI). If it
    ///     fails with zero reasoning, the Copilot proxy strips reasoning summaries server-side (not
    ///     fixable client-side) — the signal to document it rather than chase the parser.
    /// </summary>
    [SkippableFact]
    public async Task Reasoning_summary_is_returned_by_copilot_responses_backend()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var cancellationToken = cts.Token;

        // Reasoning summaries only come from a reasoning-capable model; pin gpt-5.5 (the fixture's
        // default resolves to a nano model that does not reason).
        const string model = "gpt-5.5";
        _output.WriteLine($"OpenAI model: {model}");

        using var agent = CopilotResponsesAgentFactory.Create(
            "copilot-responses-reasoning",
            _fixture.TokenProvider,
            CopilotResponsesTransport.Sse,
            _fixture.Session,
            _fixture.Options
        );

        var stream = await agent.GenerateReplyStreamingAsync(
            [new TextMessage { Role = Role.User, Text = "Think step by step, then answer: what is 17 * 23?" }],
            new GenerateReplyOptions
            {
                ModelId = model,
                MaxToken = 2048,
                ExtraProperties = System.Collections.Immutable.ImmutableDictionary<string, object?>.Empty.Add(
                    "Reasoning",
                    new ResponseReasoningOptions { Summary = "auto" }
                ),
            },
            cancellationToken
        );

        var reasoningUpdates = 0;
        var finalReasoning = 0;
        var reasoningText = new StringBuilder();
        await foreach (var message in stream.WithCancellation(cancellationToken))
        {
            if (message is ReasoningUpdateMessage ru)
            {
                reasoningUpdates++;
                _ = reasoningText.Append(ru.Reasoning);
            }
            else if (message is ReasoningMessage)
            {
                finalReasoning++;
            }
        }

        _output.WriteLine(
            $"reasoningUpdates={reasoningUpdates} finalReasoning={finalReasoning} reasoning=\"{reasoningText}\""
        );
        (reasoningUpdates + finalReasoning)
            .Should()
            .BeGreaterThan(0, "Copilot Responses should return a reasoning summary when reasoning is requested");
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
