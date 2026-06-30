using System.Net;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Agents;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Auth;
using AchieveAi.LmDotnetTools.LmCore.Http;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Agents;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.GithubCopilotProvider.Tests.Agents;

/// <summary>
///     The Copilot SSE transport (the construction path of
///     <see cref="CopilotResponsesAgentFactory"/>) must retry a transient pre-stream 502 and surface
///     the retry through the forwarded logger. Built the way <c>CreateSseClient</c> builds it — a
///     <see cref="CopilotHttpClientFactory"/> client (so the Copilot auth/header handler is in the
///     chain) wrapping an SSE handler that fails once before streaming. The real internal
///     <c>CreateSseClient</c> construction path is exercised directly through an injected transport
///     handler to prove it forwards both <c>retryOptions</c> and <c>logger</c> into the client.
/// </summary>
public sealed class CopilotResponsesSseRetryTests
{
    private sealed class StubTokenProvider : ICopilotTokenProvider
    {
        public Task<string> GetTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult("gho_test");
    }

    private static ResponseCreateRequest Request() =>
        new()
        {
            Model = "gpt-5.5",
            Input = [new ResponseInputItem { Role = "user", Content = [new ResponseInputContent { Text = "hi" }] }],
        };

    [Fact]
    public async Task Copilot_sse_path_retries_502_and_logs_through_forwarded_logger()
    {
        var sse = new OpenAiResponsesTestSseMessageHandler
        {
            ChunkDelayMs = 0,
            FailFirstCount = 1,
            FailStatusCode = HttpStatusCode.BadGateway,
        };

        // CopilotHttpClientFactory puts the auth + Copilot headers handler in front of the SSE handler,
        // exactly as the SSE transport is wired by the factory.
        var httpClient = CopilotHttpClientFactory.Create(
            "https://copilot.test",
            new StubTokenProvider(),
            new CopilotSessionContext("m", "s"),
            new CopilotOptions(),
            innerHandler: sse
        );

        var logger = new ListLogger();
        using var client = new OpenAiResponsesClient(
            httpClient,
            disposeClient: true,
            logger: logger,
            responsesPath: "/responses",
            retryOptions: RetryOptions.FastForTests
        );

        var events = new List<ResponseEvent>();
        await foreach (var ev in client.StreamResponseAsync(Request()))
        {
            events.Add(ev);
        }

        // The 502 was retried and the SSE stream was read end-to-end.
        events.Should().NotBeEmpty();
        events[^1].Type.Should().Be(ResponseEventTypes.ResponseCompleted);

        // The forwarded logger captured the retry warning (it only fires on a retry).
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task CreateSseClient_forwards_retry_options_and_logger()
    {
        var sse = new OpenAiResponsesTestSseMessageHandler
        {
            ChunkDelayMs = 0,
            FailFirstCount = 1,
            FailStatusCode = HttpStatusCode.BadGateway,
        };
        var logger = new ListLogger();

        // Drive the REAL CreateSseClient construction path (not a hand-rolled replica) through an injected
        // transport handler, so a regression that stopped forwarding retryOptions/logger would fail here.
        using var client = CopilotResponsesAgentFactory.CreateSseClient(
            "https://copilot.test",
            new StubTokenProvider(),
            new CopilotSessionContext("m", "s"),
            new CopilotOptions(),
            logger,
            RetryOptions.FastForTests,
            innerHandler: sse
        );

        var events = new List<ResponseEvent>();
        await foreach (var ev in client.StreamResponseAsync(Request()))
        {
            events.Add(ev);
        }

        events.Should().NotBeEmpty();
        events[^1].Type.Should().Be(ResponseEventTypes.ResponseCompleted);
        logger
            .Entries.Should()
            .Contain(e => e.Level == LogLevel.Warning, "the forwarded logger must observe the retry warning");
    }

    [Fact]
    public async Task CreateSseClient_forwarded_retry_options_disable_retry_when_max_retries_zero()
    {
        var sse = new OpenAiResponsesTestSseMessageHandler
        {
            ChunkDelayMs = 0,
            FailFirstCount = 1,
            FailStatusCode = HttpStatusCode.BadGateway,
        };

        // MaxRetries=0: the single 502 must surface immediately. If CreateSseClient dropped the forwarded
        // retryOptions and fell back to RetryOptions.Default, the 502 would be retried and SUCCEED — so this
        // pins the retryOptions wiring (not just that *some* retry happens).
        var noRetry = RetryOptions.FastForTests with { MaxRetries = 0 };
        using var client = CopilotResponsesAgentFactory.CreateSseClient(
            "https://copilot.test",
            new StubTokenProvider(),
            new CopilotSessionContext("m", "s"),
            new CopilotOptions(),
            logger: null,
            retryOptions: noRetry,
            innerHandler: sse
        );

        var act = async () =>
        {
            await foreach (var _ in client.StreamResponseAsync(Request())) { }
        };

        var ex = (await act.Should().ThrowAsync<HttpRequestException>()).Which;
        ex.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }
}
