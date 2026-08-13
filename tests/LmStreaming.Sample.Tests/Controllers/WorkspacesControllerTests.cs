
using LmStreaming.Sample.Services;
using Microsoft.AspNetCore.Http;

namespace LmStreaming.Sample.Tests.Controllers;

/// <summary>
/// Controller tests for <see cref="WorkspacesController"/>, constructed directly over a real
/// <see cref="FileWorkspaceStore"/> on a temp dir (mirrors <c>ProvidersControllerTests</c>'s
/// direct-construction style).
/// </summary>
public class WorkspacesControllerTests
{
    private static (WorkspacesController Controller, FileWorkspaceStore Store) Build(
        string? defaultLeaf = null,
        IWorkspacePluginSelectionService? pluginSelection = null,
        IMarketplaceCatalogClient? catalog = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var store = new FileWorkspaceStore(dir, defaultLeaf);
        var compatibility = new WorkspaceCatalogCompatibilityService(
            catalog ?? new SupportedCatalogClient(),
            new SandboxGatewayOptions()
        );
        var identity = GatewayWorkspaceCatalogIdentity.Create("http://gateway:3000", "sample");
        return (
            new WorkspacesController(
                store,
                compatibility,
                identity,
                // Unless a test supplies its own, the migration service is one that FAILS LOUDLY when
                // touched. A no-op stub would let "an ordinary update silently migrated" pass as green;
                // NotSupportedException maps to no catch in the controller, so it escapes the action.
                pluginSelection ?? new StubPluginSelection(new NotSupportedException("migration must not run"))
            ),
            store
        );
    }

    /// <summary>Reads a named property off one of the controller's anonymous error payloads.</summary>
    private static object? ErrorField(object? payload, string name) =>
        payload!.GetType().GetProperty(name)!.GetValue(payload);

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
    public async Task Create_ExplicitPluginSelection_UnsupportedPlugin_Returns400_WithCode()
    {
        // Catalog supports filtering and offers exactly x/known, so x/ghost is rejected on the CREATE
        // path — the path that had no plugin validation at all before this change.
        var (controller, store) = Build(catalog: new SupportedCatalogClient(pluginFiltering: true, "known"));

        var result = await controller.Create(
            new WorkspaceCreate
            {
                Name = "Ghosted",
                Marketplaces = ["x"],
                PluginSelection = [new PluginRef("x", "ghost")],
            }
        );

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        ErrorField(bad.Value, "code").Should().Be("unsupported_plugins");

        // Rejected BEFORE persistence: the workspace must not exist at all.
        (await store.GetAllAsync()).Should().ContainSingle(w => w.IsSystemDefined);
    }

    [Fact]
    public async Task Create_ExplicitPluginSelection_GatewayCannotFilter_Returns503_WithCode()
    {
        // Default catalog advertises no capability at all; unknown is not permission (fail closed).
        var (controller, _) = Build();

        var result = await controller.Create(
            new WorkspaceCreate
            {
                Name = "NoFilter",
                Marketplaces = ["x"],
                PluginSelection = [],
            }
        );

        var status = result.Should().BeAssignableTo<ObjectResult>().Subject;
        status.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        ErrorField(status.Value, "code").Should().Be("gateway_plugin_filtering_unsupported");
    }

    [Fact]
    public async Task Create_ExplicitPluginSelection_Supported_PersistsSelection()
    {
        var (controller, store) = Build(catalog: new SupportedCatalogClient(pluginFiltering: true, "known"));

        var result = await controller.Create(
            new WorkspaceCreate
            {
                Name = "Picky",
                Marketplaces = ["x"],
                PluginSelection = [new PluginRef("x", "known")],
            }
        );

        var created = result.Should().BeOfType<CreatedResult>().Subject;
        var view = created.Value.Should().BeOfType<WorkspaceView>().Subject;

        var stored = await store.GetAsync(view.Id);
        stored!.PluginSelection.Should().Equal(new PluginRef("x", "known"));
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
    public async Task Update_WithoutPluginSelection_NeverInvokesMigration()
    {
        // The whole point of the routing branch: a rename or marketplace edit must stay an ordinary
        // store write. The stub counts calls, so this cannot pass vacuously.
        var migration = new StubPluginSelection();
        var (controller, store) = Build(pluginSelection: migration);
        var created = await store.CreateAsync(new WorkspaceCreate { Name = "Proj" });

        var result = await controller.Update(created.Id, new WorkspaceUpdate { Marketplaces = ["x"] });

        _ = result.Should().BeOfType<OkObjectResult>();
        migration.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Update_WithPluginSelection_RoutesThroughMigration()
    {
        var migrated = new Workspace
        {
            Id = "w1",
            Name = "Proj",
            DirectoryRelPath = "proj",
            Marketplaces = ["x"],
        };
        var migration = new StubPluginSelection(result: migrated);
        var (controller, _) = Build(pluginSelection: migration);

        var result = await controller.Update(
            "w1",
            new WorkspaceUpdate
            {
                Marketplaces = ["x"],
                PluginSelection = new Optional<IReadOnlyList<PluginRef>?>([]),
                PluginsRevision = 0,
            }
        );

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        _ = ok.Value.Should().BeOfType<WorkspaceView>();
        migration.Calls.Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(MigrationFailureMappings))]
    public async Task Update_MigrationFailure_MapsToSpecCodeAndStatus(
        Exception failure,
        int expectedStatus,
        string expectedCode)
    {
        var (controller, _) = Build(pluginSelection: new StubPluginSelection(failure));

        var result = await controller.Update(
            "w1",
            new WorkspaceUpdate
            {
                Marketplaces = ["x"],
                PluginSelection = new Optional<IReadOnlyList<PluginRef>?>([]),
                PluginsRevision = 0,
            }
        );

        var status = result.Should().BeAssignableTo<ObjectResult>().Subject;
        status.StatusCode.Should().Be(expectedStatus);
        ErrorField(status.Value, "code").Should().Be(expectedCode);
    }

    public static TheoryData<Exception, int, string> MigrationFailureMappings() =>
        new()
        {
            {
                new UnsupportedWorkspacePluginsException([new PluginRef("x", "ghost")], []),
                StatusCodes.Status400BadRequest,
                "unsupported_plugins"
            },
            {
                new GatewayPluginFilteringUnsupportedException(),
                StatusCodes.Status503ServiceUnavailable,
                "gateway_plugin_filtering_unsupported"
            },
            {
                new WorkspaceRevisionConflictException("w1", 1, 4),
                StatusCodes.Status409Conflict,
                "workspace_revision_conflict"
            },
            {
                new SandboxSessionRestartTimeoutException("w1", TimeSpan.FromSeconds(30)),
                StatusCodes.Status503ServiceUnavailable,
                "sandbox_restart_timeout"
            },
            {
                new SandboxSessionReplacementFailedException("w1", new IOException("gateway down")),
                StatusCodes.Status502BadGateway,
                "sandbox_replacement_failed"
            },
        };

    [Fact]
    public async Task Update_RevisionConflict_ReportsBothRevisions()
    {
        var (controller, _) = Build(
            pluginSelection: new StubPluginSelection(new WorkspaceRevisionConflictException("w1", 1, 4))
        );

        var result = await controller.Update(
            "w1",
            new WorkspaceUpdate
            {
                Marketplaces = ["x"],
                PluginSelection = new Optional<IReadOnlyList<PluginRef>?>([]),
                PluginsRevision = 1,
            }
        );

        var conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        ErrorField(conflict.Value, "expectedRevision").Should().Be(1);
        ErrorField(conflict.Value, "actualRevision").Should().Be(4);
    }

    [Fact]
    public async Task Update_CatalogCorrupt_Returns503_WithCode()
    {
        // WorkspaceCatalogCorruptException derives from InvalidOperationException, so without a
        // dedicated catch ABOVE the generic one it silently degrades to 400 "bad request" — blaming
        // the caller for a corrupt catalog on disk. List/Get/Create answer 503; Update must match.
        var (controller, _) = Build(
            pluginSelection: new StubPluginSelection(
                new WorkspaceCatalogCorruptException("catalog.json", "unterminated object")
            )
        );

        var result = await controller.Update(
            "w1",
            new WorkspaceUpdate
            {
                Marketplaces = ["x"],
                PluginSelection = new Optional<IReadOnlyList<PluginRef>?>([]),
                PluginsRevision = 0,
            }
        );

        var status = result.Should().BeAssignableTo<ObjectResult>().Subject;
        status.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        ErrorField(status.Value, "code").Should().Be("workspace_catalog_unavailable");
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

    private sealed class SupportedCatalogClient(bool? pluginFiltering = null, params string[] pluginsUnderX)
        : IMarketplaceCatalogClient
    {
        public Task<MarketplaceCatalog> GetCatalogAsync(
            IReadOnlyList<string>? marketplaces = null,
            CancellationToken ct = default) =>
            Task.FromResult(
                new MarketplaceCatalog(
                    ["x", "y"],
                    [
                        new CatalogMarketplace(
                            "x",
                            null,
                            [.. pluginsUnderX.Select(p => new CatalogPlugin(p, null, string.Empty, [], []))]
                        ),
                        new CatalogMarketplace("y", null, []),
                    ]
                )
                {
                    Capabilities = new MarketplaceCapabilities(pluginFiltering),
                }
            );
    }

    /// <summary>
    /// Hand-written stand-in for the migration service. Written by hand rather than mocked because the
    /// real implementation is sealed, and because these tests need the CALL COUNT: "an update that
    /// omits PluginSelection must not migrate" is only provable by observing that nothing was invoked.
    /// </summary>
    private sealed class StubPluginSelection(Exception? failure = null, Workspace? result = null)
        : IWorkspacePluginSelectionService
    {
        public int Calls { get; private set; }

        public Task<Workspace> ApplyPluginSelectionUpdateAsync(
            string workspaceId,
            WorkspaceUpdate dto,
            CancellationToken ct = default)
        {
            Calls++;
            return failure is not null
                ? Task.FromException<Workspace>(failure)
                : Task.FromResult(
                    result
                        ?? new Workspace
                        {
                            Id = workspaceId,
                            Name = "Migrated",
                            DirectoryRelPath = "migrated",
                        }
                );
        }
    }
}
