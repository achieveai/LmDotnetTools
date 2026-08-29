using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Persistence;

/// <summary>
/// Tests for InMemoryConversationStore.
/// </summary>
public class InMemoryConversationStoreTests
{
    #region AppendMessagesAsync Tests

    [Fact]
    public async Task AppendMessagesAsync_AddsMessagesToNewThread()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        var messages = CreateTestMessages("thread-1", "run-1", 3);

        // Act
        await store.AppendMessagesAsync("thread-1", messages);

        // Assert
        store.GetMessageCount("thread-1").Should().Be(3);
    }

    [Fact]
    public async Task AppendMessagesAsync_AppendsToExistingThread()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        var batch1 = CreateTestMessages("thread-1", "run-1", 2);
        var batch2 = CreateTestMessages("thread-1", "run-2", 3);

        await store.AppendMessagesAsync("thread-1", batch1);

        // Act
        await store.AppendMessagesAsync("thread-1", batch2);

        // Assert
        store.GetMessageCount("thread-1").Should().Be(5);
    }

    [Fact]
    public async Task AppendMessagesAsync_WithEmptyList_DoesNothing()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        var messages = new List<PersistedMessage>();

        // Act
        await store.AppendMessagesAsync("thread-1", messages);

        // Assert
        store.GetMessageCount("thread-1").Should().Be(0);
    }

    [Fact]
    public async Task AppendMessagesAsync_ThrowsOnNullThreadId()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        var messages = CreateTestMessages("thread-1", "run-1", 1);

        // Act
        var act = async () => await store.AppendMessagesAsync(null!, messages);

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("threadId");
    }

    [Fact]
    public async Task AppendMessagesAsync_ThrowsOnNullMessages()
    {
        // Arrange
        var store = new InMemoryConversationStore();

        // Act
        var act = async () => await store.AppendMessagesAsync("thread-1", null!);

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("messages");
    }

    #endregion

    #region LoadMessagesAsync Tests

    [Fact]
    public async Task LoadMessagesAsync_ReturnsMessagesOrderedByTimestamp()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var messages = new List<PersistedMessage>
        {
            CreateMessage("thread-1", "run-1", "msg-3", now + 200),
            CreateMessage("thread-1", "run-1", "msg-1", now),
            CreateMessage("thread-1", "run-1", "msg-2", now + 100),
        };

        await store.AppendMessagesAsync("thread-1", messages);

        // Act
        var loaded = await store.LoadMessagesAsync("thread-1");

        // Assert
        loaded.Should().HaveCount(3);
        loaded[0].Id.Should().Be("msg-1");
        loaded[1].Id.Should().Be("msg-2");
        loaded[2].Id.Should().Be("msg-3");
    }

    [Fact]
    public async Task LoadMessagesAsync_ReturnsMessagesOrderedByMessageOrderIdxWithinSameTimestamp()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var messages = new List<PersistedMessage>
        {
            CreateMessage("thread-1", "run-1", "msg-3", now, messageOrderIdx: 2),
            CreateMessage("thread-1", "run-1", "msg-1", now, messageOrderIdx: 0),
            CreateMessage("thread-1", "run-1", "msg-2", now, messageOrderIdx: 1),
        };

        await store.AppendMessagesAsync("thread-1", messages);

        // Act
        var loaded = await store.LoadMessagesAsync("thread-1");

        // Assert
        loaded.Should().HaveCount(3);
        loaded[0].Id.Should().Be("msg-1");
        loaded[1].Id.Should().Be("msg-2");
        loaded[2].Id.Should().Be("msg-3");
    }

    [Fact]
    public async Task LoadMessagesAsync_ReturnsEmptyListForNonexistentThread()
    {
        // Arrange
        var store = new InMemoryConversationStore();

        // Act
        var loaded = await store.LoadMessagesAsync("nonexistent-thread");

        // Assert
        loaded.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadMessagesAsync_ReturnsCopyOfMessages()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        var messages = CreateTestMessages("thread-1", "run-1", 2);
        await store.AppendMessagesAsync("thread-1", messages);

        // Act
        var loaded1 = await store.LoadMessagesAsync("thread-1");
        var loaded2 = await store.LoadMessagesAsync("thread-1");

        // Assert
        loaded1.Should().NotBeSameAs(loaded2);
    }

    #endregion

    #region SaveMetadataAsync / LoadMetadataAsync Tests

    [Fact]
    public async Task SaveMetadataAsync_StoresMetadata()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        var metadata = CreateTestMetadata("thread-1");

        // Act
        await store.SaveMetadataAsync("thread-1", metadata);
        var loaded = await store.LoadMetadataAsync("thread-1");

        // Assert
        loaded.Should().NotBeNull();
        loaded!.ThreadId.Should().Be("thread-1");
    }

    [Fact]
    public async Task SaveMetadataAsync_OverwritesExistingMetadata()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        var metadata1 = CreateTestMetadata("thread-1", currentRunId: "run-1");
        var metadata2 = CreateTestMetadata("thread-1", currentRunId: "run-2");

        await store.SaveMetadataAsync("thread-1", metadata1);

        // Act
        await store.SaveMetadataAsync("thread-1", metadata2);
        var loaded = await store.LoadMetadataAsync("thread-1");

        // Assert
        loaded.Should().NotBeNull();
        loaded!.CurrentRunId.Should().Be("run-2");
    }

    [Fact]
    public async Task LoadMetadataAsync_ReturnsNullForNonexistentThread()
    {
        // Arrange
        var store = new InMemoryConversationStore();

        // Act
        var loaded = await store.LoadMetadataAsync("nonexistent-thread");

        // Assert
        loaded.Should().BeNull();
    }

    #endregion

    #region DeleteThreadAsync Tests

    [Fact]
    public async Task DeleteThreadAsync_RemovesMessagesAndMetadata()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        var messages = CreateTestMessages("thread-1", "run-1", 3);
        var metadata = CreateTestMetadata("thread-1");

        await store.AppendMessagesAsync("thread-1", messages);
        await store.SaveMetadataAsync("thread-1", metadata);

        // Act
        await store.DeleteThreadAsync("thread-1");

        // Assert
        store.GetMessageCount("thread-1").Should().Be(0);
        (await store.LoadMetadataAsync("thread-1")).Should().BeNull();
    }

    [Fact]
    public async Task DeleteThreadAsync_DoesNotThrowForNonexistentThread()
    {
        // Arrange
        var store = new InMemoryConversationStore();

        // Act
        var act = async () => await store.DeleteThreadAsync("nonexistent-thread");

        // Assert
        await act.Should().NotThrowAsync();
    }

    #endregion

    #region Thread Safety Tests

    [Fact]
    public async Task Store_IsThreadSafe_ForConcurrentAppends()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        var tasks = new List<Task>();

        // Act - Append messages from multiple threads
        for (var i = 0; i < 10; i++)
        {
            var runId = $"run-{i}";
            tasks.Add(
                Task.Run(async () =>
                {
                    var messages = CreateTestMessages("thread-1", runId, 10);
                    await store.AppendMessagesAsync("thread-1", messages);
                })
            );
        }

        await Task.WhenAll(tasks);

        // Assert
        store.GetMessageCount("thread-1").Should().Be(100);
    }

    #endregion

    #region Helper Methods

    [Fact]
    public async Task GetAllThreadIds_ReturnsAllThreadIds()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        await store.AppendMessagesAsync("thread-1", CreateTestMessages("thread-1", "run-1", 1));
        await store.AppendMessagesAsync("thread-2", CreateTestMessages("thread-2", "run-1", 1));
        await store.SaveMetadataAsync("thread-3", CreateTestMetadata("thread-3"));

        // Act
        var threadIds = store.GetAllThreadIds();

        // Assert
        threadIds.Should().Contain("thread-1");
        threadIds.Should().Contain("thread-2");
        threadIds.Should().Contain("thread-3");
    }

    [Fact]
    public async Task Clear_RemovesAllData()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        await store.AppendMessagesAsync("thread-1", CreateTestMessages("thread-1", "run-1", 1));
        await store.SaveMetadataAsync("thread-1", CreateTestMetadata("thread-1"));

        // Act
        store.Clear();

        // Assert
        store.GetMessageCount("thread-1").Should().Be(0);
        store.GetAllThreadIds().Should().BeEmpty();
    }

    #endregion

    #region ListThreadsAsync Tests

    [Fact]
    public async Task ListThreadsAsync_ReturnsEmptyWhenNoThreads()
    {
        // Arrange
        var store = new InMemoryConversationStore();

        // Act
        var result = await store.ListThreadsAsync();

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ListThreadsAsync_ReturnsAllThreadsWithMetadata()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        await store.SaveMetadataAsync("thread-1", CreateTestMetadata("thread-1"));
        await store.SaveMetadataAsync("thread-2", CreateTestMetadata("thread-2"));
        await store.SaveMetadataAsync("thread-3", CreateTestMetadata("thread-3"));

        // Act
        var result = await store.ListThreadsAsync();

        // Assert
        result.Should().HaveCount(3);
        result.Select(m => m.ThreadId).Should().Contain(["thread-1", "thread-2", "thread-3"]);
    }

    [Fact]
    public async Task ListThreadsAsync_ReturnsSortedByLastUpdatedDescending()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await store.SaveMetadataAsync(
            "thread-oldest",
            new ThreadMetadata { ThreadId = "thread-oldest", LastUpdated = now - 2000 }
        );
        await store.SaveMetadataAsync(
            "thread-newest",
            new ThreadMetadata { ThreadId = "thread-newest", LastUpdated = now }
        );
        await store.SaveMetadataAsync(
            "thread-middle",
            new ThreadMetadata { ThreadId = "thread-middle", LastUpdated = now - 1000 }
        );

        // Act
        var result = await store.ListThreadsAsync();

        // Assert
        result.Should().HaveCount(3);
        result[0].ThreadId.Should().Be("thread-newest");
        result[1].ThreadId.Should().Be("thread-middle");
        result[2].ThreadId.Should().Be("thread-oldest");
    }

    [Fact]
    public async Task ListThreadsAsync_RespectsLimitParameter()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var i = 0; i < 5; i++)
        {
            await store.SaveMetadataAsync(
                $"thread-{i}",
                new ThreadMetadata
                {
                    ThreadId = $"thread-{i}",
                    LastUpdated = now - (i * 1000), // thread-0 is newest
                }
            );
        }

        // Act
        var result = await store.ListThreadsAsync(limit: 3);

        // Assert
        result.Should().HaveCount(3);
        result[0].ThreadId.Should().Be("thread-0"); // newest
        result[1].ThreadId.Should().Be("thread-1");
        result[2].ThreadId.Should().Be("thread-2");
    }

    [Fact]
    public async Task ListThreadsAsync_RespectsOffsetParameter()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var i = 0; i < 5; i++)
        {
            await store.SaveMetadataAsync(
                $"thread-{i}",
                new ThreadMetadata
                {
                    ThreadId = $"thread-{i}",
                    LastUpdated = now - (i * 1000), // thread-0 is newest
                }
            );
        }

        // Act
        var result = await store.ListThreadsAsync(limit: 2, offset: 2);

        // Assert
        result.Should().HaveCount(2);
        result[0].ThreadId.Should().Be("thread-2");
        result[1].ThreadId.Should().Be("thread-3");
    }

    [Fact]
    public async Task ListThreadsAsync_PreservesPropertiesInMetadata()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        var metadata = new ThreadMetadata
        {
            ThreadId = "thread-with-props",
            LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Properties = new Dictionary<string, object>
            {
                ["title"] = "My Conversation Title",
                ["preview"] = "First message preview...",
            }.ToImmutableDictionary(),
        };
        await store.SaveMetadataAsync("thread-with-props", metadata);

        // Act
        var result = await store.ListThreadsAsync();

        // Assert
        result.Should().HaveCount(1);
        result[0].Properties.Should().NotBeNull();
        result[0].Properties!["title"].ToString().Should().Be("My Conversation Title");
        result[0].Properties!["preview"].ToString().Should().Be("First message preview...");
    }

    [Fact]
    public async Task ListThreadsAsync_CreatesMinimalMetadataForThreadsWithoutExplicitMetadata()
    {
        // Arrange
        var store = new InMemoryConversationStore();

        // Add messages to a thread without explicit metadata
        await store.AppendMessagesAsync("thread-no-metadata", CreateTestMessages("thread-no-metadata", "run-1", 1));

        // Add a thread with metadata
        await store.SaveMetadataAsync("thread-with-metadata", CreateTestMetadata("thread-with-metadata"));

        // Act
        var result = await store.ListThreadsAsync();

        // Assert - Should include both threads (one with full metadata, one with minimal)
        result.Should().HaveCount(2);
        result.Select(m => m.ThreadId).Should().Contain("thread-with-metadata");
        result.Select(m => m.ThreadId).Should().Contain("thread-no-metadata");
    }

    #endregion

    #region ListThreadsAsync Options Tests

    /// <summary>
    ///     A page stays FULL when the excluded rows dominate the ordering - the exclusion runs before
    ///     <c>Skip</c>/<c>Take</c>, not after it.
    /// </summary>
    /// <remarks>
    ///     This is the store-level shape of a production failure. <c>LastUpdated</c> is bumped on every
    ///     completed run and background agent runs are constant, so agent-owned rows crowd the front of
    ///     a last-updated ordering; on a live deployment of 302 threads, 256 of them agent-owned, a
    ///     caller that trimmed the page first and filtered second got five usable rows out of fifty and
    ///     lost every older conversation. The seed here reproduces that shape deliberately: EVERY
    ///     excluded row is newer than EVERY kept row, so an implementation that filters after paging
    ///     returns nothing at all.
    /// </remarks>
    [Fact]
    public async Task ListThreadsAsync_ReturnsAFullPage_WhenExcludedThreadsOutnumberTheLimit()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        for (var i = 0; i < 20; i++)
        {
            await store.SaveMetadataAsync(
                $"subagent-{i:D2}",
                new ThreadMetadata { ThreadId = $"subagent-{i:D2}", LastUpdated = 10_000 + i }
            );
        }

        for (var i = 0; i < 15; i++)
        {
            await store.SaveMetadataAsync(
                $"keep-{i:D2}",
                new ThreadMetadata { ThreadId = $"keep-{i:D2}", LastUpdated = 1_000 + i }
            );
        }

        var options = new ConversationListOptions { ExcludedThreadIdPrefixes = ["subagent-"] };

        // Act
        var result = await store.ListThreadsAsync(limit: 10, offset: 0, options: options);

        // Assert
        result.Should().HaveCount(10);
        result.Should().OnlyContain(m => m.ThreadId.StartsWith("keep-", StringComparison.Ordinal));
    }

    /// <summary>
    ///     Offset paging over an excluded set is contiguous: no row appears twice and none is skipped.
    /// </summary>
    /// <remarks>
    ///     The seed INTERLEAVES excluded and kept rows in the last-updated ordering, which is the case
    ///     that separates "filtered before the page" from "filtered after it". Filtering afterwards
    ///     would still return rows here - just roughly half a page of them each time, and a different
    ///     half depending on where the offset happened to land. Asserting only that page 1 is non-empty
    ///     would pass against that; asserting the concatenation of all three pages equals the full kept
    ///     set, in order, is what does not.
    /// </remarks>
    [Fact]
    public async Task ListThreadsAsync_PagesWithoutOverlapOrGaps_WhenAnExclusionIsActive()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        for (var i = 0; i < 12; i++)
        {
            await store.SaveMetadataAsync(
                $"keep-{i:D2}",
                new ThreadMetadata { ThreadId = $"keep-{i:D2}", LastUpdated = 10_000 - (i * 2) }
            );
            await store.SaveMetadataAsync(
                $"subagent-{i:D2}",
                new ThreadMetadata { ThreadId = $"subagent-{i:D2}", LastUpdated = 10_000 - (i * 2) - 1 }
            );
        }

        var options = new ConversationListOptions { ExcludedThreadIdPrefixes = ["subagent-"] };

        // Act
        var page1 = await store.ListThreadsAsync(limit: 5, offset: 0, options: options);
        var page2 = await store.ListThreadsAsync(limit: 5, offset: 5, options: options);
        var page3 = await store.ListThreadsAsync(limit: 5, offset: 10, options: options);

        // Assert
        page1.Should().HaveCount(5);
        page2.Should().HaveCount(5);
        page3.Should().HaveCount(2);

        var paged = page1.Concat(page2).Concat(page3).Select(m => m.ThreadId).ToList();
        paged.Should().OnlyHaveUniqueItems();
        paged.Should().Equal(Enumerable.Range(0, 12).Select(i => $"keep-{i:D2}"));
    }

    /// <summary>
    ///     <see cref="ConversationSortOrder.Created"/> orders by the creation time derived from the
    ///     thread id, NOT by <c>LastUpdated</c>.
    /// </summary>
    /// <remarks>
    ///     The two orderings are seeded to DISAGREE completely - the earliest-created thread carries the
    ///     newest <c>LastUpdated</c>, so the expected sequences are exact reverses of one another. A
    ///     seed in which they agreed could not distinguish the two implementations at all, and would
    ///     pass against a <c>Created</c> branch that quietly did nothing.
    /// </remarks>
    [Fact]
    public async Task ListThreadsAsync_OrdersByDerivedCreationTime_WhenSortOrderIsCreated()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        await store.SaveMetadataAsync(
            "thread-1000-aaa",
            new ThreadMetadata { ThreadId = "thread-1000-aaa", LastUpdated = 9_000 }
        );
        await store.SaveMetadataAsync(
            "thread-2000-bbb",
            new ThreadMetadata { ThreadId = "thread-2000-bbb", LastUpdated = 8_000 }
        );
        await store.SaveMetadataAsync(
            "thread-3000-ccc",
            new ThreadMetadata { ThreadId = "thread-3000-ccc", LastUpdated = 7_000 }
        );

        // Act
        var byLastUsed = await store.ListThreadsAsync(
            limit: 10,
            options: new ConversationListOptions { SortOrder = ConversationSortOrder.LastUsed }
        );
        var byCreated = await store.ListThreadsAsync(
            limit: 10,
            options: new ConversationListOptions { SortOrder = ConversationSortOrder.Created }
        );

        // Assert
        byLastUsed.Select(m => m.ThreadId).Should().Equal("thread-1000-aaa", "thread-2000-bbb", "thread-3000-ccc");
        byCreated.Select(m => m.ThreadId).Should().Equal("thread-3000-ccc", "thread-2000-bbb", "thread-1000-aaa");
    }

    /// <summary>
    ///     A thread id that carries no timestamp segment sorts by <c>LastUpdated</c> under
    ///     <see cref="ConversationSortOrder.Created"/> - it is neither dropped nor sorted to position
    ///     zero.
    /// </summary>
    /// <remarks>
    ///     There are two ways an id can fail to carry one, and both are seeded, because a test that
    ///     covered only one leaves the other branch free to return anything at all. Conversations
    ///     provisioned before the server minted a timestamp segment carry <c>thread-{guid:N}</c>,
    ///     which has no <c>-</c> after the prefix, so the
    ///     scan for a delimiter finds nothing; an id shaped like <c>thread-notatimestamp-zzz</c> has the
    ///     delimiter but a segment that does not parse. Losing either row from the listing would be the
    ///     very defect this change exists to fix, so the fallback keeps both - positioned by the one
    ///     timestamp that does exist. The seed places both fallback values ABOVE both parseable rows, so
    ///     an implementation that treated an unparseable id as zero would order them last and fail here.
    /// </remarks>
    [Fact]
    public async Task ListThreadsAsync_FallsBackToLastUpdated_WhenTheThreadIdCarriesNoTimestamp()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        var provisioned = $"thread-{Guid.NewGuid():N}";
        const string NonNumeric = "thread-notatimestamp-zzz";
        await store.SaveMetadataAsync(
            "thread-5000-aaa",
            new ThreadMetadata { ThreadId = "thread-5000-aaa", LastUpdated = 1_000 }
        );
        await store.SaveMetadataAsync(provisioned, new ThreadMetadata { ThreadId = provisioned, LastUpdated = 7_000 });
        await store.SaveMetadataAsync(NonNumeric, new ThreadMetadata { ThreadId = NonNumeric, LastUpdated = 6_500 });
        await store.SaveMetadataAsync(
            "thread-6000-bbb",
            new ThreadMetadata { ThreadId = "thread-6000-bbb", LastUpdated = 2_000 }
        );

        // Act
        var byCreated = await store.ListThreadsAsync(
            limit: 10,
            options: new ConversationListOptions { SortOrder = ConversationSortOrder.Created }
        );

        // Assert - both fallbacks (7,000 and 6,500) rank above the parsed 6,000 and 5,000.
        byCreated
            .Select(m => m.ThreadId)
            .Should()
            .Equal(provisioned, NonNumeric, "thread-6000-bbb", "thread-5000-aaa");
    }

    /// <summary>
    ///     A null <c>options</c> is exactly today's behavior: nothing excluded, last-used order.
    /// </summary>
    /// <remarks>
    ///     Every pre-existing caller passes nothing, so this is the compatibility pin: the fix must not
    ///     change what a caller that did not ask for it receives. The agent-owned row is seeded NEWEST
    ///     on purpose - it has to come back FIRST, because a store that quietly applied a default
    ///     exclusion would look correct on every other assertion in this file.
    /// </remarks>
    [Fact]
    public async Task ListThreadsAsync_ExcludesNothingAndOrdersByLastUsed_WhenOptionsIsNull()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        await store.SaveMetadataAsync(
            "subagent-newest",
            new ThreadMetadata { ThreadId = "subagent-newest", LastUpdated = 9_000 }
        );
        await store.SaveMetadataAsync(
            "thread-1000-aaa",
            new ThreadMetadata { ThreadId = "thread-1000-aaa", LastUpdated = 8_000 }
        );
        await store.SaveMetadataAsync(
            "workflow-older",
            new ThreadMetadata { ThreadId = "workflow-older", LastUpdated = 7_000 }
        );

        // Act
        var withNull = await store.ListThreadsAsync(limit: 10, offset: 0, options: null);
        var withDefault = await store.ListThreadsAsync(limit: 10, offset: 0, options: ConversationListOptions.Default);

        // Assert
        withNull.Select(m => m.ThreadId).Should().Equal("subagent-newest", "thread-1000-aaa", "workflow-older");
        withDefault.Select(m => m.ThreadId).Should().Equal(withNull.Select(m => m.ThreadId));
    }

    // Rows are saved in an order that is deliberately NOT the expected one, because LINQ's sort is
    // stable: with no tie-break the result would simply echo whatever order the backing
    // ConcurrentDictionary enumerated, and the assertion below would be describing insertion order
    // rather than a defined ordering.
    [Fact]
    public async Task ListThreadsAsync_BreaksLastUsedTiesByThreadIdDescending()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        foreach (var suffix in new[] { "bbb", "eee", "aaa", "fff", "ccc", "ddd" })
        {
            await store.SaveMetadataAsync(
                $"thread-5000-{suffix}",
                new ThreadMetadata { ThreadId = $"thread-5000-{suffix}", LastUpdated = 5_000 }
            );
        }

        // Act
        var listed = await store.ListThreadsAsync(limit: 10, offset: 0);

        // Assert
        listed
            .Select(m => m.ThreadId)
            .Should()
            .Equal(
                "thread-5000-fff",
                "thread-5000-eee",
                "thread-5000-ddd",
                "thread-5000-ccc",
                "thread-5000-bbb",
                "thread-5000-aaa"
            );
    }

    // NOTE: this test does NOT prove the tie-break - it stays green without it. A single test run
    // enumerates the store consistently, so the pages line up whether or not tied rows have a
    // defined order; the instability the tie-break prevents needs the enumeration order to CHANGE
    // between two page requests, which no deterministic test can force. What this guards is the
    // paging arithmetic over a tied set: Skip/Take counts right, nothing double-counted, nothing
    // dropped. The claim that ties have a defined order at all is pinned by the
    // ...BreaksLastUsedTiesByThreadIdDescending test above, which does go red without the
    // tie-break - and a total order is what makes this consistency hold across requests too.
    [Fact]
    public async Task ListThreadsAsync_PagesTiedRowsWithoutOverlapOrGaps()
    {
        // Arrange
        var store = new InMemoryConversationStore();
        var expected = new List<string>();
        foreach (var suffix in new[] { "bbb", "eee", "aaa", "fff", "ccc", "ddd" })
        {
            var threadId = $"thread-5000-{suffix}";
            expected.Add(threadId);
            await store.SaveMetadataAsync(threadId, new ThreadMetadata { ThreadId = threadId, LastUpdated = 5_000 });
        }

        // Act - page the tied set two at a time, exactly as the sidebar does
        var paged = new List<string>();
        for (var offset = 0; offset < expected.Count; offset += 2)
        {
            var page = await store.ListThreadsAsync(limit: 2, offset: offset);
            paged.AddRange(page.Select(m => m.ThreadId));
        }

        // Assert
        paged.Should().OnlyHaveUniqueItems("a tied row must not be returned by two different pages");
        paged.Should().BeEquivalentTo(expected, "no tied row may be skipped by offset paging");
    }

    #endregion

    #region ReplaceMessageAsync Tests

    [Fact]
    public async Task ReplaceMessageAsync_ReplacesInPlace_AndPreservesTimestamp()
    {
        var store = new InMemoryConversationStore();
        var original = CreateMessage("thread-1", "run-1", "msg-id", 1_000_000, messageOrderIdx: 0);
        await store.AppendMessagesAsync("thread-1", [original]);

        var replacement = original with
        {
            MessageJson = "{\"text\":\"replaced\"}",
            Timestamp = 9_999_999, // store should ignore this and keep the original timestamp
        };
        await store.ReplaceMessageAsync("thread-1", replacement);

        var loaded = await store.LoadMessagesAsync("thread-1");
        loaded.Should().ContainSingle();
        loaded[0].Id.Should().Be("msg-id");
        loaded[0].MessageJson.Should().Be("{\"text\":\"replaced\"}");
        loaded[0].Timestamp.Should().Be(1_000_000, "ReplaceMessageAsync must preserve the original timestamp");
    }

    [Fact]
    public async Task ReplaceMessageAsync_Throws_WhenMessageIdNotFound()
    {
        var store = new InMemoryConversationStore();
        await store.AppendMessagesAsync("thread-1", [CreateMessage("thread-1", "run-1", "existing", 1, 0)]);

        var phantom = CreateMessage("thread-1", "run-1", "does-not-exist", 1, 0);

        var act = () => store.ReplaceMessageAsync("thread-1", phantom);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*does-not-exist*");
    }

    [Fact]
    public async Task ReplaceMessageAsync_Throws_WhenThreadNotFound()
    {
        var store = new InMemoryConversationStore();
        var msg = CreateMessage("thread-1", "run-1", "id", 1, 0);

        var act = () => store.ReplaceMessageAsync("never-created-thread", msg);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*never-created-thread*");
    }

    #endregion

    #region Test Helpers

    private static List<PersistedMessage> CreateTestMessages(string threadId, string runId, int count)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return
        [
            .. Enumerable
                .Range(0, count)
                .Select(i => CreateMessage(threadId, runId, $"msg-{runId}-{i}", now + i, messageOrderIdx: i)),
        ];
    }

    private static PersistedMessage CreateMessage(
        string threadId,
        string runId,
        string id,
        long timestamp,
        int? messageOrderIdx = null
    )
    {
        return new PersistedMessage
        {
            Id = id,
            ThreadId = threadId,
            RunId = runId,
            Timestamp = timestamp,
            MessageOrderIdx = messageOrderIdx,
            MessageType = "TextMessage",
            Role = "user",
            MessageJson = $"{{\"text\": \"Test message {id}\"}}",
        };
    }

    private static ThreadMetadata CreateTestMetadata(string threadId, string? currentRunId = null)
    {
        return new ThreadMetadata
        {
            ThreadId = threadId,
            CurrentRunId = currentRunId,
            LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
    }

    #endregion
}
