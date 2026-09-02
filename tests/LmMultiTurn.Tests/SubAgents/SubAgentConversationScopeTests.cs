using System.Text.Json;
using System.Text.RegularExpressions;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Identity;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests.SubAgents;

/// <summary>
/// #705. A spawned sub-agent's id is an ORDINAL — <c>agent-1</c>, <c>agent-2</c>, … — numbered in strict
/// spawn order under the ROOT conversation, so a human can read it, the model can reference it, and it
/// survives a restart because the counter is persisted with the root conversation. Uniqueness across
/// conversations is the THREAD id's job: <c>subagent-{rootTag}-agent-N</c>, where the tag is a
/// deterministic digest of the root thread id (same conversation → same tag, so nested spawns land in
/// the same scope; different conversations → different tags, so two <c>agent-1</c>s never share a
/// transcript). The guid the id used to carry is gone.
/// </summary>
public class SubAgentConversationScopeTests : IAsyncLifetime
{
    private static readonly Regex OrdinalId = new(@"^agent-[1-9][0-9]*$", RegexOptions.Compiled);
    private static readonly Regex ScopedThreadId = new(
        @"^subagent-[0-9a-f]{12}-agent-[1-9][0-9]*$",
        RegexOptions.Compiled
    );

    private readonly List<SubAgentManager> _managers = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // Bounded AND best-effort. Bounding each teardown (#362) turned a stall into a throw, and a
        // throw mid-loop would exit DisposeAsync with every LATER manager still undisposed — trading
        // one leak shape for another. Collect, dispose them all, then report together.
        List<Exception>? failures = null;
        foreach (var manager in _managers)
        {
            try
            {
                await Wait.ForTeardownAsync(manager, "a sub-agent manager created by this test");
            }
            catch (Exception ex)
            {
                // Collected, never swallowed: rethrown as an aggregate below.
                (failures ??= []).Add(ex);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException(
                "One or more sub-agent managers failed to tear down within their ceiling; every "
                    + "manager was still disposed before this was reported.",
                failures
            );
        }
    }

    [Fact]
    public async Task SpawnAsync_NumbersAgentsInSpawnOrder_StartingAtOne()
    {
        // The ordinal is the id. Template, explicit name, and background/foreground make no difference:
        // the third spawn in this conversation is agent-3, full stop.
        var store = await StoreWithRootAsync("thread-ordinal-basic");
        var manager = CreateManager("thread-ordinal-basic", store);

        var id1 = ParseAgentId(await manager.SpawnAsync("worker", "first", runInBackground: true));
        var id2 = ParseAgentId(await manager.SpawnAsync("reviewer", "second", name: "named", runInBackground: true));
        var id3 = ParseAgentId(await manager.SpawnAsync("worker", "third", runInBackground: true));

        id1.Should().Be("agent-1");
        id2.Should().Be("agent-2");
        id3.Should().Be("agent-3");
        id1.Should().MatchRegex(OrdinalId);

        // The ordinal is what the tools resolve — SendMessage/CheckAgents go through TryGetAgent.
        manager.TryGetAgent("agent-2", out var second).Should().BeTrue();
        second!.ThreadId.Should().MatchRegex(ScopedThreadId);
    }

    [Fact]
    public async Task SpawnAsync_DerivesTheReadableName_FromTheOrdinal_WhenNoNameIsGiven()
    {
        // The fallback name used to be `{role}-{first six hex of the guid}`; with the ordinal as the id
        // the readable name is `{role}-{N}`, so telemetry and SendMessage targets read as e.g. worker-2.
        var manager = CreateManager("thread-ordinal-names");

        _ = await manager.SpawnAsync("worker", "first", runInBackground: true);
        _ = await manager.SpawnAsync("plugin:reviewer", "second", runInBackground: true);
        _ = await manager.SpawnAsync("worker", "third", name: "explicit", runInBackground: true);

        var names = manager.ListAgents().OrderBy(s => s.AgentId, StringComparer.Ordinal).Select(s => s.Name).ToList();
        names.Should().Equal("worker-1", "reviewer-2", "explicit");
    }

    [Fact]
    public async Task SpawnAsync_ChildThreadId_IsScopedToTheRootConversation()
    {
        // Two conversations both mint agent-1. Their TRANSCRIPTS must not collide: the thread id
        // carries a digest of the root thread id, and the digest is the same for every spawn within one
        // conversation so the whole family shares one scope.
        var store = new InMemoryConversationStore();
        var managerA = CreateManager("thread-conversation-alpha", store);
        var managerB = CreateManager("thread-conversation-beta", store);

        var idA = ParseAgentId(await managerA.SpawnAsync("worker", "do work", runInBackground: true));
        var idB = ParseAgentId(await managerB.SpawnAsync("worker", "do work", runInBackground: true));
        var idA2 = ParseAgentId(await managerA.SpawnAsync("worker", "more work", runInBackground: true));

        idA.Should().Be("agent-1");
        idB.Should().Be("agent-1", "numbering is per root conversation, not per process");

        managerA.TryGetAgent(idA, out var agentA).Should().BeTrue();
        managerB.TryGetAgent(idB, out var agentB).Should().BeTrue();
        managerA.TryGetAgent(idA2, out var agentA2).Should().BeTrue();

        agentA!.ThreadId.Should().MatchRegex(ScopedThreadId);
        agentB!.ThreadId.Should().MatchRegex(ScopedThreadId);
        agentA.ThreadId.Should().NotBe(agentB.ThreadId, "same ordinal, different conversations, different transcripts");
        TagOf(agentA.ThreadId).Should().NotBe(TagOf(agentB.ThreadId));
        TagOf(agentA2!.ThreadId).Should().Be(TagOf(agentA.ThreadId), "one conversation, one scope");

        agentA.ThreadId.Should().Be(SubAgentManager.SubAgentThreadId("thread-conversation-alpha", "agent-1"));
    }

    [Fact]
    public async Task SpawnAsync_ContinuesNumbering_WhenTheManagerIsRebuiltOverTheSameStore()
    {
        // Restart. The manager is in-memory state that dies with the host; the counter is not. A new
        // manager over the same store (same root conversation) must hand out agent-3, never agent-1 again.
        const string root = "thread-ordinal-restart";
        var store = await StoreWithRootAsync(root);

        var before = CreateManager(root, store);
        _ = await before.SpawnAsync("worker", "first", runInBackground: true);
        _ = await before.SpawnAsync("worker", "second", runInBackground: true);
        await Wait.ForTeardownAsync(before, "the pre-restart manager");
        _ = _managers.Remove(before);

        var after = CreateManager(root, store);
        var id = ParseAgentId(await after.SpawnAsync("worker", "third", runInBackground: true));

        id.Should().Be("agent-3");

        // The counter lives in the ROOT conversation's metadata, under a library-owned key.
        var rootMetadata = await store.LoadMetadataAsync(root);
        rootMetadata!.Properties.Should().ContainKey(SubAgentManager.NextOrdinalProperty);
        Convert.ToInt32(rootMetadata.Properties![SubAgentManager.NextOrdinalProperty]).Should().Be(4);
    }

    [Fact]
    public async Task SpawnAsync_WithoutAStore_StillNumbersSequentially_InProcess()
    {
        // No store means nothing to persist, not nothing to number: the allocator falls back to an
        // in-process counter per root so the ids still read agent-1, agent-2.
        var manager = CreateManager("thread-ordinal-no-store");

        var id1 = ParseAgentId(await manager.SpawnAsync("worker", "first", runInBackground: true));
        var id2 = ParseAgentId(await manager.SpawnAsync("worker", "second", runInBackground: true));

        id1.Should().Be("agent-1");
        id2.Should().Be("agent-2");
    }

    [Fact]
    public async Task SpawnAsync_ContinuesNumbering_FromPersistedTranscripts_WhenTheRootHasNoRow()
    {
        // A host that never minted a metadata row for the root (a CLI, or a conversation that only
        // exists as messages) has no counter to continue from after a restart. The child transcripts
        // already in the store are the record of the numbers handed out, so numbering resumes above the
        // highest one under THIS root's scope — another conversation's agent-9 and a legacy-shaped
        // thread are not in the sequence.
        const string root = "thread-ordinal-rowless";
        var store = new InMemoryConversationStore();
        await SaveRowAsync(store, SubAgentManager.SubAgentThreadId(root, "agent-1"));
        await SaveRowAsync(store, SubAgentManager.SubAgentThreadId(root, "agent-2"));
        await SaveRowAsync(store, SubAgentManager.SubAgentThreadId("thread-somebody-else", "agent-9"));
        await SaveRowAsync(store, SubAgentManager.SubAgentThreadId(root, "0123456789ab-deadbeef"));

        var manager = CreateManager(root, store);
        var id = ParseAgentId(await manager.SpawnAsync("worker", "third", runInBackground: true));

        id.Should().Be("agent-3");
        (await store.LoadMetadataAsync(root)).Should().BeNull("a spawn must not invent the root's row");
    }

    [Fact]
    public async Task SpawnAsync_ContinuesNumbering_FromPersistedTranscripts_WhenTheRootRowHasNoCounter()
    {
        // The root row exists but has never carried the counter (it predates #705, or a store dropped
        // the property). The scan supplies the floor once; from then on the row's counter is the record.
        const string root = "thread-ordinal-counterless";
        var store = await StoreWithRootAsync(root);
        await SaveRowAsync(store, SubAgentManager.SubAgentThreadId(root, "agent-4"));

        var manager = CreateManager(root, store);
        var id = ParseAgentId(await manager.SpawnAsync("worker", "fifth", runInBackground: true));

        id.Should().Be("agent-5");
        var rootMetadata = await store.LoadMetadataAsync(root);
        Convert.ToInt32(rootMetadata!.Properties![SubAgentManager.NextOrdinalProperty]).Should().Be(6);
    }

    [Fact]
    public async Task SpawnAsync_NestedSpawn_NumbersUnderTheRootConversation_AndSharesItsScope()
    {
        // A child's own manager is built from the parent's ChildOptions — exactly what the child loop
        // receives — so a grandchild draws from the ROOT's counter and lands in the root's thread
        // scope. Numbering is one sequence for the whole family: root spawns agent-1, agent-1 spawns
        // agent-2, root then spawns agent-3.
        const string root = "thread-ordinal-nested";
        var store = await StoreWithRootAsync(root);
        var rootManager = CreateManager(root, store);

        var childId = ParseAgentId(await rootManager.SpawnAsync("worker", "child", runInBackground: true));
        rootManager.TryGetAgent(childId, out var child).Should().BeTrue();

        var nestedManager = CreateManager(child!.ThreadId, store, options: rootManager.ChildOptions);
        var grandchildId = ParseAgentId(await nestedManager.SpawnAsync("worker", "grandchild", runInBackground: true));
        var siblingId = ParseAgentId(await rootManager.SpawnAsync("worker", "sibling", runInBackground: true));

        childId.Should().Be("agent-1");
        grandchildId.Should().Be("agent-2");
        siblingId.Should().Be("agent-3");

        nestedManager.TryGetAgent(grandchildId, out var grandchild).Should().BeTrue();
        TagOf(grandchild!.ThreadId).Should().Be(TagOf(child.ThreadId), "the grandchild lives in the root's scope");
        grandchild.ThreadId.Should().Be(SubAgentManager.SubAgentThreadId(root, "agent-2"));
    }

    [Fact]
    public async Task SpawnAsync_ARejectedTemplate_DoesNotConsumeAnOrdinal()
    {
        // Numbers are never reused, so anything that fails BEFORE allocation must not allocate — or the
        // user's first visible agent would be agent-2.
        var manager = CreateManager("thread-ordinal-rejected");

        var act = () => manager.SpawnAsync("no-such-template", "task", runInBackground: true);
        await act.Should().ThrowAsync<ArgumentException>();

        var id = ParseAgentId(await manager.SpawnAsync("worker", "task", runInBackground: true));
        id.Should().Be("agent-1");
    }

    [Fact]
    public async Task SpawnAsync_StampsTheChildThread_WithTheLaunchingConversationsOwnership()
    {
        // #385. A subagent-* thread id is minted inside the spawn, never through the HTTP
        // provisioning route that stamps ownership, so without this it persists with a null tenant
        // forever - and a null tenant reads as an absent row under Identity:Enforce, which makes
        // the sub-agent's transcript unreadable to the person whose conversation spawned it.
        var store = new InMemoryConversationStore();
        const string parent = "thread-conversation-owned";

        await store.SaveMetadataAsync(
            parent,
            new ThreadMetadata
            {
                ThreadId = parent,
                LastUpdated = 1_000,
                TenantId = "tnt_acme",
                OwnerUserId = "entra-tid:owner-oid",
                OwnerAppId = "codereview-daemon",
                Visibility = Visibility.Shared,
            }
        );

        var manager = CreateManager(parent, store);
        var agentId = ParseAgentId(await manager.SpawnAsync("worker", "do work", name: "a", runInBackground: true));
        manager.TryGetAgent(agentId, out var agent).Should().BeTrue();

        var metadata = await store.LoadMetadataAsync(agent!.ThreadId);

        metadata.Should().NotBeNull();
        metadata!.TenantId.Should().Be("tnt_acme");
        metadata.OwnerUserId.Should().Be("entra-tid:owner-oid");
        metadata.OwnerAppId.Should().Be("codereview-daemon");

        // Visibility is deliberately NOT inherited - see AgentThreadOwnership. Tenant and owner are
        // identity; "Shared" is a publication decision about the PARENT that nobody made about this
        // transcript.
        metadata.Visibility.Should().Be(Visibility.Private);

        // Allocating the ordinal must not disturb the root's identity columns either.
        var rootMetadata = await store.LoadMetadataAsync(parent);
        rootMetadata!.TenantId.Should().Be("tnt_acme");
        rootMetadata.Visibility.Should().Be(Visibility.Shared);
    }

    [Fact]
    public async Task SpawnAsync_FromAnUntenantedConversation_WritesNoOwnershipAtAll()
    {
        // Non-vacuity for the test above, and the enforcement-off path. A helper that stamped
        // unconditionally would satisfy those assertions while inventing metadata on every
        // deployment that never asked for identity.
        var store = new InMemoryConversationStore();
        var manager = CreateManager("thread-conversation-untenanted", store);

        var agentId = ParseAgentId(await manager.SpawnAsync("worker", "do work", name: "a", runInBackground: true));
        manager.TryGetAgent(agentId, out var agent).Should().BeTrue();

        var metadata = await store.LoadMetadataAsync(agent!.ThreadId);

        metadata?.TenantId.Should().BeNull();
        metadata?.OwnerUserId.Should().BeNull();
    }

    [Fact]
    public async Task Spawn_ProvenanceAwareStoreFactory_ResolvesFromThisManagersOwnParentAndRoster()
    {
        // #275: the provenance a child's store is stamped with must be resolved from the SPAWNING
        // manager, never captured once at the root. A manager whose own parent is itself a sub-agent —
        // the exact shape a grandchild's spawning manager has — must attribute the children IT spawns to
        // ITS OWN parent thread and resolve their snapshots from ITS OWN roster. A root-captured factory
        // would ignore both, which is why grandchildren were misattributed to the root and never resolved.
        const string nestedParentThread = "subagent-0123456789ab-agent-1";
        string? capturedChildThread = null;
        string? capturedParentThread = null;
        Func<string, SubAgentSnapshot?>? capturedDescribe = null;
        var childStore = new InMemoryConversationStore();

        var manager = CreateManager(
            nestedParentThread,
            provenanceFactory: (childThread, parentThread, describe) =>
            {
                capturedChildThread = childThread;
                capturedParentThread = parentThread;
                capturedDescribe = describe;
                return childStore;
            }
        );

        var agentId = ParseAgentId(await manager.SpawnAsync("worker", "do work", name: "a", runInBackground: true));
        manager.TryGetAgent(agentId, out var agent).Should().BeTrue();

        capturedParentThread
            .Should()
            .Be(
                nestedParentThread,
                "the child is attributed to THIS manager's own parent thread, not a root captured elsewhere"
            );
        capturedChildThread.Should().Be(agent!.ThreadId);

        capturedDescribe.Should().NotBeNull("the manager must hand the factory a live describe callback");
        var resolved = capturedDescribe!(agent.ThreadId);
        resolved
            .Should()
            .NotBeNull(
                "the child's snapshot resolves against THIS manager's live roster, so a grandchild is no "
                    + "longer a null snapshot on a manager it does not live in"
            );
        resolved!.AgentId.Should().Be(agentId);
    }

    [Fact]
    public void SubAgentThreadId_ReusesTheScopeOfANestedParent_AndDigestsARoot()
    {
        // The scope tag travels textually: a parent that is itself `subagent-{tag}-agent-N` hands the
        // SAME tag to its children, so the host can rebuild a grandchild's thread id from its parent's
        // without knowing the root. A root (any other id) is digested.
        var fromRoot = SubAgentManager.SubAgentThreadId("thread-root", "agent-1");
        var fromChild = SubAgentManager.SubAgentThreadId(fromRoot, "agent-2");

        fromRoot.Should().MatchRegex(ScopedThreadId);
        fromChild.Should().Be($"subagent-{TagOf(fromRoot)}-agent-2");
        SubAgentManager.SubAgentThreadId("thread-root", "agent-1").Should().Be(fromRoot, "the digest is deterministic");
        TagOf(SubAgentManager.SubAgentThreadId("thread-other", "agent-1")).Should().NotBe(TagOf(fromRoot));
    }

    /// <summary>The 12-hex scope segment of a <c>subagent-{tag}-agent-N</c> thread id.</summary>
    private static string TagOf(string threadId) => threadId.Split('-')[1];

    private static Task SaveRowAsync(IConversationStore store, string threadId) =>
        store.SaveMetadataAsync(threadId, new ThreadMetadata { ThreadId = threadId, LastUpdated = 0 });

    private static async Task<InMemoryConversationStore> StoreWithRootAsync(string rootThreadId)
    {
        var store = new InMemoryConversationStore();
        await store.SaveMetadataAsync(
            rootThreadId,
            new ThreadMetadata { ThreadId = rootThreadId, LastUpdated = 1_000 }
        );
        return store;
    }

    private SubAgentManager CreateManager(
        string parentThreadId,
        IConversationStore? store = null,
        Func<string, string?, Func<string, SubAgentSnapshot?>, IConversationStore>? provenanceFactory = null,
        SubAgentOptions? options = null
    )
    {
        var parentMock = new Mock<IMultiTurnAgent>();
        parentMock.Setup(p => p.ThreadId).Returns(parentThreadId);
        parentMock
            .Setup(p =>
                p.SendAsync(
                    It.IsAny<List<IMessage>>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new SendReceipt("receipt-1", null, DateTimeOffset.UtcNow));

        var provider = new Mock<IStreamingAgent>();
        provider
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(
                (IEnumerable<IMessage> _, GenerateReplyOptions? _, CancellationToken _) =>
                    Task.FromResult(SingleMessage(new TextMessage { Text = "done", Role = Role.Assistant }))
            );

        options ??= new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["worker"] = new SubAgentTemplate
                {
                    SystemPrompt = "You are a test agent.",
                    AgentFactory = () => provider.Object,
                },
                ["reviewer"] = new SubAgentTemplate
                {
                    SystemPrompt = "You review.",
                    AgentFactory = () => provider.Object,
                },
                ["plugin:reviewer"] = new SubAgentTemplate
                {
                    SystemPrompt = "You review.",
                    AgentFactory = () => provider.Object,
                },
            },
            MaxConcurrentSubAgents = 5,
            DefaultConversationStoreFactory = store is null ? null : _ => store,
            ProvenanceAwareConversationStoreFactory = provenanceFactory,
        };

        var manager = new SubAgentManager(
            parentAgent: parentMock.Object,
            parentContracts: [],
            parentHandlers: new Dictionary<string, ToolHandler>(),
            options: options,
            source: new MutableSubAgentTemplateSource(options.Templates)
        );
        _managers.Add(manager);
        return manager;
    }

    private static string ParseAgentId(string spawnJson)
    {
        using var doc = JsonDocument.Parse(spawnJson);
        return doc.RootElement.GetProperty("agent_id").GetString()!;
    }

    private static async IAsyncEnumerable<IMessage> SingleMessage(IMessage message)
    {
        yield return message;
        await Task.Yield();
    }
}
