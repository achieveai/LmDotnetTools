using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

namespace LmStreaming.Sample.Tests.Persistence;

/// <summary>
/// Unit coverage for <see cref="NonOwningConversationStore"/> — the decorator that lets the sample hand
/// its single application-wide conversation store to spawned sub-agents WITHOUT letting a child dispose
/// it. Proves it (a) exposes neither disposal interface so <c>SubAgentManager</c>'s
/// <c>store is IAsyncDisposable</c> ownership checks skip it, and (b) forwards reads/writes across BOTH
/// <see cref="IConversationStore"/> and <see cref="IRunLedgerStore"/> to the wrapped instance.
/// </summary>
public sealed class NonOwningConversationStoreTests
{
    private const string ThreadId = "subagent-child-1";

    [Fact]
    public void NonOwningWrapper_ImplementsNeitherDisposalInterface()
    {
        var wrapper = new NonOwningConversationStore(new InMemoryConversationStore());

        ((object)wrapper is IAsyncDisposable)
            .Should()
            .BeFalse("a child must never be able to dispose the shared store");
        ((object)wrapper is IDisposable).Should().BeFalse("a child must never be able to dispose the shared store");
    }

    [Fact]
    public async Task NonOwningWrapper_ForwardsMessageWritesAndReads_ToUnderlyingStore()
    {
        var underlying = new InMemoryConversationStore();
        var wrapper = new NonOwningConversationStore(underlying);
        var message = MessagePersistenceConverter.ToPersistedMessage(
            new TextMessage { Text = "hi from child", Role = Role.Assistant },
            ThreadId,
            "run-1"
        );

        // Write through the wrapper...
        await wrapper.AppendMessagesAsync(ThreadId, [message]);

        // ...and it must be visible on the UNDERLYING shared store (both directions forward).
        var fromUnderlying = await underlying.LoadMessagesAsync(ThreadId);
        fromUnderlying.Should().ContainSingle().Which.Id.Should().Be(message.Id);

        var fromWrapper = await wrapper.LoadMessagesAsync(ThreadId);
        fromWrapper.Should().ContainSingle().Which.Id.Should().Be(message.Id);
    }

    [Fact]
    public async Task NonOwningWrapper_ForwardsRunLedgerMembers_ToUnderlyingStore()
    {
        var underlying = new InMemoryConversationStore();
        var wrapper = new NonOwningConversationStore(underlying);
        var acceptedAt = DateTimeOffset.UtcNow;

        await wrapper.RecordAcceptedInputAsync(ThreadId, "input-1", acceptedAt);

        var fromUnderlying = await underlying.ListAcceptedInputIdsAsync(ThreadId);
        fromUnderlying.Should().Contain("input-1");

        var fromWrapper = await wrapper.ListAcceptedInputIdsAsync(ThreadId);
        fromWrapper.Should().Contain("input-1");
    }

    private static NonOwningConversationStore WithProvenance(
        IConversationStore underlying,
        string childThreadId,
        string parentThreadId
    ) =>
        new(
            underlying,
            childThreadId,
            () =>
                SubAgentProvenance.Build(
                    parentThreadId,
                    new SubAgentSnapshot(
                        AgentId: "child-1",
                        Name: "alpha",
                        TemplateName: "code-reviewer:security",
                        Task: "check auth",
                        Status: SubAgentStatus.Running,
                        ThreadId: childThreadId,
                        LastActivityUtc: null
                    )
                )
        );

    [Fact]
    public async Task NonOwningWrapper_StampsProvenance_OnBothMetadataWritePaths()
    {
        var underlying = new InMemoryConversationStore();
        var wrapper = WithProvenance(underlying, ThreadId, "thread-parent");

        await wrapper.SaveMetadataAsync(ThreadId, new ThreadMetadata { ThreadId = ThreadId, LastUpdated = 1 });

        var saved = await underlying.LoadMetadataAsync(ThreadId);
        SubAgentProvenance.TryProject(saved!, "thread-parent")!.Template.Should().Be("code-reviewer:security");

        // UpdateMetadataAsync is the path MultiTurnAgentBase actually takes after each run, and it must
        // preserve properties the caller set as well as add the stamp.
        await wrapper.UpdateMetadataAsync(
            ThreadId,
            existing =>
                (existing ?? new ThreadMetadata { ThreadId = ThreadId, LastUpdated = 1 }) with
                {
                    LastUpdated = 2,
                    Properties = (existing?.Properties ?? ImmutableDictionary<string, object>.Empty).SetItem(
                        "usage.records",
                        "kept"
                    ),
                }
        );

        var updated = await underlying.LoadMetadataAsync(ThreadId);
        updated!.Properties!["usage.records"].Should().Be("kept");
        SubAgentProvenance.TryProject(updated, "thread-parent")!.Name.Should().Be("alpha");
    }

    /// <summary>
    /// A child's store is also written to on behalf of OTHER threads — the conversation usage
    /// projection persists under the ROOT conversation id. Those writes must not be stamped, or the
    /// root would list itself as its own child.
    /// </summary>
    [Fact]
    public async Task NonOwningWrapper_DoesNotStamp_WritesForOtherThreads()
    {
        var underlying = new InMemoryConversationStore();
        var wrapper = WithProvenance(underlying, ThreadId, "thread-parent");

        await wrapper.SaveMetadataAsync(
            "thread-root",
            new ThreadMetadata { ThreadId = "thread-root", LastUpdated = 1 }
        );

        var saved = await underlying.LoadMetadataAsync("thread-root");
        (saved!.Properties?.ContainsKey(SubAgentProvenance.ParentThreadIdKey) ?? false).Should().BeFalse();
    }

    [Fact]
    public async Task NonOwningWrapper_RemovesStaleRoutingAndEffort_WhenReplacementSnapshotHasNone()
    {
        var underlying = new InMemoryConversationStore();
        SubAgentSnapshot snapshot = new(
            AgentId: "child-1",
            Name: "alpha",
            TemplateName: "code-reviewer:security",
            Task: "check auth",
            Status: SubAgentStatus.Running,
            ThreadId: ThreadId,
            LastActivityUtc: null,
            EffectiveModelId: "gpt-5.6-sol",
            EffectiveModelIntelligence: 5,
            ModelSelectionSource: "spawn-tier",
            RequestedReasoningEffort: "xhigh",
            ShapedReasoningEffort: "xhigh"
        );
        var wrapper = new NonOwningConversationStore(
            underlying,
            ThreadId,
            () => SubAgentProvenance.Build("thread-parent", snapshot)
        );

        await wrapper.SaveMetadataAsync(ThreadId, new ThreadMetadata { ThreadId = ThreadId, LastUpdated = 1 });
        var routed = await underlying.LoadMetadataAsync(ThreadId);
        routed!
            .Properties!.Should()
            .ContainKeys(
                SubAgentProvenance.ModelKey,
                SubAgentProvenance.ModelIntelligenceKey,
                SubAgentProvenance.RequestedReasoningEffortKey,
                SubAgentProvenance.ShapedReasoningEffortKey
            );

        snapshot = snapshot with
        {
            EffectiveModelId = null,
            EffectiveModelIntelligence = null,
            ModelSelectionSource = "parent",
            RequestedReasoningEffort = null,
            ShapedReasoningEffort = null,
        };
        await wrapper.UpdateMetadataAsync(ThreadId, existing => existing! with { LastUpdated = 2 });

        var replacement = await underlying.LoadMetadataAsync(ThreadId);
        replacement!
            .Properties!.Should()
            .NotContainKeys(
                SubAgentProvenance.ModelKey,
                SubAgentProvenance.ModelIntelligenceKey,
                SubAgentProvenance.RequestedReasoningEffortKey,
                SubAgentProvenance.ShapedReasoningEffortKey
            );
        replacement!.Properties![SubAgentProvenance.ModelSelectionSourceKey].Should().Be("parent");
    }

    /// <summary>
    /// Task 1 (daemon-recursive-review-completion-barrier): the manager's causal terminal-state push
    /// goes through this wrapper's <see cref="NonOwningConversationStore.UpdateMetadataAsync"/> path —
    /// the SAME path a passive/child refresh also uses — so it must merge the exact terminal
    /// status/timestamp in exactly the same way, whether the write originates from the child's own
    /// loop or from the manager's new push.
    /// </summary>
    [Fact]
    public async Task NonOwningWrapper_ForwardsExactTerminalStatusAndTimestamp_ThroughUpdateMetadataAsync()
    {
        var underlying = new InMemoryConversationStore();
        var terminalAt = DateTimeOffset.FromUnixTimeMilliseconds(123_456_000);
        var wrapper = new NonOwningConversationStore(
            underlying,
            ThreadId,
            () =>
                SubAgentProvenance.Build(
                    "thread-parent",
                    new SubAgentSnapshot(
                        AgentId: "child-1",
                        Name: "alpha",
                        TemplateName: "code-reviewer:security",
                        Task: "check auth",
                        Status: SubAgentStatus.Completed,
                        ThreadId: ThreadId,
                        LastActivityUtc: null,
                        TerminalAtUtc: terminalAt
                    )
                )
        );

        // Mirrors the manager's causal push: an atomic update against metadata that may not exist yet.
        await wrapper.UpdateMetadataAsync(
            ThreadId,
            existing => existing ?? new ThreadMetadata { ThreadId = ThreadId, LastUpdated = 1 }
        );

        var saved = await underlying.LoadMetadataAsync(ThreadId);
        var projected = SubAgentProvenance.TryProject(saved!, "thread-parent");

        projected!.Status.Should().Be("completed");
        projected
            .LastActivityUtc.Should()
            .Be(
                terminalAt,
                "the exact terminal instant captured at the transition must survive, not a value "
                    + "recomputed at write time"
            );
    }

    /// <summary>
    /// Task 1 review, Finding 2: <see cref="NonOwningConversationStore"/>'s metadata-write merge is
    /// purely additive — it never removes a key merely because a later stamp omits it. Combined with
    /// the live provenance supplier being re-resolved on EVERY write (mirroring
    /// <c>Program.cs</c>'s <c>describeChild</c>), a stale <see cref="SubAgentProvenance.TerminalAtKey"/>
    /// from a PRIOR terminal transition would otherwise survive in the PERSISTED metadata even after
    /// a restarted child returns to Running — not just the in-memory snapshot.
    /// </summary>
    [Fact]
    public async Task NonOwningWrapper_RemovesStaleTerminalTimestamp_WhenAFollowingWriteReportsRunning()
    {
        var underlying = new InMemoryConversationStore();
        var status = SubAgentStatus.Completed;
        var terminalAt = DateTimeOffset.FromUnixTimeMilliseconds(1_000_000);

        // Mirrors Program.cs's describeChild: re-resolved fresh on every write from the live
        // (mutable) manager state, not frozen at construction time.
        SubAgentSnapshot Snapshot() =>
            new(
                AgentId: "child-1",
                Name: "alpha",
                TemplateName: "code-reviewer:security",
                Task: "check auth",
                Status: status,
                ThreadId: ThreadId,
                LastActivityUtc: null,
                TerminalAtUtc: status == SubAgentStatus.Completed ? terminalAt : null
            );

        var wrapper = new NonOwningConversationStore(
            underlying,
            ThreadId,
            () => SubAgentProvenance.Build("thread-parent", Snapshot())
        );

        // First write: terminal (Completed) — TerminalAtKey lands in persisted metadata.
        await wrapper.SaveMetadataAsync(ThreadId, new ThreadMetadata { ThreadId = ThreadId, LastUpdated = 1 });
        var afterTerminal = await underlying.LoadMetadataAsync(ThreadId);
        afterTerminal!.Properties!.Should().ContainKey(SubAgentProvenance.TerminalAtKey);

        // Restart: the child is Running again (mirrors SubAgentState.TryArmRunning clearing
        // TerminalAtUtc, Finding 2's in-memory half).
        status = SubAgentStatus.Running;

        // Second write: the REAL path MultiTurnAgentBase uses after each run —
        // UpdateMetadataAsync, preserving the existing persisted properties (carrying the stale
        // TerminalAtKey forward) and only touching its own. This must REMOVE the stale terminal
        // timestamp, not merely leave it un-refreshed — a bare SaveMetadataAsync with a fresh,
        // property-less ThreadMetadata would mask the bug by discarding the stale key anyway.
        await wrapper.UpdateMetadataAsync(
            ThreadId,
            existing =>
                (existing ?? new ThreadMetadata { ThreadId = ThreadId, LastUpdated = 1 }) with
                {
                    LastUpdated = 2,
                }
        );
        var afterRestart = await underlying.LoadMetadataAsync(ThreadId);

        afterRestart!
            .Properties!.Should()
            .NotContainKey(
                SubAgentProvenance.TerminalAtKey,
                "a restarted, currently-running child must not still report a stale terminal instant "
                    + "in its PERSISTED metadata"
            );
        SubAgentProvenance.TryProject(afterRestart, "thread-parent")!.Status.Should().Be("running");
    }
}
