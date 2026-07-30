namespace LmStreaming.Sample.Tests.Persistence;

public sealed class GatewayWorkspaceCatalogResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveAsync_IsolatesGatewayAndAppCatalogs()
    {
        var resolver = new GatewayWorkspaceCatalogResolver();
        var a = await resolver.ResolveAsync(
            _root,
            GatewayWorkspaceCatalogIdentity.Create("http://gateway:3000", "app-a")
        );
        var b = await resolver.ResolveAsync(
            _root,
            GatewayWorkspaceCatalogIdentity.Create("http://gateway:3000", "app-b")
        );

        a.CatalogDirectory.Should().NotBe(b.CatalogDirectory);
        var storeA = new FileWorkspaceStore(a.CatalogDirectory);
        var storeB = new FileWorkspaceStore(b.CatalogDirectory);
        _ = await storeA.CreateAsync(new WorkspaceCreate { Name = "Only A" });

        (await storeA.GetAllAsync()).Should().Contain(x => x.Name == "Only A");
        (await storeB.GetAllAsync()).Should().NotContain(x => x.Name == "Only A");
    }

    [Fact]
    public async Task ResolveAsync_ArchivesLegacyCatalogWithoutImportingIt()
    {
        Directory.CreateDirectory(_root);
        var legacy = new[]
        {
            new Workspace
            {
                Id = Guid.NewGuid().ToString(),
                Name = "Old Gateway",
                DirectoryRelPath = "old-gateway",
            },
        };
        await File.WriteAllTextAsync(
            Path.Combine(_root, "workspaces.json"),
            JsonSerializer.Serialize(
                legacy,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
            )
        );

        var result = await new GatewayWorkspaceCatalogResolver().ResolveAsync(
            _root,
            GatewayWorkspaceCatalogIdentity.Create("http://remote:3000", "sample")
        );

        File.Exists(Path.Combine(_root, "workspaces.json")).Should().BeFalse();
        result.LegacyArchivePath.Should().NotBeNull();
        File.Exists(result.LegacyArchivePath!).Should().BeTrue();
        File.Exists(Path.Combine(_root, "legacy", "migration.json")).Should().BeTrue();
        (await new FileWorkspaceStore(result.CatalogDirectory).GetAllAsync())
            .Should().ContainSingle(x => x.IsSystemDefined);
    }

    [Fact]
    public async Task ResolveAsync_IsIdempotentAndCreatesSingleArchive()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "workspaces.json"), "[]");
        var resolver = new GatewayWorkspaceCatalogResolver();
        var identity = GatewayWorkspaceCatalogIdentity.Create("http://remote:3000", "sample");

        var first = await resolver.ResolveAsync(_root, identity);
        var second = await resolver.ResolveAsync(_root, identity);

        second.CatalogDirectory.Should().Be(first.CatalogDirectory);
        Directory.GetFiles(Path.Combine(_root, "legacy"), "workspaces.*.json")
            .Should().ContainSingle();
    }

    [Fact]
    public async Task ResolveAsync_RejectsMismatchedManifest()
    {
        var identity = GatewayWorkspaceCatalogIdentity.Create("http://remote:3000", "sample");
        var directory = Path.Combine(_root, "gateways", identity.CatalogKey);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "gateway.json"),
            """{"schemaVersion":1,"derivationVersion":1,"canonicalBaseUrl":"http://other:3000","appId":"sample"}"""
        );

        var act = () => new GatewayWorkspaceCatalogResolver().ResolveAsync(_root, identity);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*mismatch*");
    }

    [Fact]
    public async Task ResolveAsync_CorruptLegacyPreservesArchiveAndFails()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "workspaces.json"), "not-json");

        var act = () => new GatewayWorkspaceCatalogResolver().ResolveAsync(
            _root,
            GatewayWorkspaceCatalogIdentity.Create("http://remote:3000", "sample")
        );

        await act.Should().ThrowAsync<WorkspaceCatalogCorruptException>();
        Directory.GetFiles(Path.Combine(_root, "legacy"), "workspaces.*.json")
            .Should().ContainSingle();
    }

    [Fact]
    public async Task ResolveAsync_ConcurrentCallsProduceOneArchive()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "workspaces.json"), "[]");
        var identity = GatewayWorkspaceCatalogIdentity.Create("http://remote:3000", "sample");

        await Task.WhenAll(
            new GatewayWorkspaceCatalogResolver().ResolveAsync(_root, identity),
            new GatewayWorkspaceCatalogResolver().ResolveAsync(_root, identity)
        );

        Directory.GetFiles(Path.Combine(_root, "legacy"), "workspaces.*.json")
            .Should().ContainSingle();
    }
}
