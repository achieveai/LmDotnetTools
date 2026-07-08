namespace AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;

/// <summary>
/// A <see cref="DelegatingHandler"/> that attaches the sandbox gateway's per-app bearer headers
/// (<c>X-Sbx-App-Id</c> + <c>X-Sbx-App-Key</c>, gateway ADR 0029) to every outbound REST request, so the
/// gateway's session-lifecycle clients (<c>SandboxSessionRegistry</c>, <c>SandboxGatewayLifetime</c>,
/// <c>MarketplaceCatalogClient</c>) authenticate under <c>AUTH_ENFORCE</c> without each call site setting the
/// headers itself. Mirrors the daemon's <c>OperationPolicyHandler</c> pattern (constructor-injected values,
/// mutate <c>request.Headers</c>, continue via <see cref="DelegatingHandler.SendAsync"/>).
/// <para>
/// No-op when no app key is configured, so an unenforced gateway keeps working unchanged. Existing headers
/// are never overwritten (a hand-built request that already carries the bearer wins).
/// </para>
/// </summary>
public sealed class GatewayAuthHandler : DelegatingHandler
{
    private readonly string? _appId;
    private readonly string? _appKey;

    /// <summary>Creates the handler with the app identity + base64 secret (either may be null when unenforced).</summary>
    public GatewayAuthHandler(string? appId, string? appKey)
    {
        _appId = appId;
        _appKey = appKey;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (GatewayAuthHeaders.IsConfigured(_appId, _appKey))
        {
            // TryAddWithoutValidation is a no-op when the header is already present, so a caller that
            // pre-set the bearer (or a retried request) is never duplicated.
            if (!request.Headers.Contains(GatewayAuthHeaders.AppIdHeader))
            {
                _ = request.Headers.TryAddWithoutValidation(GatewayAuthHeaders.AppIdHeader, _appId);
            }

            if (!request.Headers.Contains(GatewayAuthHeaders.AppKeyHeader))
            {
                _ = request.Headers.TryAddWithoutValidation(GatewayAuthHeaders.AppKeyHeader, _appKey);
            }
        }

        return base.SendAsync(request, cancellationToken);
    }
}
