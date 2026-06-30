using System.Net;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Agents;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Auth;
using AchieveAi.LmDotnetTools.LmCore.Http;
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
///     chain) wrapping an SSE handler that fails once before streaming. The public factory has no
///     transport seam, so the SSE construction path is exercised directly here; a smoke test confirms
///     the public <see cref="CopilotResponsesAgentFactory.Create"/> accepts the retry seam.
/// </summary>
public sealed class CopilotResponsesSseRetryTests
{
    private sealed class StubTokenProvider : ICopilotTokenProvider
    {
        public Task<string> GetTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult("gho_test");
    }

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Entries.Add((logLevel, formatter(state, exception)));
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
    public void Factory_create_accepts_retry_options_for_sse_transport()
    {
        using var agent = CopilotResponsesAgentFactory.Create(
            "copilot-sse",
            new StubTokenProvider(),
            CopilotResponsesTransport.Sse,
            retryOptions: RetryOptions.FastForTests
        );

        agent.Should().NotBeNull();
    }
}
