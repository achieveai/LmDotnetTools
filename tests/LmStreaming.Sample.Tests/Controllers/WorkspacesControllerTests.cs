
using System.Net;
using System.Text;
using LmStreaming.Sample.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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

    /// <summary>
    /// #459 wire contract: an unreachable catalog serializes as <c>"unavailable"</c>, not
    /// <c>"unknown"</c> and not <c>"incompatible"</c>. The literal is asserted because it is the
    /// value the SPA branches on — an enum rename that did not reach the wire would leave the picker
    /// reading a state it has no case for.
    /// </summary>
    [Fact]
    public async Task List_UnreachableCatalog_ReportsUnavailableRatherThanIncompatible()
    {
        var (controller, store) = Build(catalog: new OfflineCatalogClient());
        _ = await store.CreateAsync(new WorkspaceCreate { Name = "Mine", Marketplaces = ["x"] });

        var result = await controller.List();
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<WorkspaceListResponse>().Subject;

        response.Workspaces.Should().NotBeEmpty();
        response.Workspaces.Select(w => w.Compatibility).Should().AllBe("unavailable");
        response.Workspaces.Should().NotContain(w => w.Compatibility == "incompatible");

        // The gateway envelope still says "unavailable" — the split does not pretend the catalog is
        // readable, it only stops that fact being spelled as a per-workspace refusal.
        response.Gateway.Available.Should().BeFalse();
        response.Gateway.Error.Should().Contain("offline");
    }

    /// <summary>
    /// The distinguishing case for the test above: a catalog that ANSWERS and does not offer the
    /// alias serializes as <c>"incompatible"</c> and leaves the gateway marked available. Without
    /// this pair, an implementation that emitted one string for both states would still be green.
    /// </summary>
    [Fact]
    public async Task List_ReachableCatalogMissingTheAlias_ReportsIncompatibleAndAvailableGateway()
    {
        var (controller, store) = Build();
        _ = await store.CreateAsync(new WorkspaceCreate { Name = "Mine", Marketplaces = ["nope"] });

        var result = await controller.List();
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<WorkspaceListResponse>().Subject;

        response.Workspaces.Should().Contain(w => w.Compatibility == "incompatible");
        response.Workspaces.Should().NotContain(w => w.Compatibility == "unavailable");
        response.Gateway.Available.Should().BeTrue();
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

    #region Explicit JSON null on a non-nullable member

    // These go over real HTTP rather than calling the action directly, because the whole question is
    // what MODEL BINDING produces. A direct `controller.Update(id, new WorkspaceUpdate
    // { Marketplaces = null! })` proves only that the action mishandles a null it was handed; nothing
    // but a real request proves a null can be handed to it in the first place.

    /// <summary>
    /// The three payload shapes a client can send for <c>marketplaces</c> on an update, driven through
    /// the live pipeline. Omitted and explicit-<c>null</c> deliberately do NOT agree, and the
    /// difference is worth keeping.
    /// <para>
    /// Omitted leaves the <c>= []</c> initializer standing, which the store then applies as a
    /// replacement set — an update that names no marketplaces clears them. Explicit <c>null</c> is
    /// refused with a 400 by MVC's implicit-required convention for non-nullable reference-type
    /// members, before the action runs at all. Keeping that refusal is the safer half of the pair:
    /// <c>marketplaces</c> on an update is a REPLACEMENT set, so reading a stray <c>null</c> as
    /// "clear them" would let a client that emitted one by accident wipe a live workspace's
    /// marketplaces and be told nothing. <see cref="WorkspaceCreate"/> can afford to be lenient
    /// (and is, via <c>?? []</c>) because a workspace being created has nothing to wipe.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("{}", HttpStatusCode.OK)]
    [InlineData(/*lang=json,strict*/ "{\"marketplaces\":[]}", HttpStatusCode.OK)]
    [InlineData(/*lang=json,strict*/ "{\"marketplaces\":null}", HttpStatusCode.BadRequest)]
    public async Task Put_MarketplacesOmittedEmptyOrExplicitNull(string body, HttpStatusCode expected)
    {
        await using var app = await HttpApp.StartAsync();
        var created = await app.Store.CreateAsync(new WorkspaceCreate { Name = "Proj", Marketplaces = ["x"] });

        var response = await app.PutAsync($"/api/workspaces/{created.Id}", body);

        response.StatusCode.Should().Be(expected);
        // The refused row must leave the workspace exactly as it was: a 400 that had already
        // half-applied the update would be worse than the 200 it replaced.
        string[] expectedMarketplaces = expected == HttpStatusCode.OK ? [] : ["x"];
        (await app.Store.GetAsync(created.Id))!.Marketplaces.Should().BeEquivalentTo(expectedMarketplaces);
    }

    /// <summary>
    /// Positive control for the theory above. An empty marketplace list is always compatible, so every
    /// accepted row there would still pass under a mutation that skipped validation entirely. This one
    /// proves the request reaches the compatibility check and can still be refused on its merits.
    /// </summary>
    [Fact]
    public async Task Put_MarketplacesUnsupported_StillReturns400()
    {
        await using var app = await HttpApp.StartAsync();
        var created = await app.Store.CreateAsync(new WorkspaceCreate { Name = "Proj" });

        var response = await app.PutAsync(
            $"/api/workspaces/{created.Id}",
            /*lang=json,strict*/ "{\"marketplaces\":[\"not-a-real-alias\"]}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("unsupported_marketplaces");
    }

    /// <summary>
    /// A null ELEMENT inside a present array is a separate binding outcome from a null array, and it
    /// reaches a different line: the alias loop in <c>EvaluateAsync</c> hashes and compares each entry.
    /// Pinned as a 400 rather than a 500 so the element case cannot regress silently while the array
    /// case is guarded.
    /// </summary>
    [Fact]
    public async Task Put_MarketplacesWithNullElement_Returns400NotServerError()
    {
        await using var app = await HttpApp.StartAsync();
        var created = await app.Store.CreateAsync(new WorkspaceCreate { Name = "Proj" });

        var response = await app.PutAsync(
            $"/api/workspaces/{created.Id}",
            /*lang=json,strict*/ "{\"marketplaces\":[null]}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// The create path already normalized with <c>?? []</c>, so this is a characterization test, not a
    /// regression fixed here. It exists so the two mutation entry points are pinned to the same
    /// behaviour: a future edit that drops the <c>??</c> on create reds this rather than shipping the
    /// defect on the sibling endpoint.
    /// </summary>
    [Fact]
    public async Task Post_MarketplacesExplicitNull_Succeeds()
    {
        await using var app = await HttpApp.StartAsync();

        var response = await app.PostAsync(
            "/api/workspaces",
            /*lang=json,strict*/ "{\"name\":\"Fresh\",\"marketplaces\":null}");

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await ReadViewAsync(response)).Marketplaces.Should().BeEmpty();
    }

    /// <summary>
    /// The real, reachable instance of the same trap, and the one this commit fixes. The store's
    /// WRITE paths normalize with <c>?? []</c>; its READ path did not, so an explicit
    /// <c>"marketplaces": null</c> in <c>workspaces.json</c> deserialized straight into the
    /// non-nullable member and every reader threw. The listing is the worst of them: one bad entry
    /// made the whole catalog unreadable, so the UI could not load and no API call could repair it.
    /// </summary>
    [Fact]
    public async Task Get_PersistedNullMarketplaces_ListsNormalizedInsteadOfFailing()
    {
        await using var app = await HttpApp.StartAsync();
        var created = await app.Store.CreateAsync(new WorkspaceCreate { Name = "Proj" });
        await app.CorruptCatalogAsync(json => json.Replace(
            "\"marketplaces\": []", "\"marketplaces\": null", StringComparison.Ordinal));

        var response = await app.Client.GetAsync("/api/workspaces");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var entry = doc.RootElement.GetProperty("workspaces").EnumerateArray()
            .Should().ContainSingle(w => w.GetProperty("id").GetString() == created.Id).Subject;
        entry.GetProperty("marketplaces").EnumerateArray().Should().BeEmpty();
    }

    /// <summary>
    /// Same defect through the single-workspace read, which reaches the identical evaluation from a
    /// different action. Pinned separately because a guard placed in <c>List</c> alone would leave
    /// this one throwing.
    /// </summary>
    [Fact]
    public async Task Get_PersistedNullMarketplaces_ReadsBackAsEmptyList()
    {
        await using var app = await HttpApp.StartAsync();
        var created = await app.Store.CreateAsync(new WorkspaceCreate { Name = "Proj" });
        await app.CorruptCatalogAsync(json => json.Replace(
            "\"marketplaces\": []", "\"marketplaces\": null", StringComparison.Ordinal));

        var response = await app.Client.GetAsync($"/api/workspaces/{created.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadViewAsync(response)).Marketplaces.Should().BeEmpty();
    }

    /// <summary>
    /// A null ENTRY is a different kind of damage and gets a different answer. There is no workspace
    /// there to normalize, so it is reported as a corrupt catalog (503) rather than dropped: silently
    /// skipping it would make a truncated or half-written file look like a successful deletion.
    /// </summary>
    [Fact]
    public async Task Get_PersistedNullCatalogEntry_Returns503RatherThanFailing()
    {
        await using var app = await HttpApp.StartAsync();
        _ = await app.Store.CreateAsync(new WorkspaceCreate { Name = "Proj" });
        await app.CorruptCatalogAsync(_ => "[null]");

        var response = await app.Client.GetAsync("/api/workspaces");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).Should().Contain("workspace_catalog_unavailable");
    }

    private static async Task<WorkspaceView> ReadViewAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<WorkspaceView>(json, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException($"Response was not a workspace view: {json}");
    }

    /// <summary>
    /// Minimal in-process <see cref="TestServer"/> hosting the real <see cref="WorkspacesController"/>
    /// as an application part, so requests flow through real routing, real <c>[FromBody]</c> binding
    /// and the real MVC JSON options. Booting all of <c>Program</c> is deliberately avoided — its
    /// startup does blocking I/O (MCP clients, sandbox/session spawn) that adds nothing to the binding
    /// path under test, matching <see cref="ContextDiscoveryWebhookHttpTests"/>.
    /// </summary>
    private sealed class HttpApp : IAsyncDisposable
    {
        private readonly IHost _host;

        private HttpApp(IHost host, HttpClient client, FileWorkspaceStore store, string dir)
        {
            _host = host;
            Client = client;
            Store = store;
            Dir = dir;
        }

        public HttpClient Client { get; }
        public FileWorkspaceStore Store { get; }
        public string Dir { get; }

        /// <summary>
        /// Rewrites <c>workspaces.json</c> underneath the running store, which is how a hand-edited,
        /// externally-written or half-written catalog reaches the read path. Nothing in the app can
        /// produce these shapes through its own writers — that is precisely why the read path was
        /// trusted and why the damage only shows up on the way back in.
        /// </summary>
        public async Task CorruptCatalogAsync(Func<string, string> rewrite)
        {
            var file = Path.Combine(Dir, "workspaces.json");
            var json = await File.ReadAllTextAsync(file);
            var rewritten = rewrite(json);
            rewritten.Should().NotBe(json, "the rewrite must actually change the catalog on disk");
            await File.WriteAllTextAsync(file, rewritten);
        }

        public static async Task<HttpApp> StartAsync()
        {
            var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            var store = new FileWorkspaceStore(dir, null);
            var compatibility = new WorkspaceCatalogCompatibilityService(
                new SupportedCatalogClient(),
                new SandboxGatewayOptions()
            );
            var identity = GatewayWorkspaceCatalogIdentity.Create("http://gateway:3000", "sample");

            var host = await new HostBuilder()
                .ConfigureWebHost(webBuilder =>
                {
                    webBuilder.UseTestServer();
                    webBuilder.ConfigureServices(services =>
                    {
                        _ = services.AddControllers()
                            .AddApplicationPart(typeof(WorkspacesController).Assembly);
                        _ = services.AddSingleton<IWorkspaceStore>(store);
                        _ = services.AddSingleton(compatibility);
                        _ = services.AddSingleton(identity);
                        // Same reasoning as Build(): a stub that throws if touched, so "an ordinary
                        // marketplace edit silently migrated" cannot pass as green.
                        _ = services.AddSingleton<IWorkspacePluginSelectionService>(
                            new StubPluginSelection(new NotSupportedException("migration must not run"))
                        );
                    });
                    webBuilder.Configure(appBuilder =>
                    {
                        appBuilder.UseRouting();
                        _ = appBuilder.UseEndpoints(endpoints => endpoints.MapControllers());
                    });
                })
                .StartAsync();

            return new HttpApp(host, host.GetTestClient(), store, dir);
        }

        public Task<HttpResponseMessage> PutAsync(string path, string json) => SendAsync(HttpMethod.Put, path, json);

        public Task<HttpResponseMessage> PostAsync(string path, string json) => SendAsync(HttpMethod.Post, path, json);

        private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string json)
        {
            using var request = new HttpRequestMessage(method, path)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            return await Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    #endregion

    /// <summary>A gateway that cannot be reached at all — the gateway-less host of #459.</summary>
    private sealed class OfflineCatalogClient : IMarketplaceCatalogClient
    {
        public Task<MarketplaceCatalog> GetCatalogAsync(
            IReadOnlyList<string>? marketplaces = null,
            CancellationToken ct = default) =>
            Task.FromException<MarketplaceCatalog>(
                new MarketplaceCatalogUnavailableException("gateway offline")
            );
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
