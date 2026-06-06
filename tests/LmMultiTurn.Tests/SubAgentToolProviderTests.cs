using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
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
/// Unit tests for SubAgentToolProvider: verifies the Agent, SendMessage, and
/// CheckAgent tool descriptors are generated correctly (including the embedded
/// template catalog) and that handler argument validation works.
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

        var researcher = new SubAgentTemplate
        {
            SystemPrompt = "You are a researcher.",
            Description = "Researches topics and summarizes findings.",
            WhenToUse = "Use for open-ended investigation across many sources.",
            AgentFactory = () => _subAgentMock.Object,
        };

        var coder = new SubAgentTemplate
        {
            SystemPrompt = "You are a coder.",
            Description = "Writes and edits code.",
            WhenToUse = "Use for focused implementation tasks.",
            AgentFactory = () => _subAgentMock.Object,
        };

        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["researcher"] = researcher,
                ["coder"] = coder,
            },
            MaxConcurrentSubAgents = 5,
        };

        _manager = new SubAgentManager(
            parentAgent: _parentMock.Object,
            parentContracts: [],
            parentHandlers: new Dictionary<string, ToolHandler>(),
            options: options);

        _provider = new SubAgentToolProvider(_manager, options.Templates);

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
    public void GetFunctions_ReturnsThreeFunctions()
    {
        // Act
        var functions = _provider!.GetFunctions().ToList();

        // Assert
        functions.Should().HaveCount(3);
        functions.Select(f => f.Contract.Name)
            .Should().BeEquivalentTo(["Agent", "SendMessage", "CheckAgent"]);
    }

    [Fact]
    public void AgentDescriptor_EmbedsTemplateCatalog()
    {
        // Act
        var agent = _provider!.GetFunctions()
            .First(f => f.Contract.Name == "Agent");

        // Assert: each template's key, Description, and WhenToUse appear in the
        // tool description so the parent LLM can pick the right sub-agent type.
        var description = agent.Contract.Description!;
        description.Should().Contain("researcher");
        description.Should().Contain("Researches topics and summarizes findings.");
        description.Should().Contain("Use for open-ended investigation across many sources.");
        description.Should().Contain("coder");
        description.Should().Contain("Writes and edits code.");
    }

    [Fact]
    public void AgentDescriptor_HasParityParameters()
    {
        // Act
        var agent = _provider!.GetFunctions()
            .First(f => f.Contract.Name == "Agent");
        var paramNames = agent.Contract.Parameters!.Select(p => p.Name).ToList();

        // Assert: Claude Code parity parameters present; legacy ones gone.
        paramNames.Should().Contain(
            ["subagent_type", "prompt", "description", "name", "model", "run_in_background", "add_tools", "remove_tools"]);
        paramNames.Should().NotContain("template_name");
        paramNames.Should().NotContain("task");
        paramNames.Should().NotContain("agent_id");
    }

    [Fact]
    public async Task HandleAgentToolAsync_MissingPrompt_ThrowsArgumentException()
    {
        // Arrange
        var agentHandler = GetHandler("Agent");
        var args = JsonSerializer.Serialize(new { subagent_type = "researcher" });

        // Act
        var act = () => agentHandler(args, new ToolCallContext(), CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*prompt*required*");
    }

    [Fact]
    public async Task HandleAgentToolAsync_MissingSubagentType_ThrowsArgumentException()
    {
        // Arrange
        var agentHandler = GetHandler("Agent");
        var args = JsonSerializer.Serialize(new { prompt = "do something" });

        // Act
        var act = () => agentHandler(args, new ToolCallContext(), CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*subagent_type*required*");
    }

    [Fact]
    public async Task HandleSendMessageToolAsync_MissingTarget_ThrowsArgumentException()
    {
        // Arrange
        var handler = GetHandler("SendMessage");
        var args = JsonSerializer.Serialize(new { prompt = "follow up" });

        // Act
        var act = () => handler(args, new ToolCallContext(), CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*target*required*");
    }

    [Fact]
    public async Task HandleSendMessageToolAsync_MissingPrompt_ThrowsArgumentException()
    {
        // Arrange
        var handler = GetHandler("SendMessage");
        var args = JsonSerializer.Serialize(new { target = "abc123" });

        // Act
        var act = () => handler(args, new ToolCallContext(), CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*prompt*required*");
    }

    [Fact]
    public async Task HandleCheckAgentToolAsync_MissingAgentId_ThrowsArgumentException()
    {
        // Arrange
        var handler = GetHandler("CheckAgent");
        var args = JsonSerializer.Serialize(new { });

        // Act
        var act = () => handler(args, new ToolCallContext(), CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*agent_id*required*");
    }

    private ToolHandler GetHandler(string name)
    {
        return _provider!.GetFunctions().First(f => f.Contract.Name == name).Handler;
    }
}
