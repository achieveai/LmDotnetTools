using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Http;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// Pins the #693 contract: the context-window remediation verdict is a property of the EXCEPTION (type,
/// HTTP status, message), and the history size only breaks the tie for a transport abort. Lifecycle and
/// programming faults on a huge history must never be called an overflow.
/// </summary>
public class ProviderErrorClassifierTests
{
    private const long Large = ProviderErrorClassifier.LargeConversationTokenEstimate;
    private const long Small = 1_000;

    public static TheoryData<Exception> LifecycleAndProgrammingFaults =>
        [
            new ObjectDisposedException("HttpClient"),
            new InvalidOperationException(
                "Cannot send request: tool call 'call_1' is still deferred. Resolve all deferred tool calls via ResolveToolCallAsync before resuming."
            ),
            new ArgumentException("messages must not be empty", "messages"),
            new NotSupportedException("Streaming is not supported by this agent"),
            new NullReferenceException(),
            new JsonException("The JSON value could not be converted to System.String."),
            // A retry helper wrapping a lifecycle fault must not launder it into an overflow.
            new InvalidOperationException("provider call failed", new ObjectDisposedException("HttpClient")),
        ];

    [Theory]
    [MemberData(nameof(LifecycleAndProgrammingFaults))]
    public void LifecycleAndProgrammingFaults_AreNeverOverflow_EvenOnAHugeHistory(Exception fault)
    {
        ProviderErrorClassifier
            .ClassifyContextOverflow(fault, estimatedTokens: Large * 5)
            .Should()
            .Be(ContextOverflowVerdict.NotOverflow);
    }

    public static TheoryData<Exception> GenuineProviderOverflows =>
        [
            // Anthropic 400, exactly as HttpRetryHelper renders it.
            new HttpRequestException(
                "HTTP request failed with status BadRequest (Bad Request). Response body: {\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"prompt is too long: 213462 tokens > 200000 maximum\"}}",
                null,
                HttpStatusCode.BadRequest
            ),
            // OpenAI chat completions 400.
            new HttpRequestException(
                "HTTP request failed with status BadRequest (Bad Request). Response body: {\"error\":{\"message\":\"This model's maximum context length is 128000 tokens. However, your messages resulted in 131072 tokens.\",\"type\":\"invalid_request_error\",\"code\":\"context_length_exceeded\"}}",
                null,
                HttpStatusCode.BadRequest
            ),
            // Responses API 400.
            new HttpRequestException(
                "HTTP request failed with status BadRequest (Bad Request). Response body: {\"error\":{\"message\":\"Your input exceeds the context window of this model. Please adjust your input and try again.\"}}",
                null,
                HttpStatusCode.BadRequest
            ),
            // Anthropic 413 request_too_large — status alone is enough.
            new HttpRequestException(
                "HTTP request failed with status RequestEntityTooLarge (Request Entity Too Large)",
                null,
                HttpStatusCode.RequestEntityTooLarge
            ),
            // The provider error surfaced through the processor as a plain exception (Responses path).
            new InvalidOperationException("Responses API error: Your input exceeds the context window of this model"),
        ];

    [Theory]
    [MemberData(nameof(GenuineProviderOverflows))]
    public void GenuineProviderOverflow_IsOverflow_RegardlessOfHistorySize(Exception overflow)
    {
        ProviderErrorClassifier
            .ClassifyContextOverflow(overflow, estimatedTokens: Small)
            .Should()
            .Be(ContextOverflowVerdict.Overflow, "the provider said so; the estimate cannot veto it");
        ProviderErrorClassifier
            .ClassifyContextOverflow(overflow, estimatedTokens: Large)
            .Should()
            .Be(ContextOverflowVerdict.Overflow);
    }

    /// <summary>
    /// Overflow bodies the endpoints behind the sample host return before the stream starts, as captured in public
    /// reports. The Copilot rows are what gpt-5.6-* (Copilot Responses) and Copilot Claude (Messages) answer.
    /// </summary>
    public static TheoryData<string, HttpStatusCode, string> CapturedOverflowResponses =>
        new()
        {
            {
                "Anthropic Messages",
                HttpStatusCode.BadRequest,
                """{"type":"error","error":{"type":"invalid_request_error","message":"prompt is too long: 210503 tokens > 200000 maximum"},"request_id":"req_011CSLrpzw9u7G2f3ADrJFPA"}"""
            },
            {
                "OpenAI Chat Completions",
                HttpStatusCode.BadRequest,
                """{"error":{"type":"invalid_request_error","code":"context_length_exceeded","message":"Input tokens exceed the configured limit of 272000 tokens. Your messages resulted in 700007 tokens. Please reduce the length of the messages.","param":"messages"}}"""
            },
            {
                "OpenAI Responses",
                HttpStatusCode.BadRequest,
                """{"error":{"type":"invalid_request_error","code":"context_length_exceeded","message":"Your input exceeds the context window of this model. Please adjust your input and try again.","param":"input"}}"""
            },
            {
                "Copilot Responses and Chat Completions",
                HttpStatusCode.BadRequest,
                """{"error":{"message":"prompt token count of 168929 exceeds the limit of 168000","code":"model_max_prompt_tokens_exceeded"}}"""
            },
            {
                "Copilot Messages",
                HttpStatusCode.BadRequest,
                """{"type":"error","error":{"type":"invalid_request_error","message":"Request body is too large for model context window"}}"""
            },
            {
                "OpenRouter",
                HttpStatusCode.BadRequest,
                """{"error":{"message":"This endpoint's maximum context length is 1048576 tokens. However, you requested about 1060000 tokens (1043000 of text input, 1000 of tool input, 16000 in the output). Please reduce the length of either one, or use the \"middle-out\" transform to compress your prompt automatically.","code":400,"metadata":{"provider_name":null}}}"""
            },
            {
                "Anthropic Messages, input plus max_tokens",
                HttpStatusCode.BadRequest,
                """{"type":"error","error":{"type":"invalid_request_error","message":"input length and `max_tokens` exceed context limit: 188240 + 21333 > 200000, decrease input length or `max_tokens` and try again"}}"""
            },
        };

    [Theory]
    [MemberData(nameof(CapturedOverflowResponses))]
    public async Task ACapturedOverflowResponse_AsTheSharedRetryHelperThrowsIt_CarriesItsBody_AndIsOverflow(
        string endpoint,
        HttpStatusCode status,
        string body
    )
    {
        var thrown = await ThrownByTheRetryHelperAsync(status, body);

        thrown.StatusCode.Should().Be(status);
        thrown.Message.Should().Contain(body, "{0} must keep the body the classifier scans", endpoint);
        ProviderErrorClassifier
            .ClassifyContextOverflow(thrown, estimatedTokens: Small)
            .Should()
            .Be(ContextOverflowVerdict.Overflow, endpoint);
    }

    /// <summary>
    /// Limit errors a smaller conversation cannot fix: a rate limit counted in tokens, a server error that happens to
    /// mention the context window, and a context-limit refusal whose output reservation is at least the input.
    /// </summary>
    public static TheoryData<string, HttpStatusCode, string> LimitErrorsTheInputCannotFix =>
        new()
        {
            {
                "OpenAI tokens-per-minute rate limit",
                HttpStatusCode.TooManyRequests,
                """{"error":{"message":"Request too large for gpt-4o in organization org-abc on tokens per min (TPM): Limit 30000, Requested 45000. The input or output tokens must be reduced in order to run successfully.","type":"tokens","param":null,"code":"rate_limit_exceeded"}}"""
            },
            {
                "Anthropic rate limit",
                HttpStatusCode.TooManyRequests,
                """{"type":"error","error":{"type":"rate_limit_error","message":"This request would exceed the rate limit for your organization of 40,000 input tokens per minute."}}"""
            },
            {
                "Server error mentioning the context window",
                HttpStatusCode.InternalServerError,
                """{"error":{"message":"upstream failed while computing the context window","code":"server_error"}}"""
            },
            {
                "OpenRouter, output reservation dominates",
                HttpStatusCode.BadRequest,
                """{"error":{"message":"This endpoint's maximum context length is 1048576 tokens. However, you requested about 1048577 tokens (1 of text input, 1048576 in the output). Please reduce the length of either one, or use the \"middle-out\" transform to compress your prompt automatically.","code":400,"metadata":{"provider_name":null}}}"""
            },
            {
                "Anthropic, max_tokens dominates",
                HttpStatusCode.BadRequest,
                """{"type":"error","error":{"type":"invalid_request_error","message":"input length and `max_tokens` exceed context limit: 1200 + 200000 > 200000, decrease input length or `max_tokens` and try again"}}"""
            },
        };

    [Theory]
    [MemberData(nameof(LimitErrorsTheInputCannotFix))]
    public async Task ALimitErrorTheInputCannotFix_AsTheSharedRetryHelperThrowsIt_IsNotOverflow(
        string error,
        HttpStatusCode status,
        string body
    )
    {
        var thrown = await ThrownByTheRetryHelperAsync(status, body);

        ProviderErrorClassifier
            .ClassifyContextOverflow(thrown, estimatedTokens: Large)
            .Should()
            .Be(ContextOverflowVerdict.NotOverflow, error);
    }

    [Fact]
    public void AnOutputDominatedLimitError_WithNoStatus_IsNotOverflow()
    {
        var rethrown = new InvalidOperationException(
            "provider error: This endpoint's maximum context length is 8192 tokens. However, you requested about 9000 tokens (900 of text input, 8100 in the output)."
        );

        ProviderErrorClassifier
            .ClassifyContextOverflow(rethrown, estimatedTokens: Small)
            .Should()
            .Be(ContextOverflowVerdict.NotOverflow, "shrinking the conversation cannot free the output reservation");
    }

    [Theory]
    [InlineData(
        "maximum context length is 8192 tokens. However, you requested about 9000 tokens (900 of text input, 123456789012345678901234 in the output)."
    )]
    [InlineData(
        "maximum context length is 8192 tokens. However, you requested about 9000 tokens (123456789012345678901234 of text input, 8100 in the output)."
    )]
    [InlineData(
        "maximum context length is 8192 tokens. However, you requested about 9000 tokens (9223372036854775807 of text input, 9223372036854775807 of tool input, 1 in the output)."
    )]
    [InlineData("input length and `max_tokens` exceed context limit: 123456789012345678901234 + 21333 > 200000")]
    public void ATokenSplitTooLargeToCount_NeverThrows_AndFallsBackToTheSignature(string message)
    {
        var rethrown = new InvalidOperationException(message);

        ProviderErrorClassifier
            .ClassifyContextOverflow(rethrown, estimatedTokens: Small)
            .Should()
            .Be(
                ContextOverflowVerdict.Overflow,
                "a split the classifier cannot count must not mask the provider error"
            );
    }

    /// <summary>
    /// The exception a failed status becomes. Every sample-host client (OpenClient, AnthropicClient,
    /// OpenAiResponsesClient) sends through this helper, the one place that happens.
    /// </summary>
    private static async Task<HttpRequestException> ThrownByTheRetryHelperAsync(HttpStatusCode status, string body)
    {
        var act = () =>
            HttpRetryHelper.ExecuteHttpWithRetryAsync<string>(
                () => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) }),
                _ => throw new InvalidOperationException("a failed status never reaches the processor"),
                NullLogger.Instance,
                RetryOptions.FastForTests
            );

        return (await act.Should().ThrowAsync<HttpRequestException>()).Which;
    }

    public static TheoryData<Exception> TransportAborts =>
        [
            new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely."),
            new HttpRequestException(HttpRequestError.ResponseEnded, "The response ended prematurely."),
            new HttpRequestException("The response ended prematurely."),
            new IOException(
                "Unable to read data from the transport connection: An existing connection was forcibly closed by the remote host."
            ),
            // Reset-family socket codes are evidence on their own: Windows and Linux render different
            // messages for the same code, so the code — not the text — is what is matched.
            new SocketException((int)SocketError.ConnectionReset),
            new SocketException((int)SocketError.ConnectionAborted),
            // Wrapped by the retry helper.
            new HttpRequestException(
                "An error occurred while sending the request.",
                new IOException("The response ended prematurely.")
            ),
        ];

    [Theory]
    [MemberData(nameof(TransportAborts))]
    public void TransportAbort_OnLargeHistory_IsLikelyOverflow(Exception abort)
    {
        ProviderErrorClassifier
            .ClassifyContextOverflow(abort, estimatedTokens: Large)
            .Should()
            .Be(ContextOverflowVerdict.LikelyOverflow);
    }

    [Theory]
    [MemberData(nameof(TransportAborts))]
    public void TransportAbort_OnSmallHistory_IsNotOverflow(Exception abort)
    {
        ProviderErrorClassifier
            .ClassifyContextOverflow(abort, estimatedTokens: Large - 1)
            .Should()
            .Be(ContextOverflowVerdict.NotOverflow, "a network blip on a small conversation is just a network blip");
    }

    /// <summary>
    /// Transport-layer faults that carry no evidence the RESPONSE was aborted: a storage fault, an unreachable
    /// host, a connection that never opened, or an availability status with a bare body. Nothing about these
    /// says "the request was too big", so the history size must not promote them to an overflow verdict.
    /// </summary>
    public static TheoryData<Exception> AmbiguousTransportFaults =>
        [
            new IOException("The device is not ready."),
            new SocketException((int)SocketError.HostUnreachable),
            // ConnectionError means the connection could not be established at all — availability, not an
            // aborted response.
            new HttpRequestException(HttpRequestError.ConnectionError, "An error occurred while sending the request."),
            new HttpRequestException(
                "HTTP request failed with status BadGateway (Bad Gateway)",
                null,
                HttpStatusCode.BadGateway
            ),
            new HttpRequestException(
                "HTTP request failed with status ServiceUnavailable (Service Unavailable)",
                null,
                HttpStatusCode.ServiceUnavailable
            ),
            new HttpRequestException(
                "HTTP request failed with status GatewayTimeout (Gateway Timeout)",
                null,
                HttpStatusCode.GatewayTimeout
            ),
            new HttpRequestException(
                "HTTP request failed with status RequestTimeout (Request Timeout)",
                null,
                HttpStatusCode.RequestTimeout
            ),
        ];

    [Theory]
    [MemberData(nameof(AmbiguousTransportFaults))]
    public void AmbiguousTransportFault_OnLargeHistory_IsNotOverflow(Exception fault)
    {
        ProviderErrorClassifier
            .ClassifyContextOverflow(fault, estimatedTokens: Large * 2)
            .Should()
            .Be(
                ContextOverflowVerdict.NotOverflow,
                "there is no evidence the response was aborted for size, however large the history"
            );
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public void NonOverflowStatus_OnLargeHistory_IsNotOverflow(HttpStatusCode status)
    {
        var failure = new HttpRequestException($"HTTP request failed with status {status}", null, status);

        ProviderErrorClassifier
            .ClassifyContextOverflow(failure, estimatedTokens: Large * 2)
            .Should()
            .Be(ContextOverflowVerdict.NotOverflow, "the server answered with a status that is not about size");
    }

    [Fact]
    public void AggregateException_IsFlattened_AndTheStrongestVerdictWins()
    {
        var aggregate = new AggregateException(
            new ObjectDisposedException("HttpClient"),
            new HttpRequestException(
                "HTTP request failed with status BadRequest (Bad Request). Response body: prompt is too long",
                null,
                HttpStatusCode.BadRequest
            )
        );

        ProviderErrorClassifier
            .ClassifyContextOverflow(aggregate, estimatedTokens: Small)
            .Should()
            .Be(ContextOverflowVerdict.Overflow);
    }

    [Fact]
    public void NullException_Throws()
    {
        var act = () => ProviderErrorClassifier.ClassifyContextOverflow(null!, estimatedTokens: Large);
        act.Should().Throw<ArgumentNullException>();
    }
}
