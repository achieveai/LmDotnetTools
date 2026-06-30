using System.Net;
using AchieveAi.LmDotnetTools.LmCore.Http;
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

    private static Task<HttpResponseMessage> NewResponse(
        List<TrackingHttpResponseMessage> sink,
        HttpStatusCode status
    )
    {
        var response = new TrackingHttpResponseMessage(status) { Content = new StringContent($"error {(int)status}") };
        sink.Add(response);
        return Task.FromResult<HttpResponseMessage>(response);
    }
}

/// <summary>An <see cref="HttpResponseMessage"/> that records whether it has been disposed.</summary>
internal sealed class TrackingHttpResponseMessage : HttpResponseMessage
{
    public TrackingHttpResponseMessage(HttpStatusCode statusCode)
        : base(statusCode) { }

    private int _disposeCount;

    public bool Disposed => Volatile.Read(ref _disposeCount) > 0;

    protected override void Dispose(bool disposing)
    {
        _ = Interlocked.Increment(ref _disposeCount);
        base.Dispose(disposing);
    }
}
