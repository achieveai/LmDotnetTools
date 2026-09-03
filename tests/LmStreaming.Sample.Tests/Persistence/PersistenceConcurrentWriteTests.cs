using AchieveAi.LmDotnetTools.LmTestUtils.Persistence;

namespace LmStreaming.Sample.Tests.Persistence;

/// <summary>
/// Covers the durability of the three remaining sample write-temp-then-rename paths —
/// <see cref="FileChatModeStore"/>, <see cref="FileWorkspaceStore"/> and
/// <see cref="GatewayWorkspaceCatalogResolver"/> — against the two hazards a per-instance lock
/// cannot cover. Same defect and same shape as
/// <c>LmMultiTurn.Tests.Persistence.FileConversationStoreConcurrentWriteTests</c>, which pinned the
/// identical window in <c>FileConversationStore</c>.
/// <para>
/// The stores' <c>_lock</c> is a per-INSTANCE <see cref="SemaphoreSlim"/>. It serializes writers
/// inside one store object and nothing else, so two stores over one base directory — which is what
/// every controller resolving a per-gateway catalog directory constructs — run the write path
/// concurrently, as does any second process. That leaves two failures the lock is structurally
/// unable to prevent:
/// </para>
/// <list type="number">
/// <item>a temp file named deterministically after its target collides between concurrent writers; and</item>
/// <item><c>File.Move</c> on Windows fails outright while ANY handle is open on the destination.</item>
/// </list>
/// <para>
/// Every test below holds a real handle rather than racing threads and hoping. A race-based test for
/// a window this narrow goes green on a fast box whether or not the defect is present, which is
/// precisely the failure mode a concurrency fix must not be verified by.
/// </para>
/// </summary>
public sealed class PersistenceConcurrentWriteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"LmStreamingConcurrentWrite_{Guid.NewGuid():N}");

    public void Dispose()
    {
        // #477: detach-then-delete rather than recursive-delete in place — see DetachedStoreTeardown.
        DetachedStoreTeardown.Purge(_root);
    }

    /// <summary>
    /// Pins the temp-name collision in <see cref="FileChatModeStore"/>. Holding
    /// <c>{chat-modes.json}.tmp</c> stands in for a concurrent writer whose own temp file is still
    /// open — the deterministic name means there is only ever one such path per target, so every
    /// writer to the file contends for it.
    /// <para>
    /// Against a deterministic temp name this write cannot even reach the rename: the
    /// <c>WriteAllTextAsync</c> onto the held path fails first. A per-write unique name removes the
    /// contention by construction, so the held path is simply not one this writer ever touches.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ChatModeStore_Create_SucceedsWhileAnotherWritersTempFileForTheSameTargetIsOpen()
    {
        var dir = Path.Combine(_root, "modes-temp-collision");
        var store = new FileChatModeStore(dir);

        // Establish the target so its path is real before the handle is taken.
        _ = await store.CreateModeAsync(new ChatModeCreateUpdate { Name = "First", SystemPrompt = "p" });

        var deterministicTemp = Path.Combine(dir, "chat-modes.json") + ".tmp";

        // The other writer's in-flight temp file. FileShare.None is what a writer holding its own
        // output looks like to everyone else.
        await File.WriteAllTextAsync(deterministicTemp, "[]");
        using (var _ = new FileStream(deterministicTemp, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var act = async () =>
                await store.CreateModeAsync(new ChatModeCreateUpdate { Name = "Second", SystemPrompt = "p" });

            await act.Should().NotThrowAsync();
        }

        // The write must have actually landed, not merely declined to throw.
        (await store.GetAllModesAsync())
            .Should()
            .Contain(m => m.Name == "Second");
    }

    /// <summary>
    /// Pins the missing rename retry in <see cref="FileChatModeStore"/>. A reader opened through
    /// <c>File.ReadAllTextAsync</c> holds <c>FileShare.Read</c>, which withholds delete access, and
    /// Windows <c>MoveFile</c> with <c>REPLACE_EXISTING</c> needs it — so a plain concurrent read of
    /// the destination is enough to make the rename throw <see cref="UnauthorizedAccessException"/>.
    /// A second store instance over the same directory doing exactly that is unremarkable here: the
    /// modes file is read on every <c>GetAllModesAsync</c>, which the mode picker calls constantly.
    /// <para>
    /// The handle is released partway through the helper's retry budget, so this asserts the write
    /// WAITS OUT a transient holder rather than that it tolerates a permanent one. Without a retry
    /// the very first rename throws and the test fails; a fixed sleep in the store would not pass it
    /// either, because the release happens after the first attempt has already been made.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ChatModeStore_Create_WaitsOutATransientReaderHoldingTheDestination()
    {
        var dir = Path.Combine(_root, "modes-move-retry");
        var store = new FileChatModeStore(dir);

        _ = await store.CreateModeAsync(new ChatModeCreateUpdate { Name = "First", SystemPrompt = "p" });

        var modesFile = Path.Combine(dir, "chat-modes.json");

        // Exactly the sharing a concurrent File.ReadAllTextAsync of this file would take.
        var reader = new FileStream(modesFile, FileMode.Open, FileAccess.Read, FileShare.Read);

        var released = false;
        var write = Task.Run(async () =>
            await store.CreateModeAsync(new ChatModeCreateUpdate { Name = "Second", SystemPrompt = "p" })
        );

        // Give the rename at least one attempt against the live handle before letting go, so a
        // passing run proves the retry ran rather than that the handle was gone before the store
        // looked.
        await Task.Delay(60);
        released = true;
        await reader.DisposeAsync();

        var act = async () => await write;
        await act.Should().NotThrowAsync();
        released.Should().BeTrue();

        (await store.GetAllModesAsync()).Should().Contain(m => m.Name == "Second");
    }

    /// <inheritdoc cref="ChatModeStore_Create_SucceedsWhileAnotherWritersTempFileForTheSameTargetIsOpen" />
    [Fact]
    public async Task WorkspaceStore_Create_SucceedsWhileAnotherWritersTempFileForTheSameTargetIsOpen()
    {
        var dir = Path.Combine(_root, "workspaces-temp-collision");
        var store = new FileWorkspaceStore(dir);

        _ = await store.CreateAsync(new WorkspaceCreate { Name = "First" });

        var deterministicTemp = Path.Combine(dir, "workspaces.json") + ".tmp";

        await File.WriteAllTextAsync(deterministicTemp, "[]");
        using (var _ = new FileStream(deterministicTemp, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var act = async () => await store.CreateAsync(new WorkspaceCreate { Name = "Second" });

            await act.Should().NotThrowAsync();
        }

        (await store.GetAllAsync()).Should().Contain(w => w.Name == "Second");
    }

    /// <inheritdoc cref="ChatModeStore_Create_WaitsOutATransientReaderHoldingTheDestination" />
    [Fact]
    public async Task WorkspaceStore_Create_WaitsOutATransientReaderHoldingTheDestination()
    {
        var dir = Path.Combine(_root, "workspaces-move-retry");
        var store = new FileWorkspaceStore(dir);

        _ = await store.CreateAsync(new WorkspaceCreate { Name = "First" });

        var workspacesFile = Path.Combine(dir, "workspaces.json");
        var reader = new FileStream(workspacesFile, FileMode.Open, FileAccess.Read, FileShare.Read);

        var released = false;
        var write = Task.Run(async () => await store.CreateAsync(new WorkspaceCreate { Name = "Second" }));

        await Task.Delay(60);
        released = true;
        await reader.DisposeAsync();

        var act = async () => await write;
        await act.Should().NotThrowAsync();
        released.Should().BeTrue();

        (await store.GetAllAsync()).Should().Contain(w => w.Name == "Second");
    }

    /// <summary>
    /// Pins the temp-name collision in <see cref="GatewayWorkspaceCatalogResolver"/>, on the pending
    /// migration marker it stages before archiving a legacy catalog.
    /// <para>
    /// Only the collision is asserted for this type, and deliberately so: all three of its writes are
    /// guarded by a <c>File.Exists</c> check on the destination, so the destination is absent at
    /// rename time and the Windows delete-access rule that the rename retry answers does not arise on
    /// this path. Claiming a retry test here would be asserting something the code cannot reach. The
    /// retry still ships with the shared helper and still covers this path's other holder — a scanner
    /// on the freshly written staging file, which <c>MoveFile</c> also needs delete access to — which
    /// no test can address deterministically once the staging name is unpredictable by design.
    /// </para>
    /// </summary>
    [Fact]
    public async Task CatalogResolver_Resolve_SucceedsWhileAnotherWritersTempFileForTheSameTargetIsOpen()
    {
        var dir = Path.Combine(_root, "catalog-temp-collision");
        var legacyDirectory = Path.Combine(dir, "legacy");
        _ = Directory.CreateDirectory(legacyDirectory);

        // A legacy catalog is what makes the resolver stage a pending migration marker at all.
        await File.WriteAllTextAsync(
            Path.Combine(dir, "workspaces.json"),
            JsonSerializer.Serialize(
                new[]
                {
                    new Workspace
                    {
                        Id = Guid.NewGuid().ToString(),
                        Name = "Old Gateway",
                        DirectoryRelPath = "old-gateway",
                    },
                },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
            )
        );

        var deterministicTemp = Path.Combine(legacyDirectory, "migration.pending.json") + ".tmp";
        await File.WriteAllTextAsync(deterministicTemp, "{}");

        using (var _ = new FileStream(deterministicTemp, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var resolver = new GatewayWorkspaceCatalogResolver();
            var act = async () =>
                await resolver.ResolveAsync(
                    dir,
                    GatewayWorkspaceCatalogIdentity.Create("http://remote:3000", "sample")
                );

            await act.Should().NotThrowAsync();
        }

        // The migration must have actually completed, not merely declined to throw.
        File.Exists(Path.Combine(legacyDirectory, "migration.json")).Should().BeTrue();
        File.Exists(Path.Combine(dir, "workspaces.json")).Should().BeFalse();
    }
}
