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
/// A spawned sub-agent's id must carry the same two-part identity the user asked for on every agent:
/// a human-friendly NAME plus a UNIQUE id whose uniqueness is derived from the LAUNCHING CONVERSATION,
/// so an agent is never mistaken for one born in a different conversation. The id is
/// <c>{guid}-{conversationTag}</c>: the guid keeps per-spawn uniqueness (unchanged), and the
/// conversation tag is a deterministic function of the parent agent's thread id — same conversation
/// reconstructs the SAME tag, different conversations get DIFFERENT tags. This is the sub-agent
/// counterpart to the workflow-controller conversation-scoping guard (WorkflowManagerConversationScopeTests
/// in the LmWorkflow test suite); the HITL decision was to apply the identity change to controller AND sub-agents.
/// </summary>
public class SubAgentConversationScopeTests : IAsyncLifetime
{
    private static readonly Regex GuidThenTag = new(@"^[0-9a-f]{12}-[0-9a-f]{8}$", RegexOptions.Compiled);

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
    public async Task SpawnAsync_DerivesAgentId_FromLaunchingConversation()
    {
        // Two DIFFERENT conversations (distinct parent thread ids) must yield sub-agent ids whose
        // conversation-tag suffix differs, so ids from separate conversations are never confused.
        var managerA = CreateManager("thread-conversation-alpha");
        var managerB = CreateManager("thread-conversation-beta");

        var idA = ParseAgentId(await managerA.SpawnAsync("worker", "do work", name: "a", runInBackground: true));
        var idB = ParseAgentId(await managerB.SpawnAsync("worker", "do work", name: "b", runInBackground: true));

        idA.Should().MatchRegex(GuidThenTag, "the id is a 12-hex guid plus an 8-hex conversation tag");
        idB.Should().MatchRegex(GuidThenTag, "the id is a 12-hex guid plus an 8-hex conversation tag");

        TagOf(idA)
            .Should()
            .NotBe(
                TagOf(idB),
                "sub-agents launched from different conversations must carry different conversation tags"
            );
        GuidOf(idA).Should().NotBe(GuidOf(idB), "the guid component is unique per spawn");
    }

    [Fact]
    public async Task SpawnAsync_SameConversation_SharesConversationTag_ButUniqueGuidPrefix()
    {
        // Within ONE conversation, the two-part identity is proven: every sub-agent shares the same
        // conversation tag (deterministic, derived from the conversation) while each keeps its own guid.
        var manager = CreateManager("thread-conversation-shared");

        var id1 = ParseAgentId(await manager.SpawnAsync("worker", "first", name: "one", runInBackground: true));
        var id2 = ParseAgentId(await manager.SpawnAsync("worker", "second", name: "two", runInBackground: true));

        TagOf(id1)
            .Should()
            .Be(
                TagOf(id2),
                "the conversation tag is deterministic from the launching conversation, so it is stable within it"
            );
        GuidOf(id1).Should().NotBe(GuidOf(id2), "each spawn still gets its own unique guid");

        // The subagent-{id} thread reconstruction must stay intact so persistence/live-subscribe keep working.
        manager.TryGetAgent(id1, out var agent).Should().BeTrue();
        agent!.ThreadId.Should().Be($"subagent-{id1}");
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

        var metadata = await store.LoadMetadataAsync($"subagent-{agentId}");

        metadata.Should().NotBeNull();
        metadata!.TenantId.Should().Be("tnt_acme");
        metadata.OwnerUserId.Should().Be("entra-tid:owner-oid");
        metadata.OwnerAppId.Should().Be("codereview-daemon");

        // Visibility is deliberately NOT inherited - see AgentThreadOwnership. Tenant and owner are
        // identity; "Shared" is a publication decision about the PARENT that nobody made about this
        // transcript.
        metadata.Visibility.Should().Be(Visibility.Private);
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

        var metadata = await store.LoadMetadataAsync($"subagent-{agentId}");

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
        const string nestedParentThread = "subagent-child-not-root";
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

        capturedParentThread
            .Should()
            .Be(
                nestedParentThread,
                "the child is attributed to THIS manager's own parent thread, not a root captured elsewhere"
            );
        capturedChildThread.Should().Be($"subagent-{agentId}");

        capturedDescribe.Should().NotBeNull("the manager must hand the factory a live describe callback");
        var resolved = capturedDescribe!($"subagent-{agentId}");
        resolved
            .Should()
            .NotBeNull(
                "the child's snapshot resolves against THIS manager's live roster, so a grandchild is no "
                    + "longer a null snapshot on a manager it does not live in"
            );
        resolved!.AgentId.Should().Be(agentId);
    }

    private static string GuidOf(string agentId) => agentId.Split('-')[0];

    private static string TagOf(string agentId) => agentId.Split('-')[1];

    private SubAgentManager CreateManager(
        string parentThreadId,
        IConversationStore? store = null,
        Func<string, string?, Func<string, SubAgentSnapshot?>, IConversationStore>? provenanceFactory = null
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

        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["worker"] = new SubAgentTemplate
                {
                    SystemPrompt = "You are a test agent.",
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
