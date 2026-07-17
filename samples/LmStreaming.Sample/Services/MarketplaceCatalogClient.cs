using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.Sandbox;
using LmStreaming.Sample.Models;

namespace LmStreaming.Sample.Services;

/// <summary>
/// Talks to the gateway's <c>GET /api/v1/marketplaces/preview</c> endpoint via the typed
/// <see cref="SandboxClient"/> SDK. Best-effort by design: it does NOT spawn or wait for the gateway
/// (the catalog is a read-only browse, not a session), so when the gateway is offline it surfaces
/// <see cref="MarketplaceCatalogUnavailableException"/> rather than blocking to start one.
/// <para>
/// The SDK is constructed KEYLESS over the borrowed <see cref="HttpClient"/>: the app-auth bearer
/// headers (ADR 0029) are supplied by the <see cref="GatewayAuthHandler"/> already wired into that
/// client's pipeline, so this client neither validates nor forwards the app key itself.
/// </para>
/// </summary>
public sealed class MarketplaceCatalogClient : IMarketplaceCatalogClient
{
    private readonly SandboxGatewayOptions _options;
    private readonly SandboxClient _sandboxClient;
    private readonly ILogger<MarketplaceCatalogClient> _logger;

    public MarketplaceCatalogClient(
        SandboxGatewayOptions options,
        HttpClient httpClient,
        ILogger<MarketplaceCatalogClient> logger
    )
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(httpClient);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Keyless SDK over the borrowed client — the GatewayAuthHandler in the pipeline stamps the
        // X-Sbx-App-Key, so passing a (possibly short dev) app key into the SDK's validating options
        // would be both redundant and a fail-fast hazard. allowInsecure lets the same client talk to a
        // plain-HTTP dev gateway exactly as before. The trailing slash makes the SDK's relative-URI
        // resolution reproduce the previous "{base}/api/v1/..." string exactly, even for a path-prefixed
        // base URL.
        var serverAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        var transportTimeout = httpClient.Timeout > TimeSpan.Zero ? httpClient.Timeout : TimeSpan.FromSeconds(100);
        var sandboxOptions = new SandboxClientOptions(
            serverAddress,
            _options.AppId,
            clientSecret: string.Empty,
            executionTimeout: TimeSpan.FromMinutes(5),
            transportTimeout: transportTimeout,
            allowInsecureDevelopmentTransport: true
        );
        _sandboxClient = new SandboxClient(sandboxOptions, httpClient);
    }

    public async Task<MarketplaceCatalog> GetCatalogAsync(
        IReadOnlyList<string>? marketplaces = null,
        CancellationToken ct = default
    )
    {
        var baseUrl = _options.BaseUrl.TrimEnd('/');
        try
        {
            var catalog = await _sandboxClient.PreviewMarketplacesAsync(marketplaces, ct).ConfigureAwait(false);
            return Map(catalog);
        }
        catch (SandboxException ex)
        {
            // A 200 with a literal `null` body is a contract violation the SDK surfaces as a Protocol
            // error with a 2xx status and no inner exception; normalise it to an empty catalog rather
            // than handing callers a null — but log it so a gateway bug is not silently rendered as
            // "no marketplaces configured".
            if (ex.Kind == SandboxErrorKind.Protocol && ex.StatusCode is >= 200 and < 300 && ex.InnerException is null)
            {
                _logger.LogWarning("Marketplace catalog request returned 200 with a null body; treating as empty.");
                return new MarketplaceCatalog([], []);
            }

            // A reachable gateway that returns a 200 with an unparseable/invalid body is still "catalog
            // unavailable" — fold it into the same contract (with a log) so the controller answers 503
            // and the SPA shows the offline state instead of an opaque, unlogged 500.
            if (ex.StatusCode is >= 200 and < 300)
            {
                _logger.LogWarning(ex, "Marketplace catalog returned a 200 with an unparseable body.");
                throw new MarketplaceCatalogUnavailableException(
                    "Sandbox gateway returned a malformed marketplace catalog body.", ex);
            }

            // Any other status (non-2xx) → unavailable. The SDK never reads gateway error bodies, so
            // the previous body-in-message diagnostic is intentionally dropped; the status code is kept.
            if (ex.StatusCode is { } statusCode)
            {
                _logger.LogWarning("Marketplace catalog request failed: {StatusCode}", statusCode);
                throw new MarketplaceCatalogUnavailableException(
                    $"Sandbox gateway returned {statusCode} for the marketplace catalog.", ex);
            }

            // No status code → the gateway was unreachable (connection refused / DNS / transport
            // timeout). Distinct from caller cancellation, which the SDK re-throws as
            // OperationCanceledException and this catch never touches.
            throw new MarketplaceCatalogUnavailableException(
                $"Could not reach the sandbox gateway at {baseUrl} to list marketplaces.", ex);
        }
    }

    private static MarketplaceCatalog Map(SandboxMarketplaceCatalog catalog) =>
        new(
            catalog.Selected,
            [
                .. catalog.Marketplaces.Select(marketplace =>
                    new CatalogMarketplace(
                        marketplace.Alias,
                        marketplace.Error,
                        [
                            .. marketplace.Plugins.Select(plugin =>
                                new CatalogPlugin(
                                    plugin.Name,
                                    plugin.Version,
                                    plugin.Description,
                                    [.. plugin.Skills.Select(s => new CatalogSkill(s.Name, s.Description, s.Plugin, s.Marketplace, s.Path))],
                                    [.. plugin.Agents.Select(a => new CatalogAgent(a.Name, a.Description, a.Plugin, a.Marketplace, a.Path))]
                                )
                            ),
                        ]
                    )
                ),
            ]
        );
}
