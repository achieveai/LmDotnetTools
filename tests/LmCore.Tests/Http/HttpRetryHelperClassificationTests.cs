using System.Net;
using System.Text;
using AchieveAi.LmDotnetTools.LmCore.Http;
using AchieveAi.LmDotnetTools.LmTestUtils;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Http;

/// <summary>
///     Classification tests for <see cref="HttpRetryHelper.IsRetryableError" />. They prove retryability
///     is decided by the exception's HTTP status code when one is present, so a non-retryable status
///     (e.g. 400/401) is never retried even when its response body happens to contain retryable-looking
///     tokens such as "500", "Internal Server Error" or "timeout". Status-less transport exceptions still
///     fall back to substring detection.
/// </summary>
public class HttpRetryHelperClassificationTests
{
    [Fact]
    public void IsRetryableError_NonRetryableStatus_WithRetryableTokensInBody_ReturnsFalse()
    {
        // A 400 whose message embeds an upstream body mentioning "500 Internal Server Error" and "timeout"
        // must NOT be retried: a present status code wins over the message text.
        var exception = new HttpRequestException(
            "HTTP request failed with status BadRequest. Response body: 500 Internal Server Error and timeout",
            null,
            HttpStatusCode.BadRequest
        );

        HttpRetryHelper.IsRetryableError(exception).Should().BeFalse();
    }

    [Fact]
    public void IsRetryableError_ServerErrorStatus_ReturnsTrue()
    {
        // 5xx is retryable regardless of message text.
        var exception = new HttpRequestException("ignored", null, HttpStatusCode.InternalServerError);

        HttpRetryHelper.IsRetryableError(exception).Should().BeTrue();
    }

    [Fact]
    public void IsRetryableError_TooManyRequestsStatus_ReturnsTrue()
    {
        // 429 is retryable regardless of message text.
        var exception = new HttpRequestException("ignored", null, HttpStatusCode.TooManyRequests);

        HttpRetryHelper.IsRetryableError(exception).Should().BeTrue();
    }

    [Fact]
    public void IsRetryableError_NoStatusCode_TimeoutMessage_ReturnsTrue()
    {
        // Genuine transport failures from SendAsync carry no status code; substring detection still applies.
        var exception = new HttpRequestException("A connection timeout occurred");

        HttpRetryHelper.IsRetryableError(exception).Should().BeTrue();
    }

    [Fact]
    public void IsRetryableError_UnauthorizedStatus_ReturnsFalse()
    {
        // 401 is non-retryable; the "Unauthorized" text must not be misread as retryable.
        var exception = new HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized);

        HttpRetryHelper.IsRetryableError(exception).Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteHttpWithRetryAsync_NonRetryableStatus_WithRetryableTokensInBody_DoesNotRetry()
    {
        // A 400 response whose body contains retryable-looking tokens must be thrown immediately without
        // retrying, even though MaxRetries > 0. The handler is therefore invoked exactly once.
        var invocationCount = 0;
        var handler = new FakeHttpMessageHandler(
            (request, cancellationToken) =>
            {
                _ = Interlocked.Increment(ref invocationCount);
                var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("Internal Server Error 500 timeout", Encoding.UTF8, "text/plain"),
                };
                return Task.FromResult(response);
            }
        );
        using var httpClient = new HttpClient(handler);

        var httpOperation = new Func<Task<HttpResponseMessage>>(() => httpClient.GetAsync("http://localhost/test"));
        var responseProcessor = new Func<HttpResponseMessage, Task<string>>(response =>
            response.Content.ReadAsStringAsync()
        );

        var act = async () =>
            await HttpRetryHelper.ExecuteHttpWithRetryAsync(
                httpOperation,
                responseProcessor,
                NullLogger.Instance,
                RetryOptions.FastForTests
            );

        var thrown = await act.Should().ThrowAsync<HttpRequestException>();
        thrown.Which.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        invocationCount.Should().Be(1);
    }
}
