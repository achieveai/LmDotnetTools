using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Tests.Agents;
using LmStreaming.Sample.Tests.TestDoubles;

namespace LmStreaming.Sample.Tests.Controllers;

/// <summary>
/// Tests for the read-only <c>GET /api/conversations/{threadId}/subagents</c> endpoint (WI #194,
/// Task 3). The action is presentation-only: it projects <c>SubAgentManager.ListAgents()</c>
/// snapshots into <see cref="SubAgentSummary"/> DTOs and never touches sub-agent execution.
/// It answers from the live manager UNION the children reconstructed from persisted
/// <see cref="SubAgentProvenance"/>, so a link to a conversation whose run has ended still lists
/// its sub-agents.
/// </summary>
public sealed class ConversationsControllerSubAgentsTests
{
    /// <summary>
    /// ASP.NET Core MVC's <c>AddControllers()</c> defaults <c>JsonOptions</c> to
    /// <see cref="JsonSerializerDefaults.Web"/> (camelCase property names), unlike
    /// <see cref="JsonSerializer.Serialize{T}(T, JsonSerializerOptions?)"/>'s own reflection-based
    /// default (exact declared casing). Tests that inspect actual wire property names must serialize
    /// with this to match what a real client parsing the HTTP response would see.
    /// </summary>
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    private static ConversationsController CreateController(
        MultiTurnAgentPool pool,
        IConversationStore? store = null)
    {
        store ??= new InMemoryConversationStore();
        return new ConversationsController(
            store,
            pool,
            Mock.Of<IChatModeStore>(),
            Mock.Of<IWorkspaceStore>(),
            new FakeProviderRegistry(defaultProviderId: "test", available: ["test"]).ToReal(),
            new ConversationStatusResolver(Mock.Of<IConversationStore>(), new InMemoryConversationStore()),
            NullLogger<ConversationsController>.Instance);
    }

    /// <summary>
    /// Persists thread metadata carrying the provenance a spawned child's store would have stamped,
    /// including the exact terminal status/timestamp the manager pushes causally at completion
    /// (Task 1) — <paramref name="lastUpdated"/> doubles as the terminal instant here so ordering by
    /// activity stays deterministic.
    /// </summary>
    private static async Task SeedPersistedChildAsync(
        IConversationStore store,
        string parentThreadId,
        string agentId,
        string name,
        string template,
        string task,
        long lastUpdated)
    {
        var childThreadId = $"{SubAgentProvenance.ThreadIdPrefix}{agentId}";
        await store.SaveMetadataAsync(
            childThreadId,
            new ThreadMetadata
            {
                ThreadId = childThreadId,
                LastUpdated = lastUpdated,
                Properties = SubAgentProvenance.Build(
                    parentThreadId,
                    new SubAgentSnapshot(
                        AgentId: agentId,
                        Name: name,
                        TemplateName: template,
                        Task: task,
                        Status: SubAgentStatus.Completed,
                        ThreadId: childThreadId,
                        LastActivityUtc: DateTimeOffset.FromUnixTimeMilliseconds(lastUpdated),
                        TerminalAtUtc: DateTimeOffset.FromUnixTimeMilliseconds(lastUpdated))),
            });
    }

    private static MultiTurnAgentPool CreateFakeAgentPool()
    {
        return new MultiTurnAgentPool(
            (threadId, _, _) => new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(threadId)),
            NullLogger<MultiTurnAgentPool>.Instance);
    }

    private static MultiTurnAgentPool CreatePoolReturning(IMultiTurnAgent agent)
    {
        return new MultiTurnAgentPool(
            (_, _, _) => new MultiTurnAgentPool.AgentCreationResult(agent),
            NullLogger<MultiTurnAgentPool>.Instance);
    }

    [Fact]
    public async Task ListSubAgents_Returns404_ForUnknownParentThread()
    {
        await using var pool = CreateFakeAgentPool();
        var controller = CreateController(pool);

        var result = await controller.ListSubAgents("does-not-exist");

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var payload = JsonSerializer.Serialize(notFound.Value);
        payload.Should().Contain("unknown_thread");
        payload.Should().Contain("does-not-exist");
    }

    /// <summary>
    /// The recursive contract must 404 for a thread that is unknown everywhere (not live, not
    /// persisted, and not referenced as anyone's parent) exactly like the flat listing does — an
    /// empty subtree alone does not justify 404, so <c>BuildDescendantTreeAsync</c> only checks
    /// existence when the traversal discovers zero descendants (mirrors
    /// <see cref="ListSubAgents_Returns404_ForUnknownParentThread"/> for the recursive path).
    /// </summary>
    [Fact]
    public async Task ListSubAgents_Recursive_UnknownThread_ReturnsNotFound()
    {
        await using var pool = CreateFakeAgentPool();
        var controller = CreateController(pool);

        var result = await controller.ListSubAgents("does-not-exist", recursive: true);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var payload = JsonSerializer.Serialize(notFound.Value);
        payload.Should().Contain("unknown_thread");
        payload.Should().Contain("does-not-exist");
    }

    [Fact]
    public async Task ListSubAgents_ReturnsEmptyArray_WhenAgentHasNoSubAgentManager()
    {
        await using var pool = CreateFakeAgentPool();
        var threadId = "thread-no-subagents";
        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        _ = pool.GetOrCreateAgent(threadId, mode);

        var controller = CreateController(pool);

        var result = await controller.ListSubAgents(threadId);

        var ok = Assert.IsType<OkObjectResult>(result);
        var summaries = Assert.IsAssignableFrom<IReadOnlyCollection<SubAgentSummary>>(ok.Value);
        summaries.Should().BeEmpty();
    }

    [Fact]
    public async Task ListSubAgents_ReturnsSnapshots_ForSpawnedChildren()
    {
        var threadId = "thread-with-subagents";

        var registry = new FunctionRegistry();
        var subAgentOptions = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["worker"] = new SubAgentTemplate
                {
                    Name = "worker",
                    SystemPrompt = "You are a worker.",
                    // Blocking provider keeps each spawned child in the Running state deterministically.
                    AgentFactory = () => BlockingProvider(),
                },
            },
            MaxConcurrentSubAgents = 5,
        };

        await using var loop = new MultiTurnAgentLoop(
            BlockingProvider(),
            registry,
            threadId: threadId,
            subAgentOptions: subAgentOptions);

        await using var pool = CreatePoolReturning(loop);
        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        _ = pool.GetOrCreateAgent(threadId, mode);

        var alphaJson = await loop.SubAgentManager!.SpawnAsync(
            "worker", "first task", name: "alpha", runInBackground: true);
        var betaJson = await loop.SubAgentManager!.SpawnAsync(
            "worker", "second task", name: "beta", runInBackground: true);

        var alphaId = ParseAgentId(alphaJson);
        var betaId = ParseAgentId(betaJson);

        var controller = CreateController(pool);

        var result = await controller.ListSubAgents(threadId);

        var ok = Assert.IsType<OkObjectResult>(result);
        var summaries = Assert.IsAssignableFrom<IReadOnlyCollection<SubAgentSummary>>(ok.Value).ToList();
        summaries.Should().HaveCount(2);

        var alpha = summaries.Single(s => s.AgentId == alphaId);
        alpha.Name.Should().Be("alpha");
        alpha.Template.Should().Be("worker");
        alpha.Task.Should().Be("first task");
        alpha.Status.Should().Be("running");
        alpha.ThreadId.Should().Be($"subagent-{alphaId}");

        var beta = summaries.Single(s => s.AgentId == betaId);
        beta.Name.Should().Be("beta");
        beta.Template.Should().Be("worker");
        beta.Task.Should().Be("second task");
        beta.Status.Should().Be("running");
        beta.ThreadId.Should().Be($"subagent-{betaId}");
    }

    /// <summary>
    /// The deep-link case: the review run has ended and the parent agent is gone from the pool, but a
    /// human following the posted link must still see which sub-agents ran. Nothing is live here — the
    /// whole roster has to come back from the store.
    /// </summary>
    [Fact]
    public async Task ListSubAgents_ReturnsPersistedChildren_WhenParentIsNotInThePool()
    {
        const string parentThreadId = "thread-finished-review";
        var store = new InMemoryConversationStore();
        await store.SaveMetadataAsync(
            parentThreadId,
            new ThreadMetadata { ThreadId = parentThreadId, LastUpdated = 1_000 });
        await SeedPersistedChildAsync(
            store, parentThreadId, "aaa", "alpha", "code-reviewer:security", "check auth", 2_000);
        await SeedPersistedChildAsync(
            store, parentThreadId, "bbb", "beta", "code-reviewer:performance", "check hot path", 3_000);

        await using var pool = CreateFakeAgentPool();
        var controller = CreateController(pool, store);

        var result = await controller.ListSubAgents(parentThreadId);

        var ok = Assert.IsType<OkObjectResult>(result);
        var summaries = Assert.IsAssignableFrom<IReadOnlyCollection<SubAgentSummary>>(ok.Value).ToList();
        summaries.Should().HaveCount(2);

        // Newest activity first.
        summaries[0].AgentId.Should().Be("bbb");
        summaries[0].Template.Should().Be("code-reviewer:performance");
        summaries[0].Task.Should().Be("check hot path");
        summaries[0].ThreadId.Should().Be("subagent-bbb");
        summaries[0].Status.Should().Be("completed",
            "the manager pushes the exact terminal status causally at completion (Task 1), so a " +
            "reconstructed child now reports its real outcome instead of a placeholder");

        summaries[1].AgentId.Should().Be("aaa");
        summaries[1].Name.Should().Be("alpha");
    }

    [Fact]
    public async Task ListSubAgents_ExcludesChildrenOfOtherConversations()
    {
        const string parentThreadId = "thread-mine";
        var store = new InMemoryConversationStore();
        await store.SaveMetadataAsync(
            parentThreadId,
            new ThreadMetadata { ThreadId = parentThreadId, LastUpdated = 1_000 });
        await SeedPersistedChildAsync(
            store, parentThreadId, "mine", "mine", "worker", "my task", 2_000);
        await SeedPersistedChildAsync(
            store, "thread-someone-else", "theirs", "theirs", "worker", "their task", 3_000);

        await using var pool = CreateFakeAgentPool();
        var controller = CreateController(pool, store);

        var result = await controller.ListSubAgents(parentThreadId);

        var ok = Assert.IsType<OkObjectResult>(result);
        var summaries = Assert.IsAssignableFrom<IReadOnlyCollection<SubAgentSummary>>(ok.Value).ToList();
        summaries.Should().ContainSingle().Which.AgentId.Should().Be("mine");
    }

    /// <summary>
    /// A child that is both live and persisted appears ONCE, described by the live manager — it is the
    /// authority while it exists and is the only source of real lifecycle status.
    /// </summary>
    [Fact]
    public async Task ListSubAgents_PrefersLiveSnapshot_OverPersistedCopyOfSameChild()
    {
        const string threadId = "thread-live-and-persisted";

        var subAgentOptions = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["worker"] = new SubAgentTemplate
                {
                    Name = "worker",
                    SystemPrompt = "You are a worker.",
                    AgentFactory = () => BlockingProvider(),
                },
            },
            MaxConcurrentSubAgents = 5,
        };

        await using var loop = new MultiTurnAgentLoop(
            BlockingProvider(),
            new FunctionRegistry(),
            threadId: threadId,
            subAgentOptions: subAgentOptions);

        await using var pool = CreatePoolReturning(loop);
        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        _ = pool.GetOrCreateAgent(threadId, mode);

        var agentId = ParseAgentId(await loop.SubAgentManager!.SpawnAsync(
            "worker", "live task", name: "live", runInBackground: true));

        var store = new InMemoryConversationStore();
        await SeedPersistedChildAsync(
            store, threadId, agentId, "stale", "worker", "stale task", 9_000);

        var controller = CreateController(pool, store);

        var result = await controller.ListSubAgents(threadId);

        var ok = Assert.IsType<OkObjectResult>(result);
        var summary = Assert.IsAssignableFrom<IReadOnlyCollection<SubAgentSummary>>(ok.Value)
            .Should().ContainSingle().Subject;
        summary.AgentId.Should().Be(agentId);
        summary.Name.Should().Be("live");
        summary.Task.Should().Be("live task");
        summary.Status.Should().Be("running");
    }

    /// <summary>
    /// Persists a child's provenance directly with caller-supplied property overrides, bypassing
    /// <see cref="SeedPersistedChildAsync"/>'s live-snapshot shape. Used to build graphs
    /// <see cref="SubAgentManager"/> itself could never produce today (grandchildren, cycles,
    /// legacy unstamped status) — these fixtures exist to prove the recursive graph READER is
    /// correct, not to model any current live spawn path (nested Agent delegation stays disabled).
    /// </summary>
    private static async Task SeedRawProvenanceChildAsync(
        IConversationStore store,
        string childThreadId,
        string parentThreadId,
        long lastUpdated,
        string? status = "completed",
        string? name = null,
        string? template = "worker")
    {
        var builder = ImmutableDictionary.CreateBuilder<string, object>(StringComparer.Ordinal);
        builder[SubAgentProvenance.ParentThreadIdKey] = parentThreadId;
        if (name is not null)
        {
            builder[SubAgentProvenance.NameKey] = name;
        }

        if (template is not null)
        {
            builder[SubAgentProvenance.TemplateKey] = template;
        }

        if (status is not null)
        {
            builder[SubAgentProvenance.StatusKey] = status;
        }

        await store.SaveMetadataAsync(
            childThreadId,
            new ThreadMetadata
            {
                ThreadId = childThreadId,
                LastUpdated = lastUpdated,
                Properties = builder.ToImmutable(),
            });
    }

    /// <summary>
    /// Root→child→grandchild: the reader must follow persisted parent links transitively, not just
    /// one hop. No live code path spawns a grandchild today (nested Agent delegation is disabled) —
    /// this fixture is synthetic and validates the graph-reader's traversal/depth bookkeeping only.
    /// </summary>
    [Fact]
    public async Task ListSubAgents_Recursive_ReturnsRootChildGrandchild_WithDepthAndParentIds()
    {
        const string rootThreadId = "thread-root";
        var store = new InMemoryConversationStore();
        await store.SaveMetadataAsync(
            rootThreadId,
            new ThreadMetadata { ThreadId = rootThreadId, LastUpdated = 1_000 });
        await SeedRawProvenanceChildAsync(
            store, "subagent-child", rootThreadId, 2_000, name: "child");
        await SeedRawProvenanceChildAsync(
            store, "subagent-grandchild", "subagent-child", 3_000, name: "grandchild");

        await using var pool = CreateFakeAgentPool();
        var controller = CreateController(pool, store);

        var result = await controller.ListSubAgents(rootThreadId, recursive: true);

        var ok = Assert.IsType<OkObjectResult>(result);
        var tree = Assert.IsType<SubAgentTreeResponse>(ok.Value);
        tree.SchemaVersion.Should().Be(1);
        tree.Nodes.Should().HaveCount(2);

        var child = tree.Nodes.Single(n => n.ThreadId == "subagent-child");
        child.ParentThreadId.Should().Be(rootThreadId);
        child.Depth.Should().Be(1);

        var grandchild = tree.Nodes.Single(n => n.ThreadId == "subagent-grandchild");
        grandchild.ParentThreadId.Should().Be("subagent-child");
        grandchild.Depth.Should().Be(2);
    }

    /// <summary>
    /// Every persisted node stamps exactly one <c>ParentThreadId</c> (last write wins), so a
    /// reachable "revisit" cannot come from two nodes each naming the other as parent — that pair
    /// would simply be unreachable from an unrelated root. The only way a cycle is BOTH reachable
    /// AND causes a repeat visit is for the request root itself to sit on its own descendant chain:
    /// here the root's own persisted parent is "b", closing the loop root → a → b → (root, cut).
    /// This can never arise from a real spawn (nested delegation is disabled) — the fixture proves
    /// the reader's visited-set guard terminates and does not surface the root as a duplicate node.
    /// </summary>
    [Fact]
    public async Task ListSubAgents_Recursive_CutsCycles()
    {
        const string rootThreadId = "subagent-root-cycle";
        var store = new InMemoryConversationStore();
        // The root is itself a stamped sub-agent whose persisted parent is "b" — the loop-closing
        // edge. Only reachable/inspected once BFS reaches "b"; never emitted as a discovered node.
        await SeedRawProvenanceChildAsync(store, rootThreadId, "subagent-b", 1_000);
        await SeedRawProvenanceChildAsync(store, "subagent-a", rootThreadId, 2_000);
        await SeedRawProvenanceChildAsync(store, "subagent-b", "subagent-a", 3_000);

        await using var pool = CreateFakeAgentPool();
        var controller = CreateController(pool, store);

        var result = await controller.ListSubAgents(rootThreadId, recursive: true);

        var ok = Assert.IsType<OkObjectResult>(result);
        var tree = Assert.IsType<SubAgentTreeResponse>(ok.Value);
        tree.Nodes.Should().HaveCount(2, "the cycle back to the root must be cut, not duplicated");
        tree.Nodes.Select(n => n.ThreadId).Should().BeEquivalentTo(["subagent-a", "subagent-b"]);
    }

    /// <summary>
    /// A terminal (completed) child still has its own persisted child; the reader follows
    /// structural parent links regardless of an ancestor's lifecycle status.
    /// </summary>
    [Fact]
    public async Task ListSubAgents_Recursive_TraversesThroughTerminalAncestor()
    {
        const string rootThreadId = "thread-root-terminal";
        var store = new InMemoryConversationStore();
        await store.SaveMetadataAsync(
            rootThreadId,
            new ThreadMetadata { ThreadId = rootThreadId, LastUpdated = 1_000 });
        await SeedRawProvenanceChildAsync(
            store, "subagent-terminal-child", rootThreadId, 2_000, status: "completed");
        await SeedRawProvenanceChildAsync(
            store, "subagent-grandchild-of-terminal", "subagent-terminal-child", 3_000, status: "running");

        await using var pool = CreateFakeAgentPool();
        var controller = CreateController(pool, store);

        var result = await controller.ListSubAgents(rootThreadId, recursive: true);

        var ok = Assert.IsType<OkObjectResult>(result);
        var tree = Assert.IsType<SubAgentTreeResponse>(ok.Value);
        tree.Nodes.Should().Contain(n => n.ThreadId == "subagent-grandchild-of-terminal" && n.Depth == 2);
    }

    /// <summary>
    /// Metadata that predates the status stamp (only the parent link is present — the shape any
    /// pre-Task-1 persisted child would have) must report <c>unknown</c>, never a guessed status.
    /// </summary>
    [Fact]
    public async Task ListSubAgents_Recursive_MapsUnstampedLegacyChildStatus_ToUnknown()
    {
        const string rootThreadId = "thread-root-legacy";
        var store = new InMemoryConversationStore();
        await store.SaveMetadataAsync(
            rootThreadId,
            new ThreadMetadata { ThreadId = rootThreadId, LastUpdated = 1_000 });
        await SeedRawProvenanceChildAsync(
            store, "subagent-legacy", rootThreadId, 2_000, status: null);

        await using var pool = CreateFakeAgentPool();
        var controller = CreateController(pool, store);

        var result = await controller.ListSubAgents(rootThreadId, recursive: true);

        var ok = Assert.IsType<OkObjectResult>(result);
        var tree = Assert.IsType<SubAgentTreeResponse>(ok.Value);
        tree.Nodes.Should().ContainSingle().Which.Status.Should().Be("unknown");
    }

    /// <summary>
    /// The recursive contract's required relationship fields must round-trip through JSON even when
    /// several of them have no value yet (schema v1 guarantees the KEY is present, not that it is
    /// populated) — a consumer parsing the wire payload can rely on every key existing.
    /// </summary>
    [Fact]
    public async Task ListSubAgents_Recursive_IncludesRequiredRelationshipFields()
    {
        const string rootThreadId = "thread-root-fields";
        var store = new InMemoryConversationStore();
        await store.SaveMetadataAsync(
            rootThreadId,
            new ThreadMetadata { ThreadId = rootThreadId, LastUpdated = 1_000 });
        await SeedRawProvenanceChildAsync(store, "subagent-fields", rootThreadId, 2_000);

        await using var pool = CreateFakeAgentPool();
        var controller = CreateController(pool, store);

        var result = await controller.ListSubAgents(rootThreadId, recursive: true);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value, WebJsonOptions);
        using var doc = JsonDocument.Parse(json);
        var node = doc.RootElement.GetProperty("nodes")[0];
        foreach (var requiredField in new[]
        {
            "agentId", "threadId", "parentThreadId", "name", "template", "status", "depth",
            "terminalAtUtc", "failureCode",
        })
        {
            node.TryGetProperty(requiredField, out _).Should()
                .BeTrue($"'{requiredField}' must be present on every recursive node");
        }
    }

    /// <summary>
    /// The pre-existing endpoint shape (no query string) is a compatibility contract: it must keep
    /// returning a plain array of <see cref="SubAgentSummary"/>, never the new envelope, so the
    /// current UI panel and any other caller of the flat endpoint are unaffected by this task.
    /// </summary>
    [Fact]
    public async Task ListSubAgents_NonRecursive_RetainsFlatArrayShape()
    {
        const string parentThreadId = "thread-compat";
        var store = new InMemoryConversationStore();
        await store.SaveMetadataAsync(
            parentThreadId,
            new ThreadMetadata { ThreadId = parentThreadId, LastUpdated = 1_000 });
        await SeedPersistedChildAsync(
            store, parentThreadId, "aaa", "alpha", "code-reviewer:security", "check auth", 2_000);

        await using var pool = CreateFakeAgentPool();
        var controller = CreateController(pool, store);

        var result = await controller.ListSubAgents(parentThreadId);

        var ok = Assert.IsType<OkObjectResult>(result);
        var summaries = Assert.IsAssignableFrom<IReadOnlyCollection<SubAgentSummary>>(ok.Value);
        summaries.Should().ContainSingle().Which.AgentId.Should().Be("aaa");
    }

    /// <summary>
    /// Nodes must sort by (depth, parentThreadId, threadId) so the client renders a stable tree
    /// regardless of store scan order.
    /// </summary>
    [Fact]
    public async Task ListSubAgents_Recursive_OrdersNodesByDepthThenParentThenThreadId()
    {
        const string rootThreadId = "thread-root-order";
        var store = new InMemoryConversationStore();
        await store.SaveMetadataAsync(
            rootThreadId,
            new ThreadMetadata { ThreadId = rootThreadId, LastUpdated = 1_000 });
        // Two depth-1 children seeded out of alpha order.
        await SeedRawProvenanceChildAsync(store, "subagent-zeta", rootThreadId, 2_000);
        await SeedRawProvenanceChildAsync(store, "subagent-alpha", rootThreadId, 3_000);
        // A depth-2 grandchild under "zeta".
        await SeedRawProvenanceChildAsync(store, "subagent-grand", "subagent-zeta", 4_000);

        await using var pool = CreateFakeAgentPool();
        var controller = CreateController(pool, store);

        var result = await controller.ListSubAgents(rootThreadId, recursive: true);

        var ok = Assert.IsType<OkObjectResult>(result);
        var tree = Assert.IsType<SubAgentTreeResponse>(ok.Value);
        tree.Nodes.Select(n => n.ThreadId).Should().ContainInOrder(
            "subagent-alpha", "subagent-zeta", "subagent-grand");
    }

    private static string ParseAgentId(string spawnJson)
    {
        using var doc = JsonDocument.Parse(spawnJson);
        return doc.RootElement.GetProperty("agent_id").GetString()!;
    }

    /// <summary>
    /// A provider whose stream never yields and only unwinds on cancellation — keeps a spawned
    /// child's run in progress (Running) without any timing dependence.
    /// </summary>
    private static IStreamingAgent BlockingProvider()
    {
        var provider = new Mock<IStreamingAgent>();
        provider
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<IMessage> _, GenerateReplyOptions? _, CancellationToken ct) =>
                Task.FromResult(BlockingStream(ct)));
        return provider.Object;
    }

    private static async IAsyncEnumerable<IMessage> BlockingStream(
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        yield break;
    }
}
