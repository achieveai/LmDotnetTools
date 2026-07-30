using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.Services;

public sealed class WorkspaceCatalogCompatibilityServiceTests
{
    [Fact]
    public async Task EvaluateAsync_ReportsSupportedAndUnsupportedInStoredOrder()
    {
        var client = new StubCatalogClient(Catalog("one", "two"));
        var service = new WorkspaceCatalogCompatibilityService(client);
        var workspace = Workspace(["three", "one", "three", "four"]);

        var result = await service.EvaluateAsync(workspace);

        result.Compatibility.Should().Be(WorkspaceCompatibility.Incompatible);
        result.UnsupportedMarketplaces.Should().Equal("three", "four");
        result.AvailableMarketplaces.Should().Equal("one", "two");
    }

    [Fact]
    public async Task EvaluateAsync_EmptyAndSupportedSelectionsAreCompatible()
    {
        var service = new WorkspaceCatalogCompatibilityService(
            new StubCatalogClient(Catalog("one", "two"))
        );

        (await service.EvaluateAsync(Workspace([]))).Compatibility
            .Should().Be(WorkspaceCompatibility.Compatible);
        (await service.EvaluateAsync(Workspace(["two"]))).Compatibility
            .Should().Be(WorkspaceCompatibility.Compatible);
    }

    [Fact]
    public async Task EvaluateAsync_UnavailableGatewayIsUnknown()
    {
        var service = new WorkspaceCatalogCompatibilityService(
            new StubCatalogClient(new MarketplaceCatalogUnavailableException("offline"))
        );

        var result = await service.EvaluateAsync(Workspace(["one"]));

        result.Compatibility.Should().Be(WorkspaceCompatibility.Unknown);
        result.Error.Should().Contain("offline");
    }

    [Fact]
    public async Task ConcurrentCallsUseSingleFlightAndCache()
    {
        var client = new StubCatalogClient(Catalog("one"));
        var service = new WorkspaceCatalogCompatibilityService(client);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => service.EvaluateAsync(Workspace([]))));
        _ = await service.EvaluateAsync(Workspace([]));

        client.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task ValidateForMutationThrowsTypedErrors()
    {
        var unsupported = new WorkspaceCatalogCompatibilityService(
            new StubCatalogClient(Catalog("one"))
        );
        var unavailable = new WorkspaceCatalogCompatibilityService(
            new StubCatalogClient(new MarketplaceCatalogUnavailableException("offline"))
        );

        await FluentActions.Invoking(() => unsupported.ValidateForMutationAsync(["two"]))
            .Should().ThrowAsync<UnsupportedWorkspaceMarketplacesException>();
        await FluentActions.Invoking(() => unavailable.ValidateForMutationAsync(["one"]))
            .Should().ThrowAsync<WorkspaceGatewayCatalogUnavailableException>();
    }

    private static Workspace Workspace(IReadOnlyList<string> aliases) => new()
    {
        Id = "id",
        Name = "name",
        DirectoryRelPath = "leaf",
        Marketplaces = aliases,
    };

    private static MarketplaceCatalog Catalog(params string[] aliases) => new(
        aliases,
        [.. aliases.Select(x => new CatalogMarketplace(x, null, []))]
    );

    private sealed class StubCatalogClient : IMarketplaceCatalogClient
    {
        private readonly MarketplaceCatalog? _catalog;
        private readonly Exception? _error;
        private int _calls;

        public StubCatalogClient(MarketplaceCatalog catalog) => _catalog = catalog;
        public StubCatalogClient(Exception error) => _error = error;
        public int CallCount => _calls;

        public Task<MarketplaceCatalog> GetCatalogAsync(
            IReadOnlyList<string>? marketplaces = null,
            CancellationToken ct = default)
        {
            _ = Interlocked.Increment(ref _calls);
            return _error is not null
                ? Task.FromException<MarketplaceCatalog>(_error)
                : Task.FromResult(_catalog!);
        }
    }
}
