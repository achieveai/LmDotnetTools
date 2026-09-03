using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmTestUtils.Persistence;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Persistence;

/// <summary>
/// Covers the durability of <c>FileConversationStore</c>'s write-temp-then-rename path against the two
/// hazards its <c>_lock</c> cannot cover.
/// <para>
/// The lock is a per-INSTANCE <see cref="SemaphoreSlim"/>. It serializes writers inside one store object
/// and nothing else, so two stores over the same base directory — the shape
/// <c>ConversationContextReportTests(kind:"file")</c> and <c>InputAcceptanceStoreTests</c> construct — run
/// the write path concurrently, as does any second process. That leaves two failures the lock is
/// structurally unable to prevent:
/// </para>
/// <list type="number">
/// <item>a temp file named deterministically after its target collides between concurrent writers; and</item>
/// <item><c>File.Move</c> on Windows fails outright while ANY handle is open on the destination.</item>
/// </list>
/// <para>
/// Both tests below hold a real handle rather than racing threads and hoping. A race-based test for a
/// window this narrow goes green on a fast box whether or not the defect is present, which is precisely
/// the failure mode a concurrency fix must not be verified by.
/// </para>
/// </summary>
public sealed class FileConversationStoreConcurrentWriteTests : IDisposable
{
    private readonly string _root;

    public FileConversationStoreConcurrentWriteTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"FileStoreConcurrentWrite_{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        // #477: detach-then-delete rather than recursive-delete in place — see DetachedStoreTeardown.
        DetachedStoreTeardown.Purge(_root);
    }

    /// <summary>
    /// Pins the temp-name collision. Holding <c>{metadata.json}.tmp</c> stands in for a concurrent writer
    /// whose own temp file is still open — the deterministic name means there is only ever one such path
    /// per target, so every writer to a file contends for it.
    /// <para>
    /// Against a deterministic temp name this write cannot even reach the rename: the
    /// <c>WriteAllTextAsync</c> onto the held path fails first. A per-write unique name removes the
    /// contention by construction, so the held path is simply not one this writer ever touches.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SaveMetadata_SucceedsWhileAnotherWritersTempFileForTheSameTargetIsOpen()
    {
        var store = new FileConversationStore(_root);
        const string threadId = "thread-temp-collision";

        // Establish the target so its directory and path are real before the handle is taken.
        await store.SaveMetadataAsync(threadId, NewMetadata(threadId, 1));

        var metadataFile = Path.Combine(_root, threadId, "metadata.json");
        var deterministicTemp = metadataFile + ".tmp";

        // The other writer's in-flight temp file. FileShare.None is what a writer holding its own
        // output looks like to everyone else.
        await File.WriteAllTextAsync(deterministicTemp, "{}");
        using (var _ = new FileStream(deterministicTemp, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var act = async () => await store.SaveMetadataAsync(threadId, NewMetadata(threadId, 2));

            await act.Should().NotThrowAsync();
        }

        // The write must have actually landed, not merely declined to throw.
        var reloaded = await store.LoadMetadataAsync(threadId);
        reloaded.Should().NotBeNull();
        reloaded!.LastUpdated.Should().Be(2);
    }

    /// <summary>
    /// Pins the missing rename retry. A reader opened through <c>File.ReadAllTextAsync</c> holds
    /// <c>FileShare.Read</c>, which withholds delete access, and Windows <c>MoveFile</c> with
    /// <c>REPLACE_EXISTING</c> needs it — so a plain concurrent read of the destination is enough to make
    /// the rename throw <see cref="UnauthorizedAccessException"/>. That is the reported failure, seen via
    /// <c>UpdateMetadataAsync</c> under <c>CompactionStateProjection</c>.
    /// <para>
    /// The handle is released partway through the store's retry budget, so this asserts the write WAITS
    /// OUT a transient holder rather than that it tolerates a permanent one. Without a retry the very
    /// first rename throws and the test fails; a fixed sleep in the store would not pass it either,
    /// because the release happens after the first attempt has already been made.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SaveMetadata_WaitsOutATransientReaderHoldingTheDestination()
    {
        var store = new FileConversationStore(_root);
        const string threadId = "thread-move-retry";

        await store.SaveMetadataAsync(threadId, NewMetadata(threadId, 1));

        var metadataFile = Path.Combine(_root, threadId, "metadata.json");

        // Exactly the sharing a concurrent File.ReadAllTextAsync of this file would take.
        var reader = new FileStream(metadataFile, FileMode.Open, FileAccess.Read, FileShare.Read);

        var released = false;
        var write = Task.Run(async () =>
        {
            await store.SaveMetadataAsync(threadId, NewMetadata(threadId, 2));
        });

        // Give the rename at least one attempt against the live handle before letting go, so a passing
        // run proves the retry ran rather than that the handle was gone before the store looked.
        await Task.Delay(60);
        released = true;
        await reader.DisposeAsync();

        var act = async () => await write;
        await act.Should().NotThrowAsync();
        released.Should().BeTrue();

        var reloaded = await store.LoadMetadataAsync(threadId);
        reloaded.Should().NotBeNull();
        reloaded!.LastUpdated.Should().Be(2);
    }

    /// <summary>
    /// The end-to-end shape the other two decompose: separate store instances over one base directory,
    /// each writing the same thread's metadata. Nothing here is shared but the filesystem, which is the
    /// point — the per-instance lock offers no protection at all across these writers.
    /// </summary>
    [Fact]
    public async Task SaveMetadata_ConcurrentStoreInstancesOverOneDirectoryAllSucceed()
    {
        const string threadId = "thread-cross-instance";
        const int writers = 8;
        const int writesEach = 15;

        var stores = Enumerable.Range(0, writers).Select(_ => new FileConversationStore(_root)).ToArray();

        var tasks = stores
            .Select(store =>
                Task.Run(async () =>
                {
                    for (var i = 0; i < writesEach; i++)
                    {
                        await store.SaveMetadataAsync(threadId, NewMetadata(threadId, i));
                    }
                })
            )
            .ToArray();

        var act = async () => await Task.WhenAll(tasks);
        await act.Should().NotThrowAsync();

        // A surviving temp file means a writer died between its write and its rename.
        Directory
            .GetFiles(Path.Combine(_root, threadId), "*.tmp")
            .Should()
            .BeEmpty("a completed write must leave no temp file behind");

        // Whoever landed last, the file must be a whole document rather than an interleaving of two.
        var reloaded = await stores[0].LoadMetadataAsync(threadId);
        reloaded.Should().NotBeNull();
        reloaded!.ThreadId.Should().Be(threadId);
    }

    private static ThreadMetadata NewMetadata(string threadId, long lastUpdated) =>
        new() { ThreadId = threadId, LastUpdated = lastUpdated };
}
