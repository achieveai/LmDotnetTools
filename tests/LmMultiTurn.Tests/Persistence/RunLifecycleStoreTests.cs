using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LmMultiTurn.Tests.Persistence;

/// <summary>
/// The behaviour every <see cref="IRunLifecycleStore"/> implementation owes its callers.
/// </summary>
/// <remarks>
/// One suite run against all three stores rather than three suites, because the point of these
/// tests is that the stores agree: a run that can only terminalize once in memory must only
/// terminalize once on disk, or a caller that swaps stores silently loses the guarantee.
/// </remarks>
public abstract class RunLifecycleStoreTestsBase : IAsyncLifetime
{
    private static readonly DateTimeOffset Origin =
        new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The store under test.</summary>
    protected IRunLifecycleStore Store { get; private set; } = null!;

    /// <summary>Creates the implementation this fixture exercises.</summary>
    protected abstract Task<IRunLifecycleStore> CreateStoreAsync();

    /// <summary>Releases whatever <see cref="CreateStoreAsync"/> acquired.</summary>
    protected virtual Task DisposeStoreAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public async Task InitializeAsync() => Store = await CreateStoreAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => DisposeStoreAsync();

    #region Recording and reading a run

    [Fact]
    public async Task RecordRunStarted_ThenLoad_RoundTripsEveryField()
    {
        var state = new RunLifecycleState
        {
            ThreadId = "thread-1",
            RunId = "run-1",
            GenerationId = "gen-1",
            ParentRunId = "parent-run",
            ParentThreadId = "parent-thread",
            SpawningToolCallId = "call-spawn",
            SubAgentId = "researcher",
            CauseKind = "sub_agent_spawn",
            CauseToolCallId = "call-cause",
            TurnCount = 0,
            StartedAt = Origin,
        };

        await Store.RecordRunStartedAsync(state);
        var loaded = await Store.LoadRunLifecycleAsync("run-1");

        loaded.Should().NotBeNull();
        loaded!.ThreadId.Should().Be("thread-1");
        loaded.RunId.Should().Be("run-1");
        loaded.GenerationId.Should().Be("gen-1");
        loaded.ParentRunId.Should().Be("parent-run");
        loaded.ParentThreadId.Should().Be("parent-thread");
        loaded.SpawningToolCallId.Should().Be("call-spawn");
        loaded.SubAgentId.Should().Be("researcher");
        loaded.CauseKind.Should().Be("sub_agent_spawn");
        loaded.CauseToolCallId.Should().Be("call-cause");
        loaded.Phase.Should().Be(RunLifecyclePhase.Running);
        loaded.Outcome.Should().BeNull();
        loaded.StartedAt.Should().Be(Origin);
        loaded.TerminalAt.Should().BeNull();
        loaded.DeferredToolCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadRunLifecycle_UnknownRun_ReturnsNull()
    {
        var loaded = await Store.LoadRunLifecycleAsync("never-started");

        loaded.Should().BeNull();
    }

    [Fact]
    public async Task RecordRunStarted_WithoutThreadId_Throws()
    {
        var state = NewRun("", "run-1");

        var record = async () => await Store.RecordRunStartedAsync(state);

        _ = await record.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task RecordRunStarted_WithoutRunId_Throws()
    {
        var state = NewRun("thread-1", "");

        var record = async () => await Store.RecordRunStartedAsync(state);

        _ = await record.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task RecordRunStarted_AlreadyTerminalState_Throws()
    {
        var state = NewRun("thread-1", "run-1") with { Phase = RunLifecyclePhase.Terminal };

        var record = async () => await Store.RecordRunStartedAsync(state);

        _ = await record.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task RecordRunStarted_AfterTerminal_Throws()
    {
        await Store.RecordRunStartedAsync(NewRun("thread-1", "run-1"));
        _ = await Store.TryMarkRunTerminalAsync("run-1", "completed", 3, Origin.AddSeconds(5));

        var restart = async () => await Store.RecordRunStartedAsync(NewRun("thread-1", "run-1"));

        _ = await restart.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ListRunLifecycle_ReturnsOnlyThatThread_NewestFirst()
    {
        await Store.RecordRunStartedAsync(NewRun("thread-1", "run-b", startedAt: Origin.AddSeconds(2)));
        await Store.RecordRunStartedAsync(NewRun("thread-1", "run-a", startedAt: Origin));
        await Store.RecordRunStartedAsync(NewRun("thread-2", "run-other"));

        var listed = await Store.ListRunLifecycleAsync("thread-1");

        listed.Select(r => r.RunId).Should().Equal("run-b", "run-a");
    }

    [Fact]
    public async Task ListNonTerminalRuns_ReturnsOldestFirst()
    {
        await Store.RecordRunStartedAsync(NewRun("thread-1", "run-b", startedAt: Origin.AddSeconds(2)));
        await Store.RecordRunStartedAsync(NewRun("thread-1", "run-a", startedAt: Origin));

        var listed = await Store.ListNonTerminalRunsAsync("thread-1");

        listed.Select(r => r.RunId).Should().Equal("run-a", "run-b");
    }

    #endregion

    #region Terminalization

    [Fact]
    public async Task TryMarkRunTerminal_FirstCallWins_SecondReportsFalse()
    {
        await Store.RecordRunStartedAsync(NewRun("thread-1", "run-1"));

        var first = await Store.TryMarkRunTerminalAsync("run-1", "completed", 4, Origin.AddSeconds(9));
        var second = await Store.TryMarkRunTerminalAsync("run-1", "errored", 7, Origin.AddSeconds(11));

        first.Should().BeTrue();
        second.Should().BeFalse();

        var loaded = await Store.LoadRunLifecycleAsync("run-1");
        loaded!.Phase.Should().Be(RunLifecyclePhase.Terminal);
        loaded.Outcome.Should().Be("completed");
        loaded.TurnCount.Should().Be(4);
        loaded.TerminalAt.Should().Be(Origin.AddSeconds(9));
    }

    [Fact]
    public async Task TryMarkRunTerminal_UnknownRun_ReportsFalse()
    {
        var marked = await Store.TryMarkRunTerminalAsync("ghost", "completed", 0, Origin);

        marked.Should().BeFalse();
    }

    [Fact]
    public async Task TryMarkRunTerminal_ConcurrentCallers_ExactlyOneWins()
    {
        await Store.RecordRunStartedAsync(NewRun("thread-1", "run-1"));

        var attempts = Enumerable.Range(0, 4).Select(i => Task.Run(
            () => Store.TryMarkRunTerminalAsync("run-1", $"outcome-{i}", i, Origin.AddSeconds(i))));

        var results = await Task.WhenAll(attempts);

        results.Count(won => won).Should().Be(1);
    }

    [Fact]
    public async Task ListNonTerminalRuns_ExcludesTerminalizedRuns()
    {
        await Store.RecordRunStartedAsync(NewRun("thread-1", "run-done"));
        await Store.RecordRunStartedAsync(NewRun("thread-1", "run-live"));
        _ = await Store.TryMarkRunTerminalAsync("run-done", "completed", 1, Origin.AddSeconds(1));

        var live = await Store.ListNonTerminalRunsAsync("thread-1");

        live.Select(r => r.RunId).Should().Equal("run-live");
    }

    #endregion

    #region Deferral

    [Fact]
    public async Task RecordDeferredToolCall_AssignsOrdinalsInCommitOrder()
    {
        await Store.RecordRunStartedAsync(NewRun("thread-1", "run-1"));

        var first = await Store.RecordDeferredToolCallAsync("run-1", NewDeferral("call-a"));
        var second = await Store.RecordDeferredToolCallAsync("run-1", NewDeferral("call-b"));

        first.Ordinal.Should().Be(1);
        second.Ordinal.Should().Be(2);

        var loaded = await Store.LoadRunLifecycleAsync("run-1");
        loaded!.DeferredToolCalls.Select(d => d.ToolCallId).Should().Equal("call-a", "call-b");
        loaded.UnresolvedToolCalls.Should().HaveCount(2);
    }

    [Fact]
    public async Task RecordDeferredToolCall_SameCallTwice_ReturnsTheCommittedRecord()
    {
        await Store.RecordRunStartedAsync(NewRun("thread-1", "run-1"));
        var committed = await Store.RecordDeferredToolCallAsync("run-1", NewDeferral("call-a"));

        var again = await Store.RecordDeferredToolCallAsync(
            "run-1",
            NewDeferral("call-a") with { ToolName = "different_tool" });

        again.Ordinal.Should().Be(committed.Ordinal);
        again.ToolName.Should().Be(committed.ToolName);

        var loaded = await Store.LoadRunLifecycleAsync("run-1");
        loaded!.DeferredToolCalls.Should().ContainSingle();
    }

    [Fact]
    public async Task RecordDeferredToolCall_UnknownRun_Throws()
    {
        var record = async () =>
            await Store.RecordDeferredToolCallAsync("never-started", NewDeferral("call-a"));

        _ = await record.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task TryResolveDeferredToolCall_MarksItResolved()
    {
        await Store.RecordRunStartedAsync(NewRun("thread-1", "run-1"));
        _ = await Store.RecordDeferredToolCallAsync("run-1", NewDeferral("call-a"));

        var outcome = await Store.TryResolveDeferredToolCallAsync(
            "thread-1", "call-a", "fingerprint-1", "child-run-1", Origin.AddSeconds(30));

        outcome.Should().Be(DeferredResolutionOutcome.Resolved);

        var loaded = await Store.LoadRunLifecycleAsync("run-1");
        var deferral = loaded!.DeferredToolCalls.Single();
        deferral.IsResolved.Should().BeTrue();
        deferral.ResolvedAt.Should().Be(Origin.AddSeconds(30));
        deferral.ResolutionFingerprint.Should().Be("fingerprint-1");
        deferral.ChildRunId.Should().Be("child-run-1");
        loaded.UnresolvedToolCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task TryResolveDeferredToolCall_SameFingerprintTwice_IsADuplicate()
    {
        await Store.RecordRunStartedAsync(NewRun("thread-1", "run-1"));
        _ = await Store.RecordDeferredToolCallAsync("run-1", NewDeferral("call-a"));
        _ = await Store.TryResolveDeferredToolCallAsync(
            "thread-1", "call-a", "fingerprint-1", "child-run-1", Origin.AddSeconds(30));

        var outcome = await Store.TryResolveDeferredToolCallAsync(
            "thread-1", "call-a", "fingerprint-1", "child-run-2", Origin.AddSeconds(60));

        outcome.Should().Be(DeferredResolutionOutcome.Duplicate);

        var loaded = await Store.LoadRunLifecycleAsync("run-1");
        var deferral = loaded!.DeferredToolCalls.Single();
        deferral.ChildRunId.Should().Be("child-run-1", "the first resolution stands");
        deferral.ResolvedAt.Should().Be(Origin.AddSeconds(30));
    }

    [Fact]
    public async Task TryResolveDeferredToolCall_DifferentFingerprint_IsAConflictAndChangesNothing()
    {
        await Store.RecordRunStartedAsync(NewRun("thread-1", "run-1"));
        _ = await Store.RecordDeferredToolCallAsync("run-1", NewDeferral("call-a"));
        _ = await Store.TryResolveDeferredToolCallAsync(
            "thread-1", "call-a", "fingerprint-1", childRunId: null, Origin.AddSeconds(30));

        var outcome = await Store.TryResolveDeferredToolCallAsync(
            "thread-1", "call-a", "fingerprint-2", childRunId: null, Origin.AddSeconds(60));

        outcome.Should().Be(DeferredResolutionOutcome.Conflict);

        var loaded = await Store.LoadRunLifecycleAsync("run-1");
        loaded!.DeferredToolCalls.Single().ResolutionFingerprint.Should().Be("fingerprint-1");
    }

    [Fact]
    public async Task TryResolveDeferredToolCall_UnknownCall_IsNotFound()
    {
        await Store.RecordRunStartedAsync(NewRun("thread-1", "run-1"));

        var outcome = await Store.TryResolveDeferredToolCallAsync(
            "thread-1", "call-never-deferred", "fingerprint-1", null, Origin);

        outcome.Should().Be(DeferredResolutionOutcome.NotFound);
    }

    [Fact]
    public async Task TryResolveDeferredToolCall_WrongThread_IsNotFound()
    {
        await Store.RecordRunStartedAsync(NewRun("thread-1", "run-1"));
        _ = await Store.RecordDeferredToolCallAsync("run-1", NewDeferral("call-a"));

        var outcome = await Store.TryResolveDeferredToolCallAsync(
            "thread-2", "call-a", "fingerprint-1", null, Origin);

        outcome.Should().Be(DeferredResolutionOutcome.NotFound);
    }

    [Fact]
    public async Task TryResolveDeferredToolCall_AfterItsRunEnded_StillResolves()
    {
        await Store.RecordRunStartedAsync(NewRun("thread-1", "run-1"));
        _ = await Store.RecordDeferredToolCallAsync("run-1", NewDeferral("call-a"));
        _ = await Store.TryMarkRunTerminalAsync("run-1", "completed", 1, Origin.AddSeconds(5));

        var outcome = await Store.TryResolveDeferredToolCallAsync(
            "thread-1", "call-a", "fingerprint-1", "child-run-1", Origin.AddSeconds(30));

        outcome.Should().Be(DeferredResolutionOutcome.Resolved);
    }

    [Fact]
    public async Task TryResolveDeferredToolCall_ConcurrentCallers_ExactlyOneResolves()
    {
        await Store.RecordRunStartedAsync(NewRun("thread-1", "run-1"));
        _ = await Store.RecordDeferredToolCallAsync("run-1", NewDeferral("call-a"));

        var attempts = Enumerable.Range(0, 4).Select(i => Task.Run(
            () => Store.TryResolveDeferredToolCallAsync(
                "thread-1", "call-a", $"fingerprint-{i}", $"child-{i}", Origin.AddSeconds(30 + i))));

        var outcomes = await Task.WhenAll(attempts);

        outcomes.Count(o => o == DeferredResolutionOutcome.Resolved).Should().Be(1);
        outcomes.Should().NotContain(DeferredResolutionOutcome.NotFound);
    }

    #endregion

    private static RunLifecycleState NewRun(
        string threadId,
        string runId,
        DateTimeOffset? startedAt = null) =>
        new()
        {
            ThreadId = threadId,
            RunId = runId,
            GenerationId = $"gen-{runId}",
            CauseKind = "user_input",
            StartedAt = startedAt ?? Origin,
        };

    private static DeferredToolCallRecord NewDeferral(string toolCallId) =>
        new()
        {
            ToolCallId = toolCallId,
            ToolName = "slow_tool",
            GenerationId = "gen-run-1",
            DeferredAt = Origin.AddSeconds(1),
        };
}

/// <summary>Runs the shared lifecycle-store suite against the in-memory store.</summary>
public sealed class InMemoryRunLifecycleStoreTests : RunLifecycleStoreTestsBase
{
    /// <inheritdoc />
    protected override Task<IRunLifecycleStore> CreateStoreAsync() =>
        Task.FromResult<IRunLifecycleStore>(new InMemoryConversationStore());
}

/// <summary>Runs the shared lifecycle-store suite against the file store.</summary>
public sealed class FileRunLifecycleStoreTests : RunLifecycleStoreTestsBase
{
    private string _directory = null!;

    /// <inheritdoc />
    protected override Task<IRunLifecycleStore> CreateStoreAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"lifecycle_{Guid.NewGuid():N}");
        return Task.FromResult<IRunLifecycleStore>(new FileConversationStore(_directory));
    }

    /// <inheritdoc />
    protected override Task DisposeStoreAsync()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Ignore - a temp directory left behind is not worth failing a green test over.
        }

        return Task.CompletedTask;
    }
}

/// <summary>Runs the shared lifecycle-store suite against the SQLite store.</summary>
public sealed class SqliteRunLifecycleStoreTests : RunLifecycleStoreTestsBase
{
    private string _databasePath = null!;
    private SqliteConversationStore _sqliteStore = null!;

    /// <inheritdoc />
    protected override Task<IRunLifecycleStore> CreateStoreAsync()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"lifecycle_{Guid.NewGuid():N}.db");
        _sqliteStore = new SqliteConversationStore(_databasePath);
        return Task.FromResult<IRunLifecycleStore>(_sqliteStore);
    }

    /// <inheritdoc />
    protected override async Task DisposeStoreAsync()
    {
        await _sqliteStore.DisposeAsync();

        SqliteConnection.ClearAllPools();
        await Task.Delay(50);

        TryDelete(_databasePath);
        TryDelete(_databasePath + "-wal");
        TryDelete(_databasePath + "-shm");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Ignore - the file may still be locked briefly after the pool is cleared.
        }
    }
}
