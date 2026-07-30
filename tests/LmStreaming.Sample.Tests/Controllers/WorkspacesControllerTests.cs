
using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.Controllers;

/// <summary>
/// Controller tests for <see cref="WorkspacesController"/>, constructed directly over a real
/// <see cref="FileWorkspaceStore"/> on a temp dir (mirrors <c>ProvidersControllerTests</c>'s
/// direct-construction style).
/// </summary>
public class WorkspacesControllerTests
{
    private static (WorkspacesController Controller, FileWorkspaceStore Store) Build(string? defaultLeaf = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var store = new FileWorkspaceStore(dir, defaultLeaf);
        var compatibility = new WorkspaceCatalogCompatibilityService(new SupportedCatalogClient());
        var identity = GatewayWorkspaceCatalogIdentity.Create("http://gateway:3000", "sample");
        return (new WorkspacesController(store, compatibility, identity), store);
    }

    [Fact]
    public async Task List_ReturnsSeedPlusUser()
    {
        var (controller, store) = Build();
        _ = await store.CreateAsync(new WorkspaceCreate { Name = "Mine" });

        var result = await controller.List();
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<WorkspaceListResponse>().Subject;

        response.Gateway.CanonicalBaseUrl.Should().Be("http://gateway:3000");
        response.Workspaces.Should().HaveCount(2);
        response.Workspaces[0].Id.Should().Be(SandboxSessionRegistry.DefaultWorkspaceId);
        response.Workspaces[1].Name.Should().Be("Mine");
    }

    [Fact]
    public async Task Get_Unknown_ReturnsNotFound()
    {
        var (controller, _) = Build();

        var result = await controller.Get("does-not-exist");

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Create_Returns201_WithLocation()
    {
        var (controller, _) = Build();

        var result = await controller.Create(new WorkspaceCreate { Name = "New WS" });
        var created = result.Should().BeOfType<CreatedResult>().Subject;

        var workspace = created.Value.Should().BeOfType<WorkspaceView>().Subject;
        created.Location.Should().Be($"/api/workspaces/{workspace.Id}");
        workspace.DirectoryRelPath.Should().Be("new-ws");
    }

    [Fact]
    public async Task Create_Duplicate_Returns400_WithError()
    {
        var (controller, store) = Build();
        _ = await store.CreateAsync(new WorkspaceCreate { Name = "Dup" });

        var result = await controller.Create(new WorkspaceCreate { Name = "Dup" });
        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;

        bad.Value.Should().NotBeNull();
        bad.Value!.GetType().GetProperty("error").Should().NotBeNull();
    }

    [Fact]
    public async Task Update_ReplacesMarketplaces_Returns200()
    {
        var (controller, store) = Build();
        var created = await store.CreateAsync(new WorkspaceCreate { Name = "Proj" });

        var result = await controller.Update(created.Id, new WorkspaceUpdate { Marketplaces = ["x", "y"] });
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;

        var workspace = ok.Value.Should().BeOfType<WorkspaceView>().Subject;
        workspace.Marketplaces.Should().Equal("x", "y");
    }

    [Fact]
    public async Task Update_Unknown_Returns404()
    {
        var (controller, _) = Build();

        var result = await controller.Update("missing", new WorkspaceUpdate { Marketplaces = [] });

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Update_SystemDefined_Returns400_WithError()
    {
        var (controller, _) = Build();

        var result = await controller.Update(
            SandboxSessionRegistry.DefaultWorkspaceId,
            new WorkspaceUpdate { Marketplaces = ["x"] }
        );
        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;

        bad.Value!.GetType().GetProperty("error").Should().NotBeNull();
    }

    private sealed class SupportedCatalogClient : IMarketplaceCatalogClient
    {
        public Task<MarketplaceCatalog> GetCatalogAsync(
            IReadOnlyList<string>? marketplaces = null,
            CancellationToken ct = default) =>
            Task.FromResult(
                new MarketplaceCatalog(
                    ["x", "y"],
                    [new CatalogMarketplace("x", null, []), new CatalogMarketplace("y", null, [])]
                )
            );
    }
}
