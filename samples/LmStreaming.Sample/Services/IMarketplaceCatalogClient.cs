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
/// The catalog reader used by FAIL-CLOSED session validation, as opposed to the best-effort browse
/// reader behind <see cref="IMarketplaceCatalogClient"/>. Same contract, different transport budget —
/// and that difference is the whole reason the seam exists.
/// </summary>
/// <remarks>
/// The browse client is deliberately short-timeout: <c>GET /api/marketplaces</c> is a read-only browse
/// that must not hold a page load while an offline gateway is not answering. Session validation has the
/// opposite requirement — it REFUSES the session when it cannot read the catalog, so giving up early is
/// giving up on a gateway that would have answered, and a gateway that has to start a container before
/// its first catalog read routinely takes many times a browse budget. Two clients let each caller have
/// the budget its own failure mode deserves.
/// <para>
/// Registering this is OPTIONAL. When nothing is registered the compatibility service falls back to
/// the browse client, which is exactly the behaviour that existed before the split — so a host (or
/// test) that only wires <see cref="IMarketplaceCatalogClient"/> is unaffected.
/// </para>
/// </remarks>
public interface ISessionMarketplaceCatalogClient : IMarketplaceCatalogClient { }

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
