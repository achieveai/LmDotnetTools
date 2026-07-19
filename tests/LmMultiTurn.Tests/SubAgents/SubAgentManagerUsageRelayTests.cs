using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests.SubAgents;

/// <summary>
/// Verifies the SubAgentManager usage relay (#196): a sub-agent's UsageMessage is folded into the
/// root conversation's <see cref="UsageLedger"/>, and the relay is a no-op when no sink is supplied.
/// </summary>
public class SubAgentManagerUsageRelayTests : IAsyncLifetime
{
    private readonly Mock<IMultiTurnAgent> _parentMock = new();
    private readonly Mock<IStreamingAgent> _subAgentMock = new();
    private SubAgentManager? _manager;

    public Task InitializeAsync()
    {
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
    public async Task SubAgentUsage_IsFoldedIntoRootLedger()
    {
        var ledger = new UsageLedger("root-conv");
        SetupSubAgentResponse([
            new UsageMessage
            {
                Usage = new Usage { PromptTokens = 100, CompletionTokens = 40 },
                GenerationId = "gen-1",
            },
            new TextMessage { Text = "done", Role = Role.Assistant },
        ]);

        var agentId = await SpawnAsync(ledger);
        _ = await _manager!.ObserveCompletionAsync(agentId, CancellationToken.None);

        var snapshot = ledger.Snapshot();
        snapshot.TotalTokens.Should().Be(140);
        snapshot.RootConversationId.Should().Be("root-conv");
        snapshot.PerModel.Should().ContainSingle();
        snapshot.PerModel[0].AttemptCount.Should().Be(1);
    }

    [Fact]
    public async Task Relay_IsNoOp_WhenNoSinkSupplied()
    {
        SetupSubAgentResponse([new TextMessage { Text = "done", Role = Role.Assistant }]);

        var agentId = await SpawnAsync(usageSink: null);
        var result = await _manager!.ObserveCompletionAsync(agentId, CancellationToken.None);

        result.Should().Be("done");
    }

    [Fact]
    public async Task DescendantUsage_TriggersDurablePersist()
    {
        var ledger = new UsageLedger("root-conv");
        var persistCount = 0;
        SetupSubAgentResponse([
            new UsageMessage
            {
                Usage = new Usage { PromptTokens = 100, CompletionTokens = 40 },
                GenerationId = "gen-1",
            },
            new TextMessage { Text = "done", Role = Role.Assistant },
        ]);

        var agentId = await SpawnAsync(ledger, () =>
        {
            _ = Interlocked.Increment(ref persistCount);
            return Task.CompletedTask;
        });
        _ = await _manager!.ObserveCompletionAsync(agentId, CancellationToken.None);

        persistCount.Should().BeGreaterThan(0);
    }

    private async Task<string> SpawnAsync(IUsageSink? usageSink, Func<Task>? persistUsageAsync = null)
    {
        var manager = CreateManager(usageSink, persistUsageAsync);
        _manager = manager;

        var spawnJson = await manager.SpawnAsync("test-agent", "Do some work", runInBackground: true);
        using var spawnDoc = JsonDocument.Parse(spawnJson);
        return spawnDoc.RootElement.GetProperty("agent_id").GetString()!;
    }

    private SubAgentManager CreateManager(IUsageSink? usageSink, Func<Task>? persistUsageAsync = null)
    {
        var options = CreateOptions();
        return new SubAgentManager(
            parentAgent: _parentMock.Object,
            parentContracts: [],
            parentHandlers: new Dictionary<string, ToolHandler>(),
            options: options,
            source: new MutableSubAgentTemplateSource(options.Templates),
            usageSink: usageSink,
            persistUsageAsync: persistUsageAsync);
    }

    private SubAgentOptions CreateOptions()
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
            MaxConcurrentSubAgents = 5,
        };
    }

    private void SetupSubAgentResponse(List<IMessage> messages)
    {
        _subAgentMock
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(ToAsyncEnumerable(messages)));
    }

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
}
