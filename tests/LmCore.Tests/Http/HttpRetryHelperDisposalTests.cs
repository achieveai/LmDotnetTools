using System.Net;
using AchieveAi.LmDotnetTools.LmCore.Http;
using AchieveAi.LmDotnetTools.LmTestUtils.Http;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Http;

/// <summary>
///     Pins the lifetime contract of <see cref="HttpRetryHelper"/>'s HTTP retry overload: the
///     FINAL failed response (non-retryable status, or after retries are exhausted) must be disposed
///     before the helper throws, so the connection is not leaked. The success path is unaffected
///     (the caller owns and disposes the returned response).
/// </summary>
public sealed class HttpRetryHelperDisposalTests
{
    [Fact]
    public async Task ExecuteHttpWithRetryAsync_non_retryable_disposes_final_failed_response()
    {
        var responses = new List<TrackingHttpResponseMessage>();

        var act = async () =>
            await HttpRetryHelper.ExecuteHttpWithRetryAsync(
                () => NewResponse(responses, HttpStatusCode.BadRequest),
                resp => Task.FromResult(resp),
                NullLogger.Instance,
                RetryOptions.FastForTests
            );

        await act.Should().ThrowAsync<HttpRequestException>();

        // 400 is non-retryable: exactly one response, and it must be disposed before the throw.
        responses.Should().ContainSingle();
        responses[0].Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteHttpWithRetryAsync_exhausted_retries_disposes_every_failed_response()
    {
        var responses = new List<TrackingHttpResponseMessage>();

        var act = async () =>
            await HttpRetryHelper.ExecuteHttpWithRetryAsync(
                () => NewResponse(responses, HttpStatusCode.InternalServerError),
                resp => Task.FromResult(resp),
                NullLogger.Instance,
                RetryOptions.FastForTests
            );

        await act.Should().ThrowAsync<HttpRequestException>();

        // Initial attempt + MaxRetries; the intermediate retryable responses AND the final one disposed.
        responses.Should().HaveCount(RetryOptions.FastForTests.MaxRetries + 1);
        responses.Should().OnlyContain(r => r.Disposed);
    }

    [Fact]
    public async Task ExecuteHttpWithRetryAsync_cancellation_reading_error_body_surfaces_as_cancellation()
    {
        var responses = new List<TrackingHttpResponseMessage>();

        var act = async () =>
            await HttpRetryHelper.ExecuteHttpWithRetryAsync(
                () =>
                {
                    var resp = new TrackingHttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new CancelOnReadContent(),
                    };
                    responses.Add(resp);
                    return Task.FromResult<HttpResponseMessage>(resp);
                },
                resp => Task.FromResult(resp),
                NullLogger.Instance,
                RetryOptions.FastForTests
            );

        // A cancellation raised while reading the final error body must surface AS cancellation, not be
        // repackaged into an HttpRequestException — and the response is still disposed.
        await act.Should().ThrowAsync<OperationCanceledException>();
        responses.Should().ContainSingle();
        responses[0].Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteHttpWithRetryAsync_body_read_failure_keeps_status_and_does_not_retry()
    {
        var responses = new List<TrackingHttpResponseMessage>();
        var attempts = 0;

        var act = async () =>
            await HttpRetryHelper.ExecuteHttpWithRetryAsync(
                () =>
                {
                    attempts++;
                    var resp = new TrackingHttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        // The body read fails with an exception whose message LOOKS retryable.
                        Content = new ThrowOnReadContent(new HttpRequestException("502 Bad Gateway")),
                    };
                    responses.Add(resp);
                    return Task.FromResult<HttpResponseMessage>(resp);
                },
                resp => Task.FromResult(resp),
                NullLogger.Instance,
                RetryOptions.FastForTests
            );

        var ex = (await act.Should().ThrowAsync<HttpRequestException>()).Which;

        // The ORIGINAL 400 status is preserved, and a non-retryable status is NOT retried just because the
        // body read threw a retryable-looking exception (it must not escape to the outer retry catch).
        ex.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        attempts.Should().Be(1);
        responses.Should().ContainSingle().Which.Disposed.Should().BeTrue();
    }

    private static Task<HttpResponseMessage> NewResponse(
        List<TrackingHttpResponseMessage> sink,
        HttpStatusCode status
    )
    {
        var response = new TrackingHttpResponseMessage(status) { Content = new StringContent($"error {(int)status}") };
        sink.Add(response);
        return Task.FromResult<HttpResponseMessage>(response);
    }

    /// <summary>Content that throws <see cref="OperationCanceledException"/> when its body is read.</summary>
    private sealed class CancelOnReadContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new OperationCanceledException();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>Content that throws a supplied exception when its body is read.</summary>
    private sealed class ThrowOnReadContent : HttpContent
    {
        private readonly Exception _exception;

        public ThrowOnReadContent(Exception exception) => _exception = exception;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw _exception;

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
