using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests.SubAgents;

/// <summary>
/// Unit tests for the <see cref="SubAgentManager.CheckAgents"/> method:
/// batch observation of sub-agents with ordered ID/name resolution, duplicates,
/// and unknown agent handling.
/// </summary>
public class SubAgentManagerBatchObservationTests : IAsyncLifetime
{
    private readonly Mock<IMultiTurnAgent> _parentMock = new();
    private readonly Mock<IStreamingAgent> _subAgentMock = new();
    private SubAgentManager? _manager;

    public Task InitializeAsync()
    {
        // Default parent mock: accept any SendAsync call
        _parentMock
            .Setup(p =>
                p.SendAsync(
                    It.IsAny<List<IMessage>>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new SendReceipt("receipt-1", null, DateTimeOffset.UtcNow));

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_manager != null)
        {
            // Bounded: an unbounded teardown turns one stalled test into an aborted run (#362).
            await Wait.ForTeardownAsync(_manager, "the sub-agent manager under test");
        }
    }

    [Fact]
    public async Task CheckAgents_PreservesInputOrderDuplicatesAndUnknowns()
    {
        var manager = CreateManager();
        var first = await SpawnBackgroundAsync(manager, name: "first-reviewer");
        var second = await SpawnBackgroundAsync(manager, name: "second-reviewer");

        var batch = manager.CheckAgents([second, "first-reviewer", "missing", second]);

        batch.Entries.Select(x => x.Target).Should().Equal(second, "first-reviewer", "missing", second);
        batch.Entries.Select(x => x.AgentId).Should().Equal(second, first, null, second);
        batch.Entries[2].Status.Should().Be("not_found");
        batch.Requested.Should().Be(4);
        batch.NotFound.Should().Be(1);
    }

    [Fact]
    public async Task CheckAgents_NameResolutionIsOrdinalCaseSensitive()
    {
        var manager = CreateManager();
        await SpawnBackgroundAsync(manager, name: "ReviewOne");

        var batch = manager.CheckAgents(["reviewone"]);

        batch.Entries.Should().ContainSingle(x => x.Status == "not_found");
    }

    [Fact]
    public async Task CheckAgents_ReturnsStatusForSpawnedAgent()
    {
        var manager = CreateManager();
        var agentId = await SpawnBackgroundAsync(manager, name: "test-agent");

        var batch = manager.CheckAgents([agentId]);

        batch.Entries.Should().ContainSingle();
        var entry = batch.Entries[0];
        entry.Target.Should().Be(agentId);
        entry.AgentId.Should().Be(agentId);
        entry.Name.Should().Be("test-agent");
        // Status could be running or completed depending on timing
        entry.Status.Should().BeOneOf("running", "completed");
        entry.IsFound.Should().BeTrue();
    }

    [Fact]
    public async Task CheckAgents_ReturnsTemplateAndTaskInfo()
    {
        var manager = CreateManager();
        var agentId = await SpawnBackgroundAsync(manager, name: "worker");

        var batch = manager.CheckAgents([agentId]);

        var entry = batch.Entries[0];
        entry.TemplateName.Should().Be("test-agent");
        entry.Task.Should().Be("Do some work");
    }

    [Fact]
    public async Task CheckAgents_SummaryCountsAreAccurate()
    {
        var manager = CreateManager();
        var first = await SpawnBackgroundAsync(manager, name: "agent-1");
        var second = await SpawnBackgroundAsync(manager, name: "agent-2");

        var batch = manager.CheckAgents([first, "unknown1", second, "unknown2", first]);

        batch.Requested.Should().Be(5);
        batch.NotFound.Should().Be(2);
        // The three non-unknown entries (first, second, first) will be running or completed
        // We just verify that found entries don't count as not_found
        batch.Entries.Where(e => e.IsFound).Should().HaveCount(3);
    }

    [Fact]
    public void CheckAgents_EmptyInputReturnsEmptyBatch()
    {
        var manager = CreateManager();

        var batch = manager.CheckAgents([]);

        batch.Entries.Should().BeEmpty();
        batch.Requested.Should().Be(0);
        batch.NotFound.Should().Be(0);
        batch.Running.Should().Be(0);
    }

    #region Helpers

    /// <summary>
    /// Spawns a background sub-agent and returns the spawned agent's id.
    /// </summary>
    private async Task<string> SpawnBackgroundAsync(SubAgentManager manager, string name)
    {
        SetupSubAgentResponse([new TextMessage { Text = "working...", Role = Role.Assistant }]);

        var spawnJson = await manager.SpawnAsync("test-agent", "Do some work", name: name, runInBackground: true);

        using var spawnDoc = JsonDocument.Parse(spawnJson);
        var agentId = spawnDoc.RootElement.GetProperty("agent_id").GetString()!;

        return agentId;
    }

    /// <summary>
    /// Creates a SubAgentManager with the test's mock sub-agent and parent.
    /// </summary>
    private SubAgentManager CreateManager(int maxConcurrent = 5)
    {
        var options = CreateOptions(maxConcurrent);
        _manager = new SubAgentManager(
            parentAgent: _parentMock.Object,
            parentContracts: [],
            parentHandlers: new Dictionary<string, ToolHandler>(),
            options: options,
            source: new MutableSubAgentTemplateSource(options.Templates)
        );
        return _manager;
    }

    /// <summary>
    /// Creates SubAgentOptions with a single "test-agent" template
    /// backed by the mock sub-agent.
    /// </summary>
    private SubAgentOptions CreateOptions(int maxConcurrent = 5)
    {
        var template = new SubAgentTemplate
        {
            SystemPrompt = "You are a test agent.",
            AgentFactory = () => _subAgentMock.Object,
        };

        return new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate> { ["test-agent"] = template },
            MaxConcurrentSubAgents = maxConcurrent,
        };
    }

    /// <summary>
    /// Configures the mock sub-agent to return the given messages from
    /// GenerateReplyStreamingAsync on every call.
    /// </summary>
    private void SetupSubAgentResponse(List<IMessage> messages)
    {
        _subAgentMock
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(Task.FromResult(ToAsyncEnumerable(messages)));
    }

    /// <summary>
    /// Converts a list of messages to an IAsyncEnumerable for mock setup.
    /// </summary>
    private static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(
        List<IMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        foreach (var msg in messages)
        {
            ct.ThrowIfCancellationRequested();
            yield return msg;
            await Task.Yield();
        }
    }

    #endregion
}
