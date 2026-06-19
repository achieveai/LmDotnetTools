using LmStreaming.Sample.Services;
using Microsoft.AspNetCore.Mvc;

namespace LmStreaming.Sample.Controllers;

/// <summary>
/// Exposes the sandbox gateway's marketplace catalog to the SPA so it can browse available
/// plugins/skills/agents. Proxies <c>GET /api/v1/marketplaces/preview</c> through the
/// <see cref="IMarketplaceCatalogClient"/> seam and degrades to <c>503</c> when the gateway is
/// offline (expected during local/CI runs where no gateway is started).
/// </summary>
[ApiController]
[Route("api/marketplaces")]
public class MarketplacesController(
    IMarketplaceCatalogClient catalogClient,
    ILogger<MarketplacesController> logger) : ControllerBase
{
    /// <summary>
    /// Lists the marketplace catalog. The optional <paramref name="marketplaces"/> query is a
    /// comma-separated alias subset (e.g. <c>?marketplaces=official,claude_plugins</c>); when
    /// omitted the gateway applies its default set.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? marketplaces, CancellationToken ct)
    {
        var aliases = MarketplaceAliases.Parse(marketplaces);
        try
        {
            var catalog = await catalogClient.GetCatalogAsync(aliases, ct);
            return Ok(catalog);
        }
        catch (MarketplaceCatalogUnavailableException ex)
        {
            logger.LogWarning(ex, "Marketplace catalog unavailable");
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = "marketplace_gateway_unavailable", detail = ex.Message }
            );
        }
    }
}
