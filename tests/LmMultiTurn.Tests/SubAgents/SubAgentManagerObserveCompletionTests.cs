using System.Runtime.CompilerServices;
using System.Text.Json;
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
/// Unit tests for the <see cref="SubAgentManager.ObserveCompletionAsync"/> /
/// <see cref="SubAgentManager.SetNotifyParentOnCompletion"/> seam: lets a host-side trigger
/// source (see #144) observe a background sub-agent's completion by id and toggle its
/// automatic parent-relay flag, without inventing new spawn setup beyond what
/// <c>SubAgentManagerTests</c> already establishes for this manager.
/// </summary>
public class SubAgentManagerObserveCompletionTests : IAsyncLifetime
{
    private readonly Mock<IMultiTurnAgent> _parentMock = new();
    private readonly Mock<IStreamingAgent> _subAgentMock = new();
    private SubAgentManager? _manager;

    public Task InitializeAsync()
    {
        // Default parent mock: accept any SendAsync call
        _parentMock
            .Setup(p => p.SendAsync(
                It.IsAny<List<IMessage>>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendReceipt("receipt-1", null, DateTimeOffset.UtcNow));

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_manager != null)
        {
            await _manager.DisposeAsync();
        }
    }

    [Fact]
    public async Task ObserveCompletionAsync_ReturnsResult_WhenSubAgentCompletes()
    {
        // Arrange a manager with a spawned sub-agent whose run completes with text "done".
        var (manager, agentId) = await SpawnCompletingSubAgentAsync(result: "done");

        var observed = await manager.ObserveCompletionAsync(agentId, CancellationToken.None);

        observed.Should().Be("done");
    }

    [Fact]
    public async Task ObserveCompletionAsync_Throws_ForUnknownId()
    {
        var manager = BuildEmptyManager();
        var act = () => manager.ObserveCompletionAsync("nope", CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void SetNotifyParentOnCompletion_Throws_ForUnknownId()
    {
        var manager = BuildEmptyManager();
        var act = () => manager.SetNotifyParentOnCompletion("nope", false);
        act.Should().Throw<ArgumentException>();
    }

    #region Helpers

    /// <summary>
    /// Spawns a background sub-agent whose run completes with the given final text, mirroring
    /// the spawn setup in <c>SubAgentManagerTests.SpawnAsync_Background_ReturnsSpawnReceipt</c>.
    /// Returns the manager (tracked for disposal) and the spawned agent's id.
    /// </summary>
    private async Task<(SubAgentManager Manager, string AgentId)> SpawnCompletingSubAgentAsync(
        string result)
    {
        SetupSubAgentResponse([
            new TextMessage { Text = result, Role = Role.Assistant },
        ]);

        var manager = CreateManager();
        _manager = manager;

        var spawnJson = await manager.SpawnAsync(
            "test-agent", "Do some work", runInBackground: true);

        using var spawnDoc = JsonDocument.Parse(spawnJson);
        var agentId = spawnDoc.RootElement.GetProperty("agent_id").GetString()!;

        return (manager, agentId);
    }

    /// <summary>
    /// Builds a manager with no sub-agents spawned, for the unknown-id guard tests.
    /// </summary>
    private SubAgentManager BuildEmptyManager()
    {
        var manager = CreateManager();
        _manager = manager;
        return manager;
    }

    /// <summary>
    /// Creates a SubAgentManager with the test's mock sub-agent and parent.
    /// </summary>
    private SubAgentManager CreateManager(int maxConcurrent = 5)
    {
        var options = CreateOptions(maxConcurrent);
        return new SubAgentManager(
            parentAgent: _parentMock.Object,
            parentContracts: [],
            parentHandlers: new Dictionary<string, ToolHandler>(),
            options: options,
            source: new MutableSubAgentTemplateSource(options.Templates));
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
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["test-agent"] = template,
            },
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
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(ToAsyncEnumerable(messages)));
    }

    /// <summary>
    /// Converts a list of messages to an IAsyncEnumerable for mock setup.
    /// </summary>
    private static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(
        List<IMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default)
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
