using AchieveAi.LmDotnetTools.LmTestUtils;
using System.Text.Json;
using System.Text.RegularExpressions;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
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

        TagOf(idA).Should().NotBe(TagOf(idB),
            "sub-agents launched from different conversations must carry different conversation tags");
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

        TagOf(id1).Should().Be(TagOf(id2),
            "the conversation tag is deterministic from the launching conversation, so it is stable within it");
        GuidOf(id1).Should().NotBe(GuidOf(id2), "each spawn still gets its own unique guid");

        // The subagent-{id} thread reconstruction must stay intact so persistence/live-subscribe keep working.
        manager.TryGetAgent(id1, out var agent).Should().BeTrue();
        agent!.ThreadId.Should().Be($"subagent-{id1}");
    }

    private static string GuidOf(string agentId) => agentId.Split('-')[0];

    private static string TagOf(string agentId) => agentId.Split('-')[1];

    private SubAgentManager CreateManager(string parentThreadId)
    {
        var parentMock = new Mock<IMultiTurnAgent>();
        parentMock.Setup(p => p.ThreadId).Returns(parentThreadId);
        parentMock
            .Setup(p => p.SendAsync(
                It.IsAny<List<IMessage>>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendReceipt("receipt-1", null, DateTimeOffset.UtcNow));

        var provider = new Mock<IStreamingAgent>();
        provider
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<IMessage> _, GenerateReplyOptions? _, CancellationToken _) =>
                Task.FromResult(SingleMessage(new TextMessage { Text = "done", Role = Role.Assistant })));

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
        };

        var manager = new SubAgentManager(
            parentAgent: parentMock.Object,
            parentContracts: [],
            parentHandlers: new Dictionary<string, ToolHandler>(),
            options: options,
            source: new MutableSubAgentTemplateSource(options.Templates));
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
