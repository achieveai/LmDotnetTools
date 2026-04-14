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

namespace LmMultiTurn.Tests;

/// <summary>
/// Integration tests verifying the full sub-agent orchestration flow:
/// parent agent spawns sub-agent via tool call, sub-agent completes,
/// and result is relayed back to the parent.
/// </summary>
public class SubAgentIntegrationTests
{
    /// <summary>
    /// End-to-end test: parent calls the Agent tool to spawn a sub-agent,
    /// the tool handler returns a JSON result with agent_id, and the
    /// sub-agent's mock responds with a text message.
    /// </summary>
    [Fact]
    public async Task ParentSpawnsSubAgent_SubAgentCompletes_ParentReceivesResult()
    {
        // Arrange: Create parent and sub-agent mocks
        var parentAgentMock = new Mock<IStreamingAgent>();
        var subAgentMock = new Mock<IStreamingAgent>();

        // Sub-agent always returns a simple text response
        subAgentMock
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(ToAsyncEnumerable([
                new TextMessage { Text = "Sub-agent analysis complete", Role = Role.Assistant },
            ])));

        // Parent agent: first call returns Agent tool call, second call returns final text
        var parentCallCount = 0;
        parentAgentMock
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (_, _, _) =>
                {
                    parentCallCount++;
                    if (parentCallCount == 1)
                    {
                        // Parent's first response: call the Agent tool to spawn sub-agent
                        return Task.FromResult(ToAsyncEnumerable([
                            new ToolCallMessage
                            {
                                FunctionName = "Agent",
                                FunctionArgs = JsonSerializer.Serialize(new
                                {
                                    template_name = "researcher",
                                    task = "Research the topic",
                                }),
                                ToolCallId = "call_agent_1",
                                Role = Role.Assistant,
                            },
                        ]));
                    }

                    // Parent's subsequent responses: final text
                    return Task.FromResult(ToAsyncEnumerable([
                        new TextMessage
                        {
                            Text = "I've dispatched a researcher sub-agent.",
                            Role = Role.Assistant,
                        },
                    ]));
                });

        // Configure sub-agent template
        var subAgentTemplate = new SubAgentTemplate
        {
            Name = "researcher",
            SystemPrompt = "You are a research assistant.",
            AgentFactory = () => subAgentMock.Object,
        };

        var subAgentOptions = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["researcher"] = subAgentTemplate,
            },
            MaxConcurrentSubAgents = 3,
        };

        // Create the parent agent loop with sub-agent orchestration
        var registry = new FunctionRegistry();
        await using var loop = new MultiTurnAgentLoop(
            parentAgentMock.Object,
            registry,
            threadId: "integration-test-thread",
            subAgentOptions: subAgentOptions);

        using var cts = new CancellationTokenSource();
        var runTask = loop.RunAsync(cts.Token);

        // Act: Send user message and collect all output messages
        var userInput = new UserInput(
            [new TextMessage { Text = "Please research AI trends", Role = Role.User }]);

        var messages = new List<IMessage>();
        await foreach (var msg in loop.ExecuteRunAsync(userInput, cts.Token))
        {
            messages.Add(msg);
        }

        // Assert: verify the run produced expected message types
        messages.OfType<RunAssignmentMessage>().Should().NotBeEmpty(
            "the run should start with a RunAssignmentMessage");

        // The Agent tool call should have been made
        messages.OfType<ToolCallMessage>().Should().Contain(
            tc => tc.FunctionName == "Agent",
            "the parent should have called the Agent tool");

        // The tool call result should contain a valid agent_id
        var toolCallResults = messages.OfType<ToolCallResultMessage>().ToList();
        toolCallResults.Should().NotBeEmpty(
            "the Agent tool should have returned a result");

        var agentToolResult = toolCallResults
            .FirstOrDefault(r => r.ToolName == "Agent" || r.Result.Contains("agent_id"));
        agentToolResult.Should().NotBeNull(
            "the Agent tool result should be present");

        using var resultDoc = JsonDocument.Parse(agentToolResult!.Result);
        resultDoc.RootElement.GetProperty("agent_id").GetString()
            .Should().NotBeNullOrEmpty("spawn should return an agent_id");
        resultDoc.RootElement.GetProperty("status").GetString()
            .Should().Be("spawned");

        // The parent should have generated a final text response
        messages.OfType<TextMessage>()
            .Should().Contain(m => m.Role == Role.Assistant && m.Text.Contains("dispatched"),
                "the parent should produce a final text response after spawning");

        // The run should complete
        messages.OfType<RunCompletedMessage>().Should().NotBeEmpty(
            "the run should complete with RunCompletedMessage");

        // Cleanup
        await cts.CancelAsync();
    }

    /// <summary>
    /// Verifies that the CheckAgent tool is registered and callable
    /// alongside the Agent tool when sub-agent options are configured.
    /// </summary>
    [Fact]
    public async Task SubAgentTools_AreRegisteredAndCallable()
    {
        // Arrange
        var parentAgentMock = new Mock<IStreamingAgent>();
        var subAgentMock = new Mock<IStreamingAgent>();

        subAgentMock
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(ToAsyncEnumerable([
                new TextMessage { Text = "Done", Role = Role.Assistant },
            ])));

        // Parent first spawns, then checks the agent
        var parentCallCount = 0;
        parentAgentMock
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (msgs, _, _) =>
                {
                    parentCallCount++;
                    if (parentCallCount == 1)
                    {
                        // Spawn a sub-agent
                        return Task.FromResult(ToAsyncEnumerable([
                            new ToolCallMessage
                            {
                                FunctionName = "Agent",
                                FunctionArgs = JsonSerializer.Serialize(new
                                {
                                    template_name = "worker",
                                    task = "Do some work",
                                }),
                                ToolCallId = "call_spawn",
                                Role = Role.Assistant,
                            },
                        ]));
                    }

                    if (parentCallCount == 2)
                    {
                        // Try to extract agent_id from previous messages
                        // and call CheckAgent
                        var msgList = msgs.ToList();
                        var toolResult = msgList
                            .OfType<ToolCallResultMessage>()
                            .FirstOrDefault(r => r.Result.Contains("agent_id"));

                        var agentId = "unknown";
                        if (toolResult != null)
                        {
                            using var doc = JsonDocument.Parse(toolResult.Result);
                            agentId = doc.RootElement
                                .GetProperty("agent_id").GetString() ?? "unknown";
                        }

                        return Task.FromResult(ToAsyncEnumerable([
                            new ToolCallMessage
                            {
                                FunctionName = "CheckAgent",
                                FunctionArgs = JsonSerializer.Serialize(new
                                {
                                    agent_id = agentId,
                                }),
                                ToolCallId = "call_check",
                                Role = Role.Assistant,
                            },
                        ]));
                    }

                    // Final response
                    return Task.FromResult(ToAsyncEnumerable([
                        new TextMessage
                        {
                            Text = "Agent status checked.",
                            Role = Role.Assistant,
                        },
                    ]));
                });

        var template = new SubAgentTemplate
        {
            Name = "worker",
            SystemPrompt = "You are a worker.",
            AgentFactory = () => subAgentMock.Object,
        };

        var subAgentOptions = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["worker"] = template,
            },
        };

        var registry = new FunctionRegistry();
        await using var loop = new MultiTurnAgentLoop(
            parentAgentMock.Object,
            registry,
            threadId: "check-agent-test",
            subAgentOptions: subAgentOptions);

        using var cts = new CancellationTokenSource();
        var runTask = loop.RunAsync(cts.Token);

        // Act
        var userInput = new UserInput(
            [new TextMessage { Text = "Spawn and check agent", Role = Role.User }]);

        var messages = new List<IMessage>();
        await foreach (var msg in loop.ExecuteRunAsync(userInput, cts.Token))
        {
            messages.Add(msg);
        }

        // Assert: both Agent and CheckAgent tools were called and returned results
        var toolCalls = messages.OfType<ToolCallMessage>().ToList();
        toolCalls.Should().Contain(tc => tc.FunctionName == "Agent");
        toolCalls.Should().Contain(tc => tc.FunctionName == "CheckAgent");

        var toolResults = messages.OfType<ToolCallResultMessage>().ToList();
        toolResults.Should().HaveCountGreaterThanOrEqualTo(2,
            "both Agent and CheckAgent should produce results");

        // CheckAgent result should contain status information
        var checkResult = toolResults
            .FirstOrDefault(r => r.Result.Contains("status") && r.Result.Contains("template"));
        checkResult.Should().NotBeNull(
            "CheckAgent should return status with template info");

        // Cleanup
        await cts.CancelAsync();
    }

    #region Helpers

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
