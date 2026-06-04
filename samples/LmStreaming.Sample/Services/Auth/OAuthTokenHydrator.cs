namespace LmStreaming.Sample.Services.Auth;

/// <summary>
/// At startup, asks each registered <see cref="IOAuthTokenProvider"/> to restore its persisted
/// sign-in state, so a sign-in from a previous run is reflected by the status API / UI (and isn't
/// shown as "not signed in" after a restart). Token injection itself never depended on this —
/// <see cref="IOAuthTokenProvider.GetAccessTokenAsync"/> reads the persisted token directly —
/// this only fixes the surfaced status.
/// </summary>
public sealed class OAuthTokenHydrator(IEnumerable<IOAuthTokenProvider> providers, ILogger<OAuthTokenHydrator> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var provider in providers)
        {
            try
            {
                await provider.HydrateFromStoreAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Hydration is best-effort and must never block host startup.
                logger.LogWarning(ex, "Failed to hydrate OAuth provider {ProviderId} at startup.", provider.ProviderId);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
