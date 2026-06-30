using System.Net;
using AchieveAi.LmDotnetTools.LmCore.Http;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Http;

/// <summary>
///     Cancellation tests for <see cref="HttpRetryHelper" />. They prove the final, non-retryable
///     error path reads the response body under the caller's cancellation token instead of hanging on
///     a body that never completes.
/// </summary>
public class HttpRetryHelperCancellationTests
{
    [Fact(Timeout = 15000)]
    public async Task ExecuteHttpWithRetryAsync_NonRetryableErrorWithBlockingBody_HonorsCancellationToken()
    {
        // A non-retryable (400) response whose error body never finishes reading. The final error-body
        // read must observe the cancellation token rather than hanging forever, so cancelling the token
        // surfaces an OperationCanceledException promptly.
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(50);

        var httpOperation = new Func<Task<HttpResponseMessage>>(() =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new BlockingHttpContent() })
        );
        var responseProcessor = new Func<HttpResponseMessage, Task<string>>(response =>
            response.Content.ReadAsStringAsync()
        );

        var act = async () =>
            await HttpRetryHelper.ExecuteHttpWithRetryAsync(
                httpOperation,
                responseProcessor,
                NullLogger.Instance,
                RetryOptions.FastForTests,
                cts.Token
            );

        _ = await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    ///     HTTP content whose body read blocks until the supplied cancellation token fires, modelling an
    ///     upstream error body that never completes.
    /// </summary>
    private sealed class BlockingHttpContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken
        ) => Task.Delay(Timeout.Infinite, cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
