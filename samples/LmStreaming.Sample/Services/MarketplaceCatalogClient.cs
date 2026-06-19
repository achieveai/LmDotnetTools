using System.Text.Json;
using LmStreaming.Sample.Models;

namespace LmStreaming.Sample.Services;

/// <summary>
/// Talks to the gateway's <c>GET /api/v1/marketplaces/preview</c> endpoint. Best-effort by design:
/// it does NOT spawn or wait for the gateway (the catalog is a read-only browse, not a session), so
/// when the gateway is offline it surfaces <see cref="MarketplaceCatalogUnavailableException"/>
/// rather than blocking to start one.
/// </summary>
public sealed class MarketplaceCatalogClient : IMarketplaceCatalogClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly SandboxGatewayOptions _options;
    private readonly HttpClient _httpClient;
    private readonly ILogger<MarketplaceCatalogClient> _logger;

    public MarketplaceCatalogClient(
        SandboxGatewayOptions options,
        HttpClient httpClient,
        ILogger<MarketplaceCatalogClient> logger
    )
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<MarketplaceCatalog> GetCatalogAsync(
        IReadOnlyList<string>? marketplaces = null,
        CancellationToken ct = default
    )
    {
        var baseUrl = _options.BaseUrl.TrimEnd('/');
        var requestUri = $"{baseUrl}/api/v1/marketplaces/preview";
        if (marketplaces is { Count: > 0 })
        {
            requestUri += $"?marketplaces={Uri.EscapeDataString(string.Join(',', marketplaces))}";
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.GetAsync(requestUri, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // Connection refused / DNS / timeout — the gateway isn't reachable. Distinct from caller
            // cancellation, which we let propagate.
            throw new MarketplaceCatalogUnavailableException(
                $"Could not reach the sandbox gateway at {baseUrl} to list marketplaces.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                _logger.LogWarning(
                    "Marketplace catalog request failed: {StatusCode} {Body}",
                    (int)response.StatusCode,
                    body
                );
                throw new MarketplaceCatalogUnavailableException(
                    $"Sandbox gateway returned {(int)response.StatusCode} for the marketplace catalog: {body}");
            }

            var catalog = await response
                .Content.ReadFromJsonAsync<MarketplaceCatalog>(JsonOptions, ct)
                .ConfigureAwait(false);

            // A 200 with a null/empty body is a contract violation; normalise to an empty catalog
            // rather than handing callers a null.
            return catalog ?? new MarketplaceCatalog([], []);
        }
    }
}
