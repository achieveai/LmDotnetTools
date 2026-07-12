using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;

namespace CodeReviewDaemon.Sample.Workspace.Sandbox;

/// <summary>
/// Collects one <see cref="GitProviderToken"/> per signed-in OAuth provider for the daemon's HOST-side git,
/// isolating each provider from the others. These tokens back <b>every</b> host git command, and they are
/// gathered for all providers up front, so one provider's failure must never deny credentials to another:
/// a transient Azure DevOps token-endpoint fault (MSAL throttling/5xx) can never abort a <c>github.com</c>
/// clone, and vice versa. A provider that is not signed in / not configured — signalled by
/// <see cref="InvalidOperationException"/> per the <see cref="IOAuthTokenProvider"/> contract — is skipped
/// quietly (its host's clones stay unauthenticated and fail fast). Any other fault is skipped too, but
/// logged at <c>Warning</c> so it leaves a breadcrumb instead of surfacing later as an opaque "repository
/// not found". Cancellation always propagates.
/// </summary>
internal static class HostGitCredentialsCollector
{
    public static async Task<IReadOnlyList<GitProviderToken>> CollectAsync(
        IEnumerable<IOAuthTokenProvider> providers,
        ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(logger);

        var tokens = new List<GitProviderToken>();
        foreach (var provider in providers)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var token = await provider.GetAccessTokenAsync(ct: ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(token.Value))
                {
                    tokens.Add(new GitProviderToken(provider.ProviderId, token.Value));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Per-provider isolation (the point of this method): swallow so one provider's failure never
                // denies the OTHERS their credentials. "Not signed in / refresh failed" is the expected,
                // benign case (the IOAuthTokenProvider contract signals it via InvalidOperationException) —
                // log it at Debug. Anything else (MSAL 5xx/throttling, a transient network fault to the
                // token endpoint) is unexpected; log at Warning so a silently-skipped provider is traceable.
                var expected = ex is InvalidOperationException;
                logger.Log(
                    expected ? LogLevel.Debug : LogLevel.Warning,
                    ex,
                    "Skipping host git credential for provider {ProviderId} ({Reason}).",
                    provider.ProviderId,
                    expected ? "not signed in" : "unexpected token fetch failure");
            }
        }

        return tokens;
    }
}
