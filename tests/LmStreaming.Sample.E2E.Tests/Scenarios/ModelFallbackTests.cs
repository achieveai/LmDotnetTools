using System.Net;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.E2E.Tests.Infrastructure;
using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.E2E.Tests.Scenarios;

/// <summary>
///     Opt-in model fallback through the sample's real composition (#930 P1 item 8): the conversation's
///     provider is down, so the turn is answered by the next provider listed under
///     <c>LmStreaming:ModelFallback</c>, asked for that provider's own model, and the spend is booked to
///     the model that ran. Without the setting the same outage fails the turn, as it always has.
/// </summary>
public sealed class ModelFallbackTests
{
    // GetModelIdForProvider("test-anthropic"): the fallback is asked for ITS model, not the primary's "test-model".
    private const string FallbackModel = "claude-sonnet-4-5-20250929";

    [Fact]
    public async Task AProviderOutage_IsAnsweredByTheConfiguredFallback_AndBilledToIt()
    {
        var responder = ScriptedSseResponder
            .New()
            .ForRole("fallback", ctx => ctx.RequestBody.GetProperty("model").GetString() == FallbackModel)
            .Turn(t => t.Text("Answered by the fallback model."))
            .Build();
        var builder = new OutageThenFallbackBuilder(responder);
        using var factory = new E2EWebAppFactory(
            "test",
            builder,
            new Dictionary<string, string?> { ["LmStreaming:ModelFallback:test:0"] = "test-anthropic" }
        );

        var threadId = $"fallback-{Guid.NewGuid():N}";
        var socket = await factory.ConnectWebSocketAsync(threadId);
        await using var client = new WebSocketTestClient(socket);
        await client.SendUserMessageAsync("say hello");
        using var frames = await client.CollectUntilDoneAsync(TimeSpan.FromSeconds(30));

        frames.ConcatText().Should().Contain("Answered by the fallback model.");
        builder.PrimaryRequests.Should().BeGreaterThan(0, "the conversation's own provider was asked first");
        responder.RemainingTurns["fallback"].Should().Be(0, "the fallback request asked for the fallback's model");

        using var http = factory.CreateClient();
        var perModel = await ReadPerModelUsageAsync(http, threadId);
        perModel.Should().Equal([FallbackModel], "usage is billed to the model that ran, not the one that was down");
    }

    [Fact]
    public async Task WithoutTheSetting_TheSameOutageFailsTheTurn()
    {
        var responder = ScriptedSseResponder
            .New()
            .ForRole("fallback", _ => true)
            .Turn(t => t.Text("Answered by the fallback model."))
            .Build();
        var builder = new OutageThenFallbackBuilder(responder);
        using var factory = new E2EWebAppFactory("test", builder);

        var threadId = $"no-fallback-{Guid.NewGuid():N}";
        var socket = await factory.ConnectWebSocketAsync(threadId);
        await using var client = new WebSocketTestClient(socket);
        await client.SendUserMessageAsync("say hello");
        using var frames = await client.CollectUntilDoneAsync(TimeSpan.FromSeconds(30));

        frames.ConcatText().Should().NotContain("fallback model");
        responder.RemainingTurns["fallback"].Should().Be(1, "fallback is opt-in; nothing else was asked");
    }

    /// <summary>Polls the conversation usage aggregate (persisted after the turn) for its model ids.</summary>
    private static async Task<IReadOnlyList<string>> ReadPerModelUsageAsync(HttpClient http, string threadId)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            using var response = await http.GetAsync($"/api/conversations/{threadId}/usage", cts.Token);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
                return
                [
                    .. body
                        .RootElement.GetProperty("perModel")
                        .EnumerateArray()
                        .Select(row => row.GetProperty("modelId").GetString()!),
                ];
            }

            await Task.Delay(50, cts.Token);
        }
    }

    /// <summary>
    ///     The OpenAI-wire provider (<c>test</c>) answers every request with 503; the Anthropic-wire
    ///     provider (<c>test-anthropic</c>) is served by the scripted responder, whose stream reports usage.
    /// </summary>
    private sealed class OutageThenFallbackBuilder(ScriptedSseResponder responder) : ITestAgentBuilder
    {
        private int _primaryRequests;

        public int PrimaryRequests => Volatile.Read(ref _primaryRequests);

        public HttpMessageHandler CreateHandler(string providerMode, ILoggerFactory loggerFactory) =>
            string.Equals(providerMode, "test-anthropic", StringComparison.OrdinalIgnoreCase)
                ? responder.AsAnthropicHandler()
                : new OutageHandler(() => Interlocked.Increment(ref _primaryRequests));

        public SubAgentOptions? CreateSubAgentOptions(
            ILoggerFactory loggerFactory,
            Func<IStreamingAgent> providerAgentFactory
        ) => null;
    }

    private sealed class OutageHandler(Action onRequest) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            onRequest();
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent(
                        """{"error":{"message":"upstream unavailable","type":"server_error"}}"""
                    ),
                }
            );
        }
    }
}
