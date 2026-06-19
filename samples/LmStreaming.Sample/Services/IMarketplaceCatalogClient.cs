using LmStreaming.Sample.Models;

namespace LmStreaming.Sample.Services;

/// <summary>
/// Reads the sandbox gateway's marketplace catalog (<c>GET /api/v1/marketplaces/preview</c>).
/// This is the seam controllers and tests depend on: the real implementation talks to the gateway,
/// while tests/E2E swap in a fake so they never need a live gateway (which may be offline during
/// testing — the whole point of preview being sandbox-free).
/// </summary>
public interface IMarketplaceCatalogClient
{
    /// <summary>
    /// Fetches the catalog for the given marketplace aliases. When <paramref name="marketplaces"/>
    /// is null/empty the gateway applies its own default set (<c>DEFAULT_MARKETPLACES</c> ⇒ all).
    /// </summary>
    /// <exception cref="MarketplaceCatalogUnavailableException">
    /// The gateway was unreachable or returned a non-success status. Callers map this to a 503 so
    /// the UI can show "gateway offline" rather than a hard error.
    /// </exception>
    Task<MarketplaceCatalog> GetCatalogAsync(
        IReadOnlyList<string>? marketplaces = null,
        CancellationToken ct = default
    );
}

/// <summary>
/// Raised when the marketplace catalog cannot be obtained from the gateway — either the gateway is
/// not running or it answered with a non-success status. Carries the gateway's response body (when
/// any) so the controller can echo a useful detail.
/// </summary>
public sealed class MarketplaceCatalogUnavailableException : Exception
{
    public MarketplaceCatalogUnavailableException(string message, Exception? inner = null)
        : base(message, inner) { }
}
