namespace AchieveAi.LmDotnetTools.LmConfig.Http;

/// <summary>
///     Simple retry handler that retries on transient failures (HTTP 5xx or network errors).
/// </summary>
public sealed class RetryHandler : DelegatingHandler
{
    private readonly TimeSpan _delay;
    private readonly int _maxAttempts;

    public RetryHandler(int maxAttempts = 3, TimeSpan? delay = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        _maxAttempts = maxAttempts;
        _delay = delay ?? TimeSpan.FromMilliseconds(200);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if ((int)response.StatusCode < 500 || attempt == _maxAttempts)
                {
                    return response;
                }
            }
            catch when (attempt < _maxAttempts)
            {
                // swallow and retry
            }

            await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
        }
    }
}
