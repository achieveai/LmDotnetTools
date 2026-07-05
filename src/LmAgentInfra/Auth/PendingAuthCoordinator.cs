namespace AchieveAi.LmDotnetTools.LmAgentInfra.Auth;

/// <summary>A deferred-auth hold that is currently waiting for an interactive sign-in.</summary>
public sealed record PendingAuthInfo(string ProviderId, string SigninUrl, string Reason);

/// <summary>
/// Implements "deferred auth" for the sandbox gateway's auth webhook: when a webhook call arrives
/// for a provider with no valid token, the call is HELD here while connected chat clients are
/// prompted (via <see cref="IAuthEventNotifier"/>) to sign in. The hold polls
/// <see cref="IOAuthTokenProvider.GetAccessTokenAsync"/> until a token appears (the user signed in
/// — or, in tests, the token store was seeded), the hold times out, the gateway aborts the call, or
/// a fresh sign-in failure is observed.
/// </summary>
/// <remarks>
/// Polling (rather than a sign-in-completed event) is deliberate: providers read their token store
/// directly in <c>GetAccessTokenAsync</c>, so the hold resolves for any path that lands a token —
/// interactive sign-in, store seeding, or an out-of-band refresh — without coupling to provider
/// status transitions. Concurrent holds for the same provider each run their own cheap poll loop
/// (each honors its own gateway CancellationToken) but share one ref-counted entry so clients see
/// exactly one <c>auth_required</c> prompt per provider at a time.
/// </remarks>
public sealed class PendingAuthCoordinator(
    IAuthEventNotifier notifier,
    AuthOptions options,
    ILogger<PendingAuthCoordinator> logger)
{
    private sealed class PendingEntry
    {
        public required PendingAuthInfo Info { get; init; }
        public int WaiterCount;
        public bool TokenObtained;
    }

    private readonly Dictionary<string, PendingEntry> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>Point-in-time view of the providers currently holding webhook calls — used to replay <c>auth_required</c> to late-connecting clients.</summary>
    public IReadOnlyList<PendingAuthInfo> Snapshot()
    {
        lock (_gate)
        {
            return [.. _pending.Values.Select(e => e.Info)];
        }
    }

    /// <summary>
    /// Holds until <paramref name="provider"/> yields a token, the configured hold timeout elapses
    /// (returns null → caller denies), or <paramref name="gatewayCt"/> is cancelled (throws — the
    /// gateway gave up, nobody is waiting for a decision). Returns null immediately when deferral
    /// is disabled (<see cref="WebhookOptions.HoldTimeoutSeconds"/> &lt;= 0).
    /// </summary>
    public async Task<OAuthAccessToken?> WaitForTokenAsync(
        IOAuthTokenProvider provider,
        IReadOnlyList<string>? scopes,
        CancellationToken gatewayCt)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var holdTimeout = TimeSpan.FromSeconds(options.Webhook.HoldTimeoutSeconds);
        if (holdTimeout <= TimeSpan.Zero)
        {
            return null;
        }

        var pollInterval = TimeSpan.FromSeconds(Math.Max(0.05, options.Webhook.PollIntervalSeconds));

        // Snapshot the status REFERENCE at entry: a pre-existing Failed (from an older attempt)
        // must not block deferral, but a NEW failure during the hold (fresh status instance)
        // means the user tried and failed — deny early instead of waiting out the clock.
        var statusAtEntry = provider.Status;

        // Enter() lives INSIDE the try so its ref-count increment and the finally's ExitAsync
        // decrement are structurally paired — no statement between them can orphan the entry
        // (which would pin the auth_required broadcast/replay forever).
        PendingEntry? entry = null;
        try
        {
            entry = Enter(provider.ProviderId, out var isFirstWaiter);
            if (isFirstWaiter)
            {
                await notifier.NotifyAuthRequiredAsync(
                    entry.Info.ProviderId,
                    entry.Info.SigninUrl,
                    entry.Info.Reason,
                    CancellationToken.None);
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(gatewayCt);
            linked.CancelAfter(holdTimeout);

            while (true)
            {
                try
                {
                    await Task.Delay(pollInterval, linked.Token);
                }
                catch (OperationCanceledException)
                {
                    gatewayCt.ThrowIfCancellationRequested();
                    logger.LogInformation(
                        "Deferred-auth hold for provider {ProviderId} timed out after {HoldTimeout}; denying.",
                        provider.ProviderId,
                        holdTimeout);
                    return null;
                }

                try
                {
                    var token = await provider.GetAccessTokenAsync(scopes, linked.Token);
                    lock (_gate)
                    {
                        entry.TokenObtained = true;
                    }

                    return token;
                }
                catch (InvalidOperationException)
                {
                    // Still not signed in — keep holding unless a NEW failure occurred.
                    var status = provider.Status;
                    if (status.State == OAuthSignInState.Failed && !ReferenceEquals(status, statusAtEntry))
                    {
                        logger.LogInformation(
                            "Deferred-auth hold for provider {ProviderId} observed a fresh sign-in failure; denying early.",
                            provider.ProviderId);
                        return null;
                    }
                }
                catch (OperationCanceledException)
                {
                    gatewayCt.ThrowIfCancellationRequested();
                    return null;
                }
            }
        }
        finally
        {
            if (entry is not null)
            {
                await ExitAsync(provider.ProviderId, entry);
            }
        }
    }

    /// <summary>Joins (or creates) the per-provider entry; <paramref name="isFirstWaiter"/> signals the 0→1 transition that triggers the client prompt.</summary>
    private PendingEntry Enter(string providerId, out bool isFirstWaiter)
    {
        lock (_gate)
        {
            if (_pending.TryGetValue(providerId, out var existing))
            {
                existing.WaiterCount++;
                isFirstWaiter = false;
                return existing;
            }

            var entry = new PendingEntry
            {
                Info = new PendingAuthInfo(
                    providerId,
                    AuthSigninUrls.BuildSigninUrl(providerId),
                    AuthSigninUrls.BuildReason(providerId)),
                WaiterCount = 1,
            };
            _pending[providerId] = entry;
            isFirstWaiter = true;
            return entry;
        }
    }

    /// <summary>
    /// Leaves the per-provider entry. The last waiter out clears it and sends the terminal frame
    /// that dismisses the client prompt: <c>auth_completed</c> when a token was obtained, otherwise
    /// <c>auth_denied</c> (hold timed out, sign-in failed, or deferral disabled). Exactly one of the
    /// two fires per prompt — without the deny side the banner would linger after a timeout.
    /// </summary>
    private async Task ExitAsync(string providerId, PendingEntry entry)
    {
        bool isLastWaiter;
        bool tokenObtained;
        lock (_gate)
        {
            entry.WaiterCount--;
            isLastWaiter = entry.WaiterCount == 0;
            if (isLastWaiter)
            {
                _ = _pending.Remove(providerId);
            }

            tokenObtained = entry.TokenObtained;
        }

        if (!isLastWaiter)
        {
            return;
        }

        if (tokenObtained)
        {
            await notifier.NotifyAuthCompletedAsync(providerId, CancellationToken.None);
        }
        else
        {
            await notifier.NotifyAuthDeniedAsync(providerId, "sign-in not completed", CancellationToken.None);
        }
    }
}
