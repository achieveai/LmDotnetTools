using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmTestUtils.Persistence;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Persistence;

/// <summary>
/// Tests for FileConversationStore.
/// </summary>
public class FileConversationStoreTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly FileConversationStore _store;

    public FileConversationStoreTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"FileConversationStoreTests_{Guid.NewGuid()}");
        _store = new FileConversationStore(_testDirectory);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        // #477: detach-then-delete rather than recursive-delete in place — see DetachedStoreTeardown.
        DetachedStoreTeardown.Purge(_testDirectory);
    }

    #region Constructor Tests

    [Fact]
    public void Constructor_CreatesBaseDirectory()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), $"FileStoreTest_{Guid.NewGuid()}");

        // Act
        _ = new FileConversationStore(tempDir);

        // Assert
        Directory.Exists(tempDir).Should().BeTrue();

        // #477: detach-then-delete rather than recursive-delete in place — see DetachedStoreTeardown.
        // Deliberately NOT in a finally: Purge throws when it cannot detach, and a throw from a finally
        // REPLACES the assertion failure that is unwinding through it. A leaked temp directory is a far
        // cheaper outcome than losing the reason the test failed.
        DetachedStoreTeardown.Purge(tempDir);
    }

    [Fact]
    public void Constructor_ThrowsOnNullDirectory()
    {
        // Act
        var act = () => new FileConversationStore(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region AppendMessagesAsync Tests

    [Fact]
    public async Task AppendMessagesAsync_CreatesThreadDirectory()
    {
        // Arrange
        var messages = CreateTestMessages("thread-1", "run-1", 2);

        // Act
        await _store.AppendMessagesAsync("thread-1", messages);

        // Assert
        var threadDir = Path.Combine(_testDirectory, "thread-1");
        Directory.Exists(threadDir).Should().BeTrue();
    }

    [Fact]
    public async Task AppendMessagesAsync_CreatesMessagesFile()
    {
        // Arrange
        var messages = CreateTestMessages("thread-1", "run-1", 2);

        // Act
        await _store.AppendMessagesAsync("thread-1", messages);

        // Assert
        var messagesFile = Path.Combine(_testDirectory, "thread-1", "messages.json");
        File.Exists(messagesFile).Should().BeTrue();
    }

    [Fact]
    public async Task AppendMessagesAsync_AppendsToExistingMessages()
    {
        // Arrange
        var batch1 = CreateTestMessages("thread-1", "run-1", 2);
        var batch2 = CreateTestMessages("thread-1", "run-2", 3);

        await _store.AppendMessagesAsync("thread-1", batch1);

        // Act
        await _store.AppendMessagesAsync("thread-1", batch2);

        // Assert
        var loaded = await _store.LoadMessagesAsync("thread-1");
        loaded.Should().HaveCount(5);
    }

    [Fact]
    public async Task AppendMessagesAsync_WithEmptyList_DoesNotCreateFile()
    {
        // Arrange
        var messages = new List<PersistedMessage>();

        // Act
        await _store.AppendMessagesAsync("thread-1", messages);

        // Assert
        var threadDir = Path.Combine(_testDirectory, "thread-1");
        Directory.Exists(threadDir).Should().BeFalse();
    }

    [Fact]
    public async Task AppendMessagesAsync_SanitizesThreadId()
    {
        // Arrange
        var threadIdWithInvalidChars = "thread:with<invalid>chars";
        var messages = CreateTestMessages(threadIdWithInvalidChars, "run-1", 1);

        // Act
        await _store.AppendMessagesAsync(threadIdWithInvalidChars, messages);

        // Assert - Should not throw and should create a sanitized directory
        var loaded = await _store.LoadMessagesAsync(threadIdWithInvalidChars);
        loaded.Should().HaveCount(1);
    }

    #endregion

    #region LoadMessagesAsync Tests

    [Fact]
    public async Task LoadMessagesAsync_ReturnsMessagesOrderedByTimestamp()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var messages = new List<PersistedMessage>
        {
            CreateMessage("thread-1", "run-1", "msg-3", now + 200),
            CreateMessage("thread-1", "run-1", "msg-1", now),
            CreateMessage("thread-1", "run-1", "msg-2", now + 100),
        };

        await _store.AppendMessagesAsync("thread-1", messages);

        // Act
        var loaded = await _store.LoadMessagesAsync("thread-1");

        // Assert
        loaded.Should().HaveCount(3);
        loaded[0].Id.Should().Be("msg-1");
        loaded[1].Id.Should().Be("msg-2");
        loaded[2].Id.Should().Be("msg-3");
    }

    [Fact]
    public async Task LoadMessagesAsync_ReturnsEmptyListForNonexistentThread()
    {
        // Act
        var loaded = await _store.LoadMessagesAsync("nonexistent-thread");

        // Assert
        loaded.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadMessagesAsync_HandlesCorruptedFile()
    {
        // Arrange
        var threadDir = Path.Combine(_testDirectory, "corrupted-thread");
        Directory.CreateDirectory(threadDir);
        await File.WriteAllTextAsync(Path.Combine(threadDir, "messages.json"), "invalid json content");

        // Act
        var loaded = await _store.LoadMessagesAsync("corrupted-thread");

        // Assert - Should return empty list instead of throwing
        loaded.Should().BeEmpty();
    }

    #endregion

    #region SaveMetadataAsync / LoadMetadataAsync Tests

    [Fact]
    public async Task SaveMetadataAsync_CreatesMetadataFile()
    {
        // Arrange
        var metadata = CreateTestMetadata("thread-1");

        // Act
        await _store.SaveMetadataAsync("thread-1", metadata);

        // Assert
        var metadataFile = Path.Combine(_testDirectory, "thread-1", "metadata.json");
        File.Exists(metadataFile).Should().BeTrue();
    }

    [Fact]
    public async Task SaveMetadataAsync_RoundTripsMetadata()
    {
        // Arrange
        var metadata = new ThreadMetadata
        {
            ThreadId = "thread-1",
            CurrentRunId = "run-1",
            LatestRunId = "run-2",
            LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SessionMappings = new Dictionary<string, string>
            {
                ["session-1"] = "external-id-1",
                ["session-2"] = "external-id-2",
            },
        };

        // Act
        await _store.SaveMetadataAsync("thread-1", metadata);
        var loaded = await _store.LoadMetadataAsync("thread-1");

        // Assert
        loaded.Should().NotBeNull();
        loaded!.ThreadId.Should().Be("thread-1");
        loaded.CurrentRunId.Should().Be("run-1");
        loaded.LatestRunId.Should().Be("run-2");
        loaded.SessionMappings.Should().ContainKey("session-1");
        loaded.SessionMappings!["session-1"].Should().Be("external-id-1");
    }

    [Fact]
    public async Task LoadMetadataAsync_ReturnsNullForNonexistentThread()
    {
        // Act
        var loaded = await _store.LoadMetadataAsync("nonexistent-thread");

        // Assert
        loaded.Should().BeNull();
    }

    [Fact]
    public async Task LoadMetadataAsync_HandlesCorruptedFile()
    {
        // Arrange
        var threadDir = Path.Combine(_testDirectory, "corrupted-metadata");
        Directory.CreateDirectory(threadDir);
        await File.WriteAllTextAsync(Path.Combine(threadDir, "metadata.json"), "invalid json");

        // Act
        var loaded = await _store.LoadMetadataAsync("corrupted-metadata");

        // Assert - Should return null instead of throwing
        loaded.Should().BeNull();
    }

    #endregion

    #region DeleteThreadAsync Tests

    [Fact]
    public async Task DeleteThreadAsync_RemovesThreadDirectory()
    {
        // Arrange
        var messages = CreateTestMessages("thread-1", "run-1", 2);
        var metadata = CreateTestMetadata("thread-1");

        await _store.AppendMessagesAsync("thread-1", messages);
        await _store.SaveMetadataAsync("thread-1", metadata);

        var threadDir = Path.Combine(_testDirectory, "thread-1");
        Directory.Exists(threadDir).Should().BeTrue();

        // Act
        await _store.DeleteThreadAsync("thread-1");

        // Assert
        Directory.Exists(threadDir).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteThreadAsync_DoesNotThrowForNonexistentThread()
    {
        // Act
        var act = async () => await _store.DeleteThreadAsync("nonexistent-thread");

        // Assert
        await act.Should().NotThrowAsync();
    }

    #endregion

    #region Atomic Write Tests

    [Fact]
    public async Task AppendMessagesAsync_WritesAtomically()
    {
        // Arrange
        var messages = CreateTestMessages("thread-1", "run-1", 5);

        // Act
        await _store.AppendMessagesAsync("thread-1", messages);

        // Assert - No temp file should remain
        var threadDir = Path.Combine(_testDirectory, "thread-1");
        var tempFiles = Directory.GetFiles(threadDir, "*.tmp");
        tempFiles.Should().BeEmpty();
    }

    #endregion

    #region ListThreadsAsync Tests

    [Fact]
    public async Task ListThreadsAsync_ReturnsEmptyWhenNoThreads()
    {
        // Act
        var result = await _store.ListThreadsAsync();

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ListThreadsAsync_ReturnsAllThreads()
    {
        // Arrange
        await _store.SaveMetadataAsync("thread-1", CreateTestMetadata("thread-1"));
        await _store.SaveMetadataAsync("thread-2", CreateTestMetadata("thread-2"));
        await _store.SaveMetadataAsync("thread-3", CreateTestMetadata("thread-3"));

        // Act
        var result = await _store.ListThreadsAsync();

        // Assert
        result.Should().HaveCount(3);
        result.Select(m => m.ThreadId).Should().Contain(["thread-1", "thread-2", "thread-3"]);
    }

    [Fact]
    public async Task ListThreadsAsync_ReturnsSortedByLastUpdatedDescending()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await _store.SaveMetadataAsync("thread-oldest", new ThreadMetadata
        {
            ThreadId = "thread-oldest",
            LastUpdated = now - 2000,
        });
        await _store.SaveMetadataAsync("thread-newest", new ThreadMetadata
        {
            ThreadId = "thread-newest",
            LastUpdated = now,
        });
        await _store.SaveMetadataAsync("thread-middle", new ThreadMetadata
        {
            ThreadId = "thread-middle",
            LastUpdated = now - 1000,
        });

        // Act
        var result = await _store.ListThreadsAsync();

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
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var i = 0; i < 5; i++)
        {
            await _store.SaveMetadataAsync($"thread-{i}", new ThreadMetadata
            {
                ThreadId = $"thread-{i}",
                LastUpdated = now - (i * 1000), // thread-0 is newest
            });
        }

        // Act
        var result = await _store.ListThreadsAsync(limit: 3);

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
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var i = 0; i < 5; i++)
        {
            await _store.SaveMetadataAsync($"thread-{i}", new ThreadMetadata
            {
                ThreadId = $"thread-{i}",
                LastUpdated = now - (i * 1000), // thread-0 is newest
            });
        }

        // Act
        var result = await _store.ListThreadsAsync(limit: 2, offset: 2);

        // Assert
        result.Should().HaveCount(2);
        result[0].ThreadId.Should().Be("thread-2");
        result[1].ThreadId.Should().Be("thread-3");
    }

    [Fact]
    public async Task ListThreadsAsync_PreservesPropertiesInMetadata()
    {
        // Arrange
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
        await _store.SaveMetadataAsync("thread-with-props", metadata);

        // Act
        var result = await _store.ListThreadsAsync();

        // Assert
        result.Should().HaveCount(1);
        result[0].Properties.Should().NotBeNull();
        result[0].Properties!["title"].ToString().Should().Be("My Conversation Title");
        result[0].Properties!["preview"].ToString().Should().Be("First message preview...");
    }

    [Fact]
    public async Task ListThreadsAsync_CreatesMinimalMetadataForDirectoriesWithoutMetadataFile()
    {
        // Arrange - Create a directory without metadata.json
        var emptyThreadDir = Path.Combine(_testDirectory, "thread-no-metadata");
        Directory.CreateDirectory(emptyThreadDir);

        await _store.SaveMetadataAsync("thread-with-metadata", CreateTestMetadata("thread-with-metadata"));

        // Act
        var result = await _store.ListThreadsAsync();

        // Assert - Should include both threads (one with full metadata, one with minimal)
        result.Should().HaveCount(2);
        result.Select(m => m.ThreadId).Should().Contain("thread-with-metadata");
        result.Select(m => m.ThreadId).Should().Contain("thread-no-metadata");
    }

    [Fact]
    public async Task ListThreadsAsync_CreatesMinimalMetadataForCorruptedMetadataFiles()
    {
        // Arrange
        await _store.SaveMetadataAsync("thread-valid", CreateTestMetadata("thread-valid"));

        var corruptedDir = Path.Combine(_testDirectory, "thread-corrupted");
        Directory.CreateDirectory(corruptedDir);
        await File.WriteAllTextAsync(Path.Combine(corruptedDir, "metadata.json"), "invalid json");

        // Act
        var result = await _store.ListThreadsAsync();

        // Assert - Should include both threads (corrupted gets minimal metadata)
        result.Should().HaveCount(2);
        result.Select(m => m.ThreadId).Should().Contain("thread-valid");
        result.Select(m => m.ThreadId).Should().Contain("thread-corrupted");
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
        for (var i = 0; i < 20; i++)
        {
            await _store.SaveMetadataAsync($"subagent-{i:D2}", new ThreadMetadata
            {
                ThreadId = $"subagent-{i:D2}",
                LastUpdated = 10_000 + i,
            });
        }

        for (var i = 0; i < 15; i++)
        {
            await _store.SaveMetadataAsync($"keep-{i:D2}", new ThreadMetadata
            {
                ThreadId = $"keep-{i:D2}",
                LastUpdated = 1_000 + i,
            });
        }

        var options = new ConversationListOptions { ExcludedThreadIdPrefixes = ["subagent-"] };

        // Act
        var result = await _store.ListThreadsAsync(limit: 10, offset: 0, options: options);

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
        for (var i = 0; i < 12; i++)
        {
            await _store.SaveMetadataAsync($"keep-{i:D2}", new ThreadMetadata
            {
                ThreadId = $"keep-{i:D2}",
                LastUpdated = 10_000 - (i * 2),
            });
            await _store.SaveMetadataAsync($"subagent-{i:D2}", new ThreadMetadata
            {
                ThreadId = $"subagent-{i:D2}",
                LastUpdated = 10_000 - (i * 2) - 1,
            });
        }

        var options = new ConversationListOptions { ExcludedThreadIdPrefixes = ["subagent-"] };

        // Act
        var page1 = await _store.ListThreadsAsync(limit: 5, offset: 0, options: options);
        var page2 = await _store.ListThreadsAsync(limit: 5, offset: 5, options: options);
        var page3 = await _store.ListThreadsAsync(limit: 5, offset: 10, options: options);

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
        await _store.SaveMetadataAsync("thread-1000-aaa", new ThreadMetadata
        {
            ThreadId = "thread-1000-aaa",
            LastUpdated = 9_000,
        });
        await _store.SaveMetadataAsync("thread-2000-bbb", new ThreadMetadata
        {
            ThreadId = "thread-2000-bbb",
            LastUpdated = 8_000,
        });
        await _store.SaveMetadataAsync("thread-3000-ccc", new ThreadMetadata
        {
            ThreadId = "thread-3000-ccc",
            LastUpdated = 7_000,
        });

        // Act
        var byLastUsed = await _store.ListThreadsAsync(
            limit: 10,
            options: new ConversationListOptions { SortOrder = ConversationSortOrder.LastUsed });
        var byCreated = await _store.ListThreadsAsync(
            limit: 10,
            options: new ConversationListOptions { SortOrder = ConversationSortOrder.Created });

        // Assert
        byLastUsed.Select(m => m.ThreadId).Should()
            .Equal("thread-1000-aaa", "thread-2000-bbb", "thread-3000-ccc");
        byCreated.Select(m => m.ThreadId).Should()
            .Equal("thread-3000-ccc", "thread-2000-bbb", "thread-1000-aaa");
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
        var provisioned = $"thread-{Guid.NewGuid():N}";
        const string NonNumeric = "thread-notatimestamp-zzz";
        await _store.SaveMetadataAsync("thread-5000-aaa", new ThreadMetadata
        {
            ThreadId = "thread-5000-aaa",
            LastUpdated = 1_000,
        });
        await _store.SaveMetadataAsync(provisioned, new ThreadMetadata
        {
            ThreadId = provisioned,
            LastUpdated = 7_000,
        });
        await _store.SaveMetadataAsync(NonNumeric, new ThreadMetadata
        {
            ThreadId = NonNumeric,
            LastUpdated = 6_500,
        });
        await _store.SaveMetadataAsync("thread-6000-bbb", new ThreadMetadata
        {
            ThreadId = "thread-6000-bbb",
            LastUpdated = 2_000,
        });

        // Act
        var byCreated = await _store.ListThreadsAsync(
            limit: 10,
            options: new ConversationListOptions { SortOrder = ConversationSortOrder.Created });

        // Assert - both fallbacks (7,000 and 6,500) rank above the parsed 6,000 and 5,000.
        byCreated.Select(m => m.ThreadId).Should()
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
        await _store.SaveMetadataAsync("subagent-newest", new ThreadMetadata
        {
            ThreadId = "subagent-newest",
            LastUpdated = 9_000,
        });
        await _store.SaveMetadataAsync("thread-1000-aaa", new ThreadMetadata
        {
            ThreadId = "thread-1000-aaa",
            LastUpdated = 8_000,
        });
        await _store.SaveMetadataAsync("workflow-older", new ThreadMetadata
        {
            ThreadId = "workflow-older",
            LastUpdated = 7_000,
        });

        // Act
        var withNull = await _store.ListThreadsAsync(limit: 10, offset: 0, options: null);
        var withDefault = await _store.ListThreadsAsync(
            limit: 10,
            offset: 0,
            options: ConversationListOptions.Default);

        // Assert
        withNull.Select(m => m.ThreadId).Should()
            .Equal("subagent-newest", "thread-1000-aaa", "workflow-older");
        withDefault.Select(m => m.ThreadId).Should().Equal(withNull.Select(m => m.ThreadId));
    }

    // The file store enumerates directories, which the filesystem hands back in its own order -
    // ascending here, the exact opposite of what is expected. Without the tie-break the tied rows
    // would come back in that order instead, so this test fails for the right reason.
    [Fact]
    public async Task ListThreadsAsync_BreaksLastUsedTiesByThreadIdDescending()
    {
        // Arrange
        foreach (var suffix in new[] { "bbb", "eee", "aaa", "fff", "ccc", "ddd" })
        {
            await _store.SaveMetadataAsync($"thread-5000-{suffix}", new ThreadMetadata
            {
                ThreadId = $"thread-5000-{suffix}",
                LastUpdated = 5_000,
            });
        }

        // Act
        var listed = await _store.ListThreadsAsync(limit: 10, offset: 0);

        // Assert
        listed.Select(m => m.ThreadId).Should().Equal(
            "thread-5000-fff",
            "thread-5000-eee",
            "thread-5000-ddd",
            "thread-5000-ccc",
            "thread-5000-bbb",
            "thread-5000-aaa");
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
        var expected = new List<string>();
        foreach (var suffix in new[] { "bbb", "eee", "aaa", "fff", "ccc", "ddd" })
        {
            var threadId = $"thread-5000-{suffix}";
            expected.Add(threadId);
            await _store.SaveMetadataAsync(threadId, new ThreadMetadata
            {
                ThreadId = threadId,
                LastUpdated = 5_000,
            });
        }

        // Act
        var paged = new List<string>();
        for (var offset = 0; offset < expected.Count; offset += 2)
        {
            var page = await _store.ListThreadsAsync(limit: 2, offset: offset);
            paged.AddRange(page.Select(m => m.ThreadId));
        }

        // Assert
        paged.Should().OnlyHaveUniqueItems("a tied row must not be returned by two different pages");
        paged.Should().BeEquivalentTo(expected, "no tied row may be skipped by offset paging");
    }

    #endregion

    #region Test Helpers

    private static List<PersistedMessage> CreateTestMessages(string threadId, string runId, int count)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return
        [
            .. Enumerable.Range(0, count)
                .Select(i => CreateMessage(threadId, runId, $"msg-{runId}-{i}", now + i, messageOrderIdx: i))
        ];
    }

    private static PersistedMessage CreateMessage(
        string threadId,
        string runId,
        string id,
        long timestamp,
        int? messageOrderIdx = null)
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

    private static ThreadMetadata CreateTestMetadata(string threadId)
    {
        return new ThreadMetadata
        {
            ThreadId = threadId,
            LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
    }

    #endregion
}
