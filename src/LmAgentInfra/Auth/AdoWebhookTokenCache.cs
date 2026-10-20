using System.Runtime.ExceptionServices;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Auth;

/// <summary>Shares webhook refreshes and remembers only tokens outside the ten-minute expiry margin.</summary>
internal sealed class AdoWebhookTokenCache(TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FailurePause = TimeSpan.FromSeconds(5);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private long _generation;
    private readonly HashSet<long> _suspensions = [];
    private string? _account;
    private OAuthAccessToken? _token;
    private Task<OAuthAccessToken>? _refresh;
    private CancellationTokenSource? _refreshCancellation;
    private ExceptionDispatchInfo? _failure;
    private DateTimeOffset _retryAfter;

    internal long Generation
    {
        get
        {
            lock (_gate)
            {
                return _generation;
            }
        }
    }

    internal async Task<OAuthAccessToken> GetAsync(
        string account,
        long generation,
        Func<CancellationToken, Task<OAuthAccessToken>> acquire,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();
        Task<OAuthAccessToken> pending;
        lock (_gate)
        {
            EnsureCurrent(generation);
            if (_account != account)
            {
                if (_refresh is not null)
                {
                    throw new IOException("ADO account changed during webhook refresh.");
                }

                _token = null;
                _failure = null;
                _retryAfter = default;
                _account = account;
            }

            if (HasLifetime(_token))
            {
                return _token!;
            }

            if (_refresh is not null)
            {
                pending = _refresh;
            }
            else
            {
                if (_failure is not null && _time.GetUtcNow() < _retryAfter)
                {
                    _failure.Throw();
                }

                var completion = new TaskCompletionSource<OAuthAccessToken>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
                pending = completion.Task;
                _refresh = pending;
                _refreshCancellation = new CancellationTokenSource(RefreshTimeout, _time);
                // Observe a failure even when every waiting HTTP request has disconnected.
                _ = pending.ContinueWith(
                    task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default
                );
                _ = RefreshAsync(account, generation, acquire, completion, _refreshCancellation);
            }
        }

        var result = await pending.WaitAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            EnsureCurrent(generation);
            if (_account != account || !HasLifetime(result))
            {
                throw new IOException("ADO webhook token is no longer usable.");
            }

            return result;
        }
    }

    internal async Task<long> SuspendAsync()
    {
        long generation;
        Task<OAuthAccessToken>? refresh;
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            generation = ++_generation;
            _suspensions.Add(generation);
            _token = null;
            _account = null;
            _failure = null;
            _retryAfter = default;
            refresh = _refresh;
            cancellation = _refreshCancellation;
        }

        if (cancellation is not null)
        {
            try
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // The refresh completed between taking the snapshot and cancelling it.
            }
        }

        if (refresh is not null)
        {
            try
            {
                await refresh.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Sign-in transitions discard both the old result and its failure.
            }
        }
        return generation;
    }

    internal void Resume(long generation)
    {
        lock (_gate)
        {
            if (!_suspensions.Remove(generation))
            {
                return;
            }

            _generation++;
            _token = null;
            _account = null;
            _failure = null;
            _retryAfter = default;
        }
    }

    private async Task RefreshAsync(
        string account,
        long generation,
        Func<CancellationToken, Task<OAuthAccessToken>> acquire,
        TaskCompletionSource<OAuthAccessToken> completion,
        CancellationTokenSource cancellation
    )
    {
        // Do not execute the acquisition synchronously while publishing its shared task under the lock.
        await Task.Yield();
        try
        {
            var result = await acquire(cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                EnsureCurrent(generation);
                if (_account != account || !HasLifetime(result))
                {
                    throw new IOException("ADO refresh did not produce a token with sufficient lifetime.");
                }

                _token = result;
                _failure = null;
                _refresh = null;
                _refreshCancellation = null;
                completion.TrySetResult(result);
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (_generation == generation && _suspensions.Count == 0)
                {
                    _token = null;
                    _failure = ExceptionDispatchInfo.Capture(ex);
                    _retryAfter = _time.GetUtcNow() + FailurePause;
                }

                if (ReferenceEquals(_refresh, completion.Task))
                {
                    _refresh = null;
                    _refreshCancellation = null;
                }

                completion.TrySetException(ex);
            }
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private bool HasLifetime(OAuthAccessToken? token) =>
        token is not null && token.ExpiresAtUtc - _time.GetUtcNow() > ExpiryMargin;

    private void EnsureCurrent(long generation)
    {
        if (_suspensions.Count != 0 || _generation != generation)
        {
            throw new IOException("ADO sign-in changed during webhook token acquisition.");
        }
    }
}
