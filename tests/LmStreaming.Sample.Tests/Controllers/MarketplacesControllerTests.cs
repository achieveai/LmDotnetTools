using LmStreaming.Sample.Services;
using Microsoft.AspNetCore.Http;

namespace LmStreaming.Sample.Tests.Controllers;

/// <summary>
/// Tests <see cref="MarketplacesController"/> against a fake <see cref="IMarketplaceCatalogClient"/>.
/// The fake is the seam that lets these tests — and a future browser/E2E run — exercise the endpoint
/// with NO live gateway: the happy path returns the catalog, and the "gateway offline" path
/// (<see cref="MarketplaceCatalogUnavailableException"/>) degrades to 503 rather than 500.
/// </summary>
public class MarketplacesControllerTests
{
    [Fact]
    public async Task List_ReturnsCatalog_OnSuccess()
    {
        var catalog = new MarketplaceCatalog(
            Selected: ["official"],
            Marketplaces: [new CatalogMarketplace("official", Error: null, Plugins: [])]);
        var controller = new MarketplacesController(
            new FakeCatalogClient(catalog), NullLogger<MarketplacesController>.Instance);

        var result = await controller.List(marketplaces: null, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeSameAs(catalog);
    }

    [Fact]
    public async Task List_ParsesCommaSeparatedAliases_AndForwardsThem()
    {
        var fake = new FakeCatalogClient(new MarketplaceCatalog([], []));
        var controller = new MarketplacesController(fake, NullLogger<MarketplacesController>.Instance);

        _ = await controller.List(marketplaces: " official , claude_plugins ", CancellationToken.None);

        fake.LastRequestedAliases.Should().Equal("official", "claude_plugins");
    }

    [Fact]
    public async Task List_BlankAliases_ForwardsNull_SoGatewayUsesItsDefault()
    {
        var fake = new FakeCatalogClient(new MarketplaceCatalog([], []));
        var controller = new MarketplacesController(fake, NullLogger<MarketplacesController>.Instance);

        _ = await controller.List(marketplaces: "  ,  ", CancellationToken.None);

        fake.LastRequestedAliases.Should().BeNull();
    }

    [Fact]
    public async Task List_Returns503_WhenGatewayUnavailable()
    {
        var controller = new MarketplacesController(
            new FakeCatalogClient(new MarketplaceCatalogUnavailableException("gateway offline")),
            NullLogger<MarketplacesController>.Instance);

        var result = await controller.List(marketplaces: null, CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>
    /// Minimal <see cref="IMarketplaceCatalogClient"/> double: returns a fixed catalog (or throws a
    /// fixed exception) and records the aliases it was asked for. Reusable as an E2E registration:
    /// <c>services.RemoveAll&lt;IMarketplaceCatalogClient&gt;(); services.AddSingleton&lt;IMarketplaceCatalogClient&gt;(fake);</c>
    /// </summary>
    private sealed class FakeCatalogClient : IMarketplaceCatalogClient
    {
        private readonly MarketplaceCatalog? _catalog;
        private readonly MarketplaceCatalogUnavailableException? _error;

        public FakeCatalogClient(MarketplaceCatalog catalog) => _catalog = catalog;
        public FakeCatalogClient(MarketplaceCatalogUnavailableException error) => _error = error;

        public IReadOnlyList<string>? LastRequestedAliases { get; private set; }

        public Task<MarketplaceCatalog> GetCatalogAsync(
            IReadOnlyList<string>? marketplaces = null,
            CancellationToken ct = default)
        {
            LastRequestedAliases = marketplaces;
            return _error is not null
                ? Task.FromException<MarketplaceCatalog>(_error)
                : Task.FromResult(_catalog!);
        }
    }
}
