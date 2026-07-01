using System.Net;
using AchieveAi.LmDotnetTools.LmCore.Http;

namespace CodeReviewDaemon.Sample.Workspace;

/// <summary>
/// The daemon's outbound HTTP resilience layer (PR #121 M7). As the OUTERMOST handler in the provider/
/// publisher pipeline (before <see cref="OperationPolicyHandler"/>), it retries a transient failure —
/// <c>429 Too Many Requests</c> or a <c>5xx</c> — with bounded exponential backoff, honoring a
/// <c>Retry-After</c> header when the server supplies one.
/// <para>
/// <b>POST safety.</b> A read (GET/HEAD) is idempotent and retried on any transient status. A POST is
/// retried ONLY on <c>429</c>, where the server explicitly rejected the request before processing it (so
/// a retry cannot double-apply). Cross-attempt POST resilience for the ambiguous 5xx case is deliberately
/// left to the higher-level exactly-once guard (<c>ReviewPoster</c>'s idempotency key + provider-side
/// backstop scan), which is re-checked on the next poll/reconcile — never blindly re-POSTed here. A
/// policy denial (<see cref="OperationDeniedException"/>) is not a status-code failure, so it propagates
/// immediately without retry.
/// </para>
/// </summary>
internal sealed class RetryHandler : DelegatingHandler
{
    private const int MaxRetries = 3;
    private static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(20);

    private readonly ILogger<RetryHandler> _logger;
    private readonly TimeSpan _baseDelay;

    public RetryHandler(ILogger<RetryHandler> logger, TimeSpan? baseDelay = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _baseDelay = baseDelay ?? BaseDelay;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // Buffer any request content up front so the request can be safely re-sent on retry.
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        for (var attempt = 0; ; attempt++)
        {
            using var attemptRequest = attempt == 0 ? request : Clone(request, body);
            var response = await base.SendAsync(attemptRequest, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode
                || attempt >= MaxRetries
                || !ShouldRetry(request.Method, response.StatusCode))
            {
                // Detach the response's request so disposing attemptRequest does not dispose live content.
                response.RequestMessage = null;
                return response;
            }

            var delay = RetryAfter(response) ?? Backoff(attempt);
            _logger.LogWarning(
                "{Method} {Uri} returned {Status}; retrying in {Delay}ms (attempt {Attempt}/{Max}).",
                request.Method,
                request.RequestUri,
                (int)response.StatusCode,
                delay.TotalMilliseconds,
                attempt + 1,
                MaxRetries);
            response.Dispose();
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>GET/HEAD retry on any transient status; POST only on 429 (server rejected pre-processing).</summary>
    private static bool ShouldRetry(HttpMethod method, HttpStatusCode status)
    {
        if (status == HttpStatusCode.TooManyRequests)
        {
            return true;
        }

        var idempotent = method == HttpMethod.Get || method == HttpMethod.Head;
        return idempotent && HttpRetryHelper.IsRetryableStatusCode(status);
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null)
        {
            return null;
        }

        if (retryAfter.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return Cap(delta);
        }

        if (retryAfter.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? Cap(wait) : TimeSpan.Zero;
        }

        return null;
    }

    private TimeSpan Backoff(int attempt) =>
        Cap(TimeSpan.FromMilliseconds(_baseDelay.TotalMilliseconds * Math.Pow(2, attempt)));

    private static TimeSpan Cap(TimeSpan delay) => delay > MaxDelay ? MaxDelay : delay;

    private static HttpRequestMessage Clone(HttpRequestMessage request, byte[]? body)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version };

        if (body is not null)
        {
            var content = new ByteArrayContent(body);
            foreach (var header in request.Content!.Headers)
            {
                _ = content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            clone.Content = content;
        }

        foreach (var header in request.Headers)
        {
            _ = clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        // Preserve request options — critically the SandboxOperation tag the OperationPolicyHandler reads.
        foreach (var option in (IDictionary<string, object?>)request.Options)
        {
            ((IDictionary<string, object?>)clone.Options)[option.Key] = option.Value;
        }

        return clone;
    }
}
