using System.Net;
using AchieveAi.LmDotnetTools.LmCore.Http;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Agents;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Tests;

/// <summary>
///     Pre-stream retry coverage for <see cref="OpenAiResponsesClient.StreamResponseAsync"/>. A
///     transient 502 (and other 5xx/429) must be retried by reusing <see cref="HttpRetryHelper"/>,
///     building a FRESH <see cref="HttpRequestMessage"/> per attempt, and the SSE stream must still be
///     enumerated end-to-end. Persistent / non-retryable failures surface unchanged, cancellation is
///     honoured, and the response + stream are always disposed.
/// </summary>
public sealed class OpenAiResponsesClientRetryTests
{
    private const string ResponsesPath = "/v1/responses";

    [Fact]
    public async Task StreamResponseAsync_retries_once_after_502_then_yields_full_sequence()
    {
        var handler = new RecordingResponsesSseHandler(Sse(), failFirst: 1, failStatus: HttpStatusCode.BadGateway);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://mock.local/") };
        using var client = new OpenAiResponsesClient(http, retryOptions: RetryOptions.FastForTests);

        var events = await CollectAsync(client.StreamResponseAsync(Request()));

        events
            .Select(e => e.Type)
            .Should()
            .ContainInOrder(
                ResponseEventTypes.ResponseCreated,
                ResponseEventTypes.OutputTextDelta,
                ResponseEventTypes.ResponseCompleted
            );

        // Exactly one retry: the 502 attempt + the successful SSE attempt.
        handler.SendCount.Should().Be(2);
        handler.Requests.Should().HaveCount(2);

        // Each attempt is a DISTINCT, freshly-built request (a single HttpRequestMessage cannot be re-sent).
        ReferenceEquals(handler.Requests[0], handler.Requests[1]).Should().BeFalse();

        // Both attempts carried a readable JSON body and the SSE Accept header.
        handler.Bodies.Should().HaveCount(2);
        handler.Bodies.Should().OnlyContain(b => b.Contains("\"model\"", StringComparison.Ordinal));
        handler.AcceptHeaders.Should().OnlyContain(a => a.Contains("text/event-stream", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StreamResponseAsync_persistent_502_throws_BadGateway_after_max_retries()
    {
        var handler = new RecordingResponsesSseHandler(Sse(), failFirst: 99, failStatus: HttpStatusCode.BadGateway);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://mock.local/") };
        using var client = new OpenAiResponsesClient(http, retryOptions: RetryOptions.FastForTests);

        var act = async () => await CollectAsync(client.StreamResponseAsync(Request()));

        var ex = (await act.Should().ThrowAsync<HttpRequestException>()).Which;
        ex.StatusCode.Should().Be(HttpStatusCode.BadGateway);

        // Initial attempt + MaxRetries retries.
        handler.SendCount.Should().Be(RetryOptions.FastForTests.MaxRetries + 1);

        // Every failed response — including the final one — is disposed (no leaked connection).
        handler.Responses.Should().OnlyContain(r => r.Disposed);
    }

    [Fact]
    public async Task StreamResponseAsync_non_retryable_400_throws_immediately()
    {
        var handler = new RecordingResponsesSseHandler(Sse(), failFirst: 1, failStatus: HttpStatusCode.BadRequest);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://mock.local/") };
        using var client = new OpenAiResponsesClient(http, retryOptions: RetryOptions.FastForTests);

        var act = async () => await CollectAsync(client.StreamResponseAsync(Request()));

        var ex = (await act.Should().ThrowAsync<HttpRequestException>()).Which;
        ex.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // No retry on a non-retryable status: exactly one POST.
        handler.SendCount.Should().Be(1);

        // The final failed response is disposed (no leaked connection).
        handler.Responses.Should().ContainSingle().Which.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task StreamResponseAsync_consumer_break_disposes_response_and_stream()
    {
        var handler = new RecordingResponsesSseHandler(Sse(), failFirst: 0);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://mock.local/") };
        using var client = new OpenAiResponsesClient(http, retryOptions: RetryOptions.FastForTests);

        // Break after the first event so the iterator's finally must dispose the in-flight stream + response.
        await foreach (var _ in client.StreamResponseAsync(Request()))
        {
            break;
        }

        handler.SendCount.Should().Be(1);
        var response = handler.Responses.Should().ContainSingle().Subject;
        response.Disposed.Should().BeTrue("the iterator must dispose the HttpResponseMessage when the consumer stops early");
        response.BodyStream.Should().NotBeNull();
        response.BodyStream!.Disposed.Should().BeTrue("the iterator must dispose the SSE stream when the consumer stops early");
    }

    [Fact]
    public async Task StreamResponseAsync_cancel_during_backoff_throws_and_stops()
    {
        using var cts = new CancellationTokenSource();
        var handler = new RecordingResponsesSseHandler(Sse(), failFirst: 99, failStatus: HttpStatusCode.BadGateway)
        {
            // Cancel as soon as the first (failing) attempt is observed, so the backoff delay is cancelled.
            OnSend = n =>
            {
                if (n == 1)
                {
                    cts.Cancel();
                }
            },
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://mock.local/") };
        using var client = new OpenAiResponsesClient(http, retryOptions: RetryOptions.FastForTests);

        var act = async () => await CollectAsync(client.StreamResponseAsync(Request(), cts.Token));

        await act.Should().ThrowAsync<OperationCanceledException>();

        // The retry never fires a second POST after cancellation.
        handler.SendCount.Should().Be(1);
    }

    [Fact]
    public async Task StreamResponseAsync_emits_retry_warning_through_supplied_logger()
    {
        var logger = new ListLogger();
        var handler = new RecordingResponsesSseHandler(Sse(), failFirst: 1, failStatus: HttpStatusCode.BadGateway);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://mock.local/") };

        // Built the way the Copilot SSE factory builds it: a non-null logger + a custom responses path.
        using var client = new OpenAiResponsesClient(
            http,
            disposeClient: false,
            logger: logger,
            responsesPath: ResponsesPath,
            retryOptions: RetryOptions.FastForTests
        );

        _ = await CollectAsync(client.StreamResponseAsync(Request()));

        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning);
    }

    private static ResponseCreateRequest Request() =>
        new()
        {
            Model = "gpt-5.5",
            Input = [new ResponseInputItem { Role = "user", Content = [new ResponseInputContent { Text = "hi" }] }],
        };

    private static string Sse() =>
        "data: {\"type\":\"response.created\",\"sequence_number\":0,\"response\":{\"id\":\"resp_1\"}}\n\n"
        + "data: {\"type\":\"response.output_text.delta\",\"sequence_number\":1,\"item_id\":\"i\",\"output_index\":0,\"content_index\":0,\"delta\":\"hello\"}\n\n"
        + "data: {\"type\":\"response.completed\",\"sequence_number\":2,\"response\":{\"id\":\"resp_1\"}}\n\n";

    private static async Task<List<ResponseEvent>> CollectAsync(IAsyncEnumerable<ResponseEvent> stream)
    {
        var list = new List<ResponseEvent>();
        await foreach (var ev in stream)
        {
            list.Add(ev);
        }

        return list;
    }
}
