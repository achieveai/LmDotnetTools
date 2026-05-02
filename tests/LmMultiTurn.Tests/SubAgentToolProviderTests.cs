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

namespace LmMultiTurn.Tests;

/// <summary>
/// Unit tests for SubAgentToolProvider: verifies that the Agent and CheckAgent
/// tool descriptors are correctly generated and that handler argument validation works.
/// </summary>
public class SubAgentToolProviderTests : IAsyncLifetime
{
    private readonly Mock<IMultiTurnAgent> _parentMock = new();
    private readonly Mock<IStreamingAgent> _subAgentMock = new();
    private SubAgentManager? _manager;
    private SubAgentToolProvider? _provider;

    public Task InitializeAsync()
    {
        _parentMock
            .Setup(p => p.SendAsync(
                It.IsAny<List<IMessage>>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendReceipt("receipt-1", null, DateTimeOffset.UtcNow));

        var template = new SubAgentTemplate
        {
            SystemPrompt = "You are a test agent.",
            AgentFactory = () => _subAgentMock.Object,
        };

        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["researcher"] = template,
                ["coder"] = template,
            },
            MaxConcurrentSubAgents = 5,
        };

        _manager = new SubAgentManager(
            parentAgent: _parentMock.Object,
            parentContracts: [],
            parentHandlers: new Dictionary<string, ToolHandler>(),
            options: options);

        _provider = new SubAgentToolProvider(
            _manager, ["researcher", "coder"]);

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
    public void GetFunctions_ReturnsTwoFunctions()
    {
        // Act
        var functions = _provider!.GetFunctions().ToList();

        // Assert
        functions.Should().HaveCount(2);
        functions.Select(f => f.Contract.Name)
            .Should().BeEquivalentTo(["Agent", "CheckAgent"]);
    }

    [Fact]
    public async Task HandleAgentToolAsync_MissingTask_ThrowsArgumentException()
    {
        // Arrange
        var functions = _provider!.GetFunctions().ToList();
        var agentHandler = functions.First(f => f.Contract.Name == "Agent").Handler;
        var args = JsonSerializer.Serialize(new { template_name = "researcher" });

        // Act
        var act = () => agentHandler(args, new ToolCallContext());

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*task*required*");
    }

    [Fact]
    public async Task HandleAgentToolAsync_MissingTemplateAndAgentId_ThrowsArgumentException()
    {
        // Arrange
        var functions = _provider!.GetFunctions().ToList();
        var agentHandler = functions.First(f => f.Contract.Name == "Agent").Handler;
        var args = JsonSerializer.Serialize(new { task = "do something" });

        // Act
        var act = () => agentHandler(args, new ToolCallContext());

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*template_name*required*");
    }

    [Fact]
    public async Task HandleCheckAgentToolAsync_MissingAgentId_ThrowsArgumentException()
    {
        // Arrange
        var functions = _provider!.GetFunctions().ToList();
        var checkHandler = functions.First(f => f.Contract.Name == "CheckAgent").Handler;
        var args = JsonSerializer.Serialize(new { });

        // Act
        var act = () => checkHandler(args, new ToolCallContext());

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*agent_id*required*");
    }
}
