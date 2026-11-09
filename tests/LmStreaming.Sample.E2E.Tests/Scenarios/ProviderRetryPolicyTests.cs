using System.Net;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.E2E.Tests.Scenarios;

/// <summary>
///     The host's <c>LmStreaming:ProviderRetry</c> policy reaches the provider client the sample really
///     builds (#930 P1 item 1). With <c>MaxRetries: 0</c> one 503 ends the turn: the request is not sent
///     again, so the answer the provider would have given on a second try never appears. With the library
///     default the same outage would be retried and answered.
/// </summary>
public sealed class ProviderRetryPolicyTests
{
    /// <summary>Fails the first request with 503, then lets the scripted provider answer.</summary>
    private sealed class OutageOnceHandler : DelegatingHandler
    {
        private int _requests;

        public OutageOnceHandler(HttpMessageHandler inner)
            : base(inner) { }

        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            return Interlocked.Increment(ref _requests) == 1
                ? Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    {
                        Content = new StringContent(
                            """{"error":{"message":"upstream unavailable","type":"server_error"}}"""
                        ),
                    }
                )
                : base.SendAsync(request, cancellationToken);
        }
    }

    [Fact]
    public async Task AZeroRetryPolicy_EndsTheTurnOnTheFirstOutage_WithoutSendingAgain()
    {
        var responder = ScriptedSseResponder
            .New()
            .ForRole("provider", _ => true)
            .Turn(t => t.Text("Answered on the second try."))
            .Build();
        var handler = new OutageOnceHandler(responder.AsOpenAiHandler());
        using var factory = new E2EWebAppFactory(
            "test",
            new ScriptedBuilder(handler),
            new Dictionary<string, string?> { ["LmStreaming:ProviderRetry:MaxRetries"] = "0" }
        );

        var socket = await factory.ConnectWebSocketAsync($"retry-policy-{Guid.NewGuid():N}");
        await using var client = new WebSocketTestClient(socket);
        await client.SendUserMessageAsync("say hello");
        using var frames = await client.CollectUntilDoneAsync(TimeSpan.FromSeconds(30));

        handler.Requests.Should().Be(1, "MaxRetries 0 sends the request once");
        frames.ConcatText().Should().NotContain("second try");
        frames
            .Should()
            .Contain(
                frame => IsErrorCompletion(frame),
                "the outage ends the run as an error, so the provider was asked and failed"
            );
    }

    private static bool IsErrorCompletion(JsonDocument frame) =>
        frame.RootElement.ValueKind == JsonValueKind.Object
        && frame.RootElement.TryGetProperty("IsError", out var isError)
        && isError.ValueKind == JsonValueKind.True;
}
