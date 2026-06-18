using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.Persistence;

/// <summary>
/// Unit tests for <see cref="FileWorkspaceStore"/> — seeded default + user persistence, directory
/// sanitization/uniqueness on create, and marketplaces-only edit semantics on update.
/// </summary>
public class FileWorkspaceStoreTests
{
    private static string NewTempDir()
    {
        return Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    }

    [Fact]
    public async Task CreateAsync_DefaultsDirectoryToSanitizedName_WhenOmitted()
    {
        var store = new FileWorkspaceStore(NewTempDir());

        var created = await store.CreateAsync(new WorkspaceCreate { Name = "My Project" });

        created.DirectoryRelPath.Should().Be("my-project");
        created.Name.Should().Be("My Project");
    }

    [Fact]
    public async Task CreateAsync_UsesProvidedDirectory_WhenSupplied()
    {
        var store = new FileWorkspaceStore(NewTempDir());

        var created = await store.CreateAsync(
            new WorkspaceCreate { Name = "Anything", DirectoryRelPath = "Custom Dir" }
        );

        created.DirectoryRelPath.Should().Be("custom-dir");
    }

    [Fact]
    public async Task CreateAsync_AssignsServerSideId_EqualTimestamps_AndNotSystemDefined()
    {
        var store = new FileWorkspaceStore(NewTempDir());

        var created = await store.CreateAsync(new WorkspaceCreate { Name = "Proj" });

        Guid.TryParse(created.Id, out _).Should().BeTrue();
        created.IsSystemDefined.Should().BeFalse();
        created.CreatedAt.Should().Be(created.UpdatedAt);
        created.CreatedAt.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task CreateAsync_DefaultsMarketplacesToEmpty_WhenNull()
    {
        var store = new FileWorkspaceStore(NewTempDir());

        var created = await store.CreateAsync(new WorkspaceCreate { Name = "Proj", Marketplaces = null });

        created.Marketplaces.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_RejectsDuplicateDirectory_CaseInsensitive()
    {
        var store = new FileWorkspaceStore(NewTempDir());
        _ = await store.CreateAsync(new WorkspaceCreate { Name = "Repo" });

        var act = async () => await store.CreateAsync(new WorkspaceCreate { Name = "REPO" });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already exists*");
    }

    [Fact]
    public async Task CreateAsync_RejectsCollisionWithDefaultDirectory()
    {
        var store = new FileWorkspaceStore(NewTempDir(), defaultDirectoryRelPath: "main");

        var act = async () => await store.CreateAsync(new WorkspaceCreate { Name = "Main" });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already exists*");
    }

    [Fact]
    public async Task CreateAsync_Rejects_WhenNameEmpty()
    {
        var store = new FileWorkspaceStore(NewTempDir());

        var act = async () => await store.CreateAsync(new WorkspaceCreate { Name = "   " });

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData("..")]
    [InlineData("/")]
    [InlineData("\\")]
    [InlineData("../..")]
    public async Task CreateAsync_Rejects_WhenDirectoryEmptyAfterSanitize(string dir)
    {
        var store = new FileWorkspaceStore(NewTempDir());

        var act = async () =>
            await store.CreateAsync(new WorkspaceCreate { Name = "Valid Name", DirectoryRelPath = dir });

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CreateAsync_SanitizesEscapingAndSeparators()
    {
        var store = new FileWorkspaceStore(NewTempDir());

        var created = await store.CreateAsync(
            new WorkspaceCreate { Name = "x", DirectoryRelPath = "a/../b\\c" }
        );

        created.DirectoryRelPath.Should().NotContain("/");
        created.DirectoryRelPath.Should().NotContain("\\");
        created.DirectoryRelPath.Should().NotContain("..");
    }

    [Fact]
    public async Task GetAllAsync_ReturnsSeedDefaultFirst_ThenUsersByName()
    {
        var store = new FileWorkspaceStore(NewTempDir(), defaultDirectoryRelPath: "main");
        _ = await store.CreateAsync(new WorkspaceCreate { Name = "Zeta" });
        _ = await store.CreateAsync(new WorkspaceCreate { Name = "Alpha" });

        var all = await store.GetAllAsync();

        all.Should().HaveCount(3);
        all[0].Id.Should().Be(SandboxSessionRegistry.DefaultWorkspaceId);
        all[0].IsSystemDefined.Should().BeTrue();
        all[0].DirectoryRelPath.Should().Be("main");
        all[1].Name.Should().Be("Alpha");
        all[2].Name.Should().Be("Zeta");
    }

    [Fact]
    public async Task GetAsync_ReturnsDefault_AndUser_AndNullForUnknown()
    {
        var store = new FileWorkspaceStore(NewTempDir());
        var created = await store.CreateAsync(new WorkspaceCreate { Name = "Proj" });

        (await store.GetAsync(SandboxSessionRegistry.DefaultWorkspaceId)).Should().NotBeNull();
        (await store.GetAsync(created.Id))!.Name.Should().Be("Proj");
        (await store.GetAsync("nope")).Should().BeNull();
    }

    [Fact]
    public async Task CreatedWorkspace_PersistsAcrossNewStoreInstance()
    {
        var dir = NewTempDir();
        var store1 = new FileWorkspaceStore(dir);
        var created = await store1.CreateAsync(new WorkspaceCreate { Name = "Persisted" });

        var store2 = new FileWorkspaceStore(dir);
        var fetched = await store2.GetAsync(created.Id);

        fetched.Should().NotBeNull();
        fetched!.Name.Should().Be("Persisted");
        fetched.DirectoryRelPath.Should().Be("persisted");
    }

    [Fact]
    public async Task UpdateAsync_ReplacesMarketplaces_AndBumpsUpdatedAt()
    {
        var store = new FileWorkspaceStore(NewTempDir());
        var created = await store.CreateAsync(
            new WorkspaceCreate { Name = "Proj", Marketplaces = ["one"] }
        );

        // Ensure the timestamp clock moves between create and update.
        await Task.Delay(2);
        var updated = await store.UpdateAsync(created.Id, new WorkspaceUpdate { Marketplaces = ["a", "b"] });

        updated.Marketplaces.Should().Equal("a", "b");
        updated.Name.Should().Be("Proj");
        updated.DirectoryRelPath.Should().Be(created.DirectoryRelPath);
        updated.UpdatedAt.Should().BeGreaterThan(created.UpdatedAt);
        updated.CreatedAt.Should().Be(created.CreatedAt);
    }

    [Fact]
    public async Task UpdateAsync_Throws_KeyNotFound_ForUnknownId()
    {
        var store = new FileWorkspaceStore(NewTempDir());

        var act = async () => await store.UpdateAsync("missing", new WorkspaceUpdate { Marketplaces = [] });

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task UpdateAsync_Throws_InvalidOperation_ForSystemDefinedDefault()
    {
        var store = new FileWorkspaceStore(NewTempDir());

        var act = async () =>
            await store.UpdateAsync(
                SandboxSessionRegistry.DefaultWorkspaceId,
                new WorkspaceUpdate { Marketplaces = ["x"] }
            );

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task UpdatedMarketplaces_PersistAcrossNewStoreInstance()
    {
        var dir = NewTempDir();
        var store1 = new FileWorkspaceStore(dir);
        var created = await store1.CreateAsync(new WorkspaceCreate { Name = "Proj" });
        _ = await store1.UpdateAsync(created.Id, new WorkspaceUpdate { Marketplaces = ["m1", "m2"] });

        var store2 = new FileWorkspaceStore(dir);
        var fetched = await store2.GetAsync(created.Id);

        fetched!.Marketplaces.Should().Equal("m1", "m2");
    }
}
