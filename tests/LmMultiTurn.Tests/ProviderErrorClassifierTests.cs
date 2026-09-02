using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using FluentAssertions;
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

    public static TheoryData<Exception> TransportAborts =>
        [
            new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely."),
            new HttpRequestException(HttpRequestError.ResponseEnded, "The response ended prematurely."),
            new HttpRequestException(HttpRequestError.ConnectionError, "An error occurred while sending the request."),
            new HttpRequestException("The response ended prematurely."),
            new IOException(
                "Unable to read data from the transport connection: An existing connection was forcibly closed by the remote host."
            ),
            new SocketException((int)SocketError.ConnectionReset),
            new HttpRequestException(
                "HTTP request failed with status BadGateway (Bad Gateway)",
                null,
                HttpStatusCode.BadGateway
            ),
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
