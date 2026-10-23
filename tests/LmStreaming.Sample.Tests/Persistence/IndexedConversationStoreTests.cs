using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmTestUtils.Persistence;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Tests.Controllers;
using LmStreaming.Sample.Tests.TestDoubles;

namespace LmStreaming.Sample.Tests.Persistence;

public sealed class IndexedConversationStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"hierarchy-index-{Guid.NewGuid():N}");

    public void Dispose() => DetachedStoreTeardown.Purge(_directory);

    private IndexedConversationStore Create(IConversationStore store) =>
        new(store, Path.Combine(_directory, ".index.json"));

    private static ThreadMetadata Child(string id, string parent, string? tenant = null) =>
        new()
        {
            ThreadId = id,
            LastUpdated = 0,
            TenantId = tenant,
            Properties = ImmutableDictionary<string, object>
                .Empty.Add(SubAgentProvenance.ParentThreadIdKey, parent)
                .Add(SubAgentProvenance.AgentIdKey, id)
                .Add(SubAgentProvenance.StatusKey, "running"),
        };

    [Fact]
    public async Task PrefixLookup_ForwardsToTheWrappedCapability()
    {
        var inner = new Mock<IConversationStore>(MockBehavior.Strict);
        var lookup = inner.As<IConversationThreadIdLookupStore>();
        lookup
            .Setup(store => store.ListThreadIdsByPrefixAsync("subagent-scope-", It.IsAny<CancellationToken>()))
            .ReturnsAsync(["subagent-scope-agent-1"]);
        var store = Create(inner.Object);

        var ids = await store.ListThreadIdsByPrefixAsync("subagent-scope-");

        ids.Should().Equal("subagent-scope-agent-1");
        lookup.Verify(
            capability => capability.ListThreadIdsByPrefixAsync("subagent-scope-", It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task PrefixLookup_FallsBackToListing_WhenTheWrappedStoreLacksTheCapability()
    {
        // A wrapped store without the capability must still answer through the paged listing, or the
        // allocator's first spawn on a counterless root throws instead of falling back.
        var inner = new Mock<IConversationStore>(MockBehavior.Strict);
        inner
            .Setup(store => store.ListThreadsAsync(500, 0, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new ThreadMetadata { ThreadId = "subagent-scope-agent-2", LastUpdated = 0 },
                new ThreadMetadata { ThreadId = "thread-unrelated", LastUpdated = 0 },
                new ThreadMetadata { ThreadId = "subagent-scope-agent-1", LastUpdated = 0 },
            ]);
        var wrapper = Create(inner.Object);

        var ids = await wrapper.ListThreadIdsByPrefixAsync("subagent-scope-");

        ids.Should().Equal("subagent-scope-agent-1", "subagent-scope-agent-2");
    }

    [Fact]
    public async Task BackfillAndLiveUpdates_ReadOnlyTheRequestedHierarchyAfterInitialization()
    {
        var inner = new InMemoryConversationStore();
        await inner.SaveMetadataAsync(
            "root",
            new ThreadMetadata
            {
                ThreadId = "root",
                LastUpdated = 0,
                TenantId = "tenant",
            }
        );
        await inner.SaveMetadataAsync("subagent-old", Child("subagent-old", "root", "tenant"));
        for (var i = 0; i < 2000; i++)
            await inner.SaveMetadataAsync(
                $"unrelated-{i}",
                new ThreadMetadata { ThreadId = $"unrelated-{i}", LastUpdated = i + 1 }
            );
        var counting = new CountingConversationStore(inner);
        var store = Create(counting);
        await store.InitializeAsync();
        counting.ListThreadsCallCount.Should().Be(1);
        var scanner = new ConversationDescendantScanner(store, NullLogger<ConversationDescendantScanner>.Instance);
        (await scanner.GetOrScanAsync("root")).Should().ContainSingle();
        await store.SaveMetadataAsync("subagent-new", Child("subagent-new", "root"));
        await store.SaveMetadataAsync("subagent-foreign", Child("subagent-foreign", "root", "foreign"));
        await store.SaveMetadataAsync("subagent-grandchild", Child("subagent-grandchild", "subagent-new"));
        var nodes = await scanner.GetOrScanAsync("root");
        nodes.Should().HaveCount(3);
        nodes.Should().Contain(n => n.ThreadId == "subagent-grandchild" && n.Depth == 2);
        await store.DeleteThreadAsync("subagent-new");
        (await scanner.ScanAsync("root")).Should().ContainSingle(n => n.ThreadId == "subagent-old");
        counting
            .ListThreadsCallCount.Should()
            .Be(1, "normal reads and metadata mutations must not rescan unrelated history");
        var restarted = Create(counting);
        (await restarted.ReadChildrenAsync("root", true)).Should().ContainSingle(n => n.ThreadId == "subagent-old");
        counting.ListThreadsCallCount.Should().Be(2, "every restart rebuilds before trusting derived state");
    }

    [Fact]
    public async Task ChangingEpoch_ReportsRetryExhaustionWithoutPendingPublication()
    {
        var inner = new Mock<IConversationStore>();
        inner
            .Setup(s =>
                s.ListThreadsAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<ConversationListOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync([]);
        inner
            .Setup(s =>
                s.SaveMetadataAsync(It.IsAny<string>(), It.IsAny<ThreadMetadata>(), It.IsAny<CancellationToken>())
            )
            .Returns(Task.CompletedTask);
        inner
            .Setup(s => s.LoadMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ThreadMetadata?)null);
        var store = Create(inner.Object);
        inner
            .Setup(s => s.LoadMetadataAsync("root", It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await store.SaveMetadataAsync(
                    "unrelated",
                    new ThreadMetadata { ThreadId = "unrelated", LastUpdated = 0 }
                );
                return new ThreadMetadata { ThreadId = "root", LastUpdated = 0 };
            });

        var busy = await Assert.ThrowsAsync<IndexedConversationStore.HierarchyBusyException>(() =>
            store.ReadChildrenAsync("root", true)
        );

        busy.Reason.Should().Be("retry_exhausted");
        busy.Attempts.Should().Be(3);
        busy.PendingCount.Should().Be(0);
        busy.CurrentEpoch.Should().BeGreaterThan(busy.SnapshotEpoch);
    }

    [Fact]
    public async Task StorageAliases_PreserveCanonicalChildIdentityThroughReparentingAndDeletion()
    {
        var inner = new FileConversationStore(_directory, null, strictMetadataReads: true);
        const string child = "subagent-a_b";
        var alias = "subagent-a" + Path.GetInvalidFileNameChars()[0] + "b";
        await inner.SaveMetadataAsync(child, Child(child, "root"));
        await inner.SaveMetadataAsync("subagent-grandchild", Child("subagent-grandchild", child));
        var store = Create(inner);
        await store.InitializeAsync();
        await store.UpdateMetadataAsync(alias, row => row! with { LastUpdated = 1 });
        (await store.ReadChildrenAsync("root", true)).Should().HaveCount(2);
        await store.UpdateMetadataAsync(
            alias,
            row =>
                row! with
                {
                    Properties = row.Properties!.SetItem(SubAgentProvenance.ParentThreadIdKey, "other-root"),
                }
        );
        (await store.ReadChildrenAsync("other-root", true))
            .Select(n => n.ThreadId)
            .Should()
            .Equal(child, "subagent-grandchild");
        await store.DeleteThreadAsync(alias);
        (await store.ReadChildrenAsync("other-root", true)).Should().BeEmpty();
    }

    // Reads re-check every child against its metadata, so a stale relation only shows in the published index.
    private Dictionary<string, string> PublishedParents() =>
        JsonDocument
            .Parse(File.ReadAllText(Path.Combine(_directory, ".index.json")))
            .RootElement.GetProperty("parents")
            .EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString()!);

    [Fact]
    public async Task ChildThatLosesItsParent_IsDroppedFromThePublishedIndex()
    {
        var store = Create(new InMemoryConversationStore());
        await store.SaveMetadataAsync("subagent-a", Child("subagent-a", "root"));
        PublishedParents().Should().ContainKey("subagent-a");

        await store.SaveMetadataAsync("subagent-a", new ThreadMetadata { ThreadId = "subagent-a", LastUpdated = 1 });

        PublishedParents().Should().BeEmpty();
        (await store.ReadChildrenAsync("root", true)).Should().BeEmpty();
    }

    [Fact]
    public async Task RecordNamingAnotherThread_DropsTheRelationStoredUnderItsSlot()
    {
        var store = Create(new InMemoryConversationStore());
        await store.SaveMetadataAsync("subagent-x", Child("subagent-x", "root-x"));
        await store.SaveMetadataAsync("subagent-y", Child("subagent-y", "root-y"));

        // The record stored under x now names y, under y's own parent: x's old relation must go.
        await store.SaveMetadataAsync("subagent-x", Child("subagent-y", "root-y"));

        PublishedParents().Should().Equal(new Dictionary<string, string> { ["subagent-y"] = "root-y" });
    }

    [Fact]
    [Trait("Category", "Integration")] // Real atomic rename retries intentionally exceed the unit-test budget.
    public async Task FailedPublication_BlocksReadsAndRestartRepairsFromMetadata()
    {
        var inner = new InMemoryConversationStore();
        var store = Create(inner);
        await store.InitializeAsync();
        // This fixture owns the empty snapshot. Replace its target with a directory to force rename failure.
        File.Delete(Path.Combine(_directory, ".index.json"));
        Directory.CreateDirectory(Path.Combine(_directory, ".index.json"));
        // Renaming onto a directory is an IOException on Linux but UnauthorizedAccessException on Windows.
        var publication = await Record.ExceptionAsync(() =>
            store.SaveMetadataAsync("subagent-child", Child("subagent-child", "root"))
        );
        publication.Should().Match(e => e is IOException || e is UnauthorizedAccessException);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadChildrenAsync("root", true));
        Directory.Delete(Path.Combine(_directory, ".index.json"));
        await store.SaveMetadataAsync("unrelated", new ThreadMetadata { ThreadId = "unrelated", LastUpdated = 0 });
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadChildrenAsync("root", true));
        (await Create(inner).ReadChildrenAsync("root", true)).Should().ContainSingle();
    }

    [Fact]
    public async Task PendingMutation_BlocksDiscoveryAndFailedCallbackReconcilesBeforeRethrowing()
    {
        var inner = new FileConversationStore(_directory, null, strictMetadataReads: true);
        var store = Create(inner);
        await store.SaveMetadataAsync("subagent-child", Child("subagent-child", "root"));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var mutation = Task.Run(() =>
            store.UpdateMetadataAsync(
                "subagent-child",
                row =>
                {
                    entered.Set();
                    if (!release.Wait(TimeSpan.FromMinutes(2)))
                        throw new TimeoutException("The test did not release the metadata callback.");
                    return row! with
                    {
                        Properties = row.Properties!.SetItem(SubAgentProvenance.StatusKey, "completed"),
                    };
                }
            )
        );
        try
        {
            entered.Wait(TimeSpan.FromMinutes(2)).Should().BeTrue();
            var busy = await Assert.ThrowsAsync<IndexedConversationStore.HierarchyBusyException>(() =>
                store.ReadChildrenAsync("root", true)
            );
            busy.Reason.Should().Be("pending_publication");
            busy.PendingCount.Should().Be(1);
            busy.Attempts.Should().Be(1);
            busy.SnapshotEpoch.Should().Be(busy.CurrentEpoch);
            await using var pool = ConversationsControllerTests.CreatePool();
            var controller = ConversationsControllerTests.CreateController(
                store,
                pool,
                ConversationsControllerTests.ModeStoreResolvingSystemModes()
            );
            var response = Assert.IsType<Microsoft.AspNetCore.Mvc.ObjectResult>(
                await controller.ListSubAgents("root", recursive: true)
            );
            response.StatusCode.Should().Be(503);
            JsonSerializer.Serialize(response.Value).Should().Be("{\"code\":\"hierarchy_busy\"}");
        }
        finally
        {
            release.Set();
            await mutation;
        }
        (await store.ReadChildrenAsync("root", true)).Should().ContainSingle(n => n.Status == "completed");
        var expected = new InvalidOperationException("callback failed");
        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.UpdateMetadataAsync("subagent-child", _ => throw expected)
        );
        observed.Should().BeSameAs(expected);
        (await store.ReadChildrenAsync("root", true)).Should().ContainSingle(n => n.Status == "completed");
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    public async Task CorruptMetadata_CannotCertifyAnEmptyBackfill(string content)
    {
        var inner = new FileConversationStore(_directory, null, strictMetadataReads: true);
        var child = Path.Combine(_directory, "subagent-child");
        Directory.CreateDirectory(child);
        await File.WriteAllTextAsync(Path.Combine(child, "metadata.json"), content);
        await Assert.ThrowsAsync<JsonException>(() => Create(inner).ReadChildrenAsync("root", true));
        // Existing callers retain tolerant reads unless the strict option is explicitly enabled.
        (await new FileConversationStore(_directory).LoadMetadataAsync("subagent-child"))
            .Should()
            .BeNull();
    }
}
