using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.ClientTools;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// Integration tests verifying the full sub-agent orchestration flow:
/// parent agent spawns a sub-agent via tool call, the sub-agent completes,
/// and the result flows back to the parent — synchronously as the tool result
/// (default) or via a background receipt polled with CheckAgent.
/// </summary>
public class SubAgentIntegrationTests
{
    /// <summary>
    /// End-to-end test: parent calls the Agent tool to spawn a sub-agent. By
    /// default the call is synchronous, so the tool result IS the sub-agent's
    /// final answer (not a JSON receipt) and there is no second parent relay turn.
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
                                    subagent_type = "researcher",
                                    prompt = "Research the topic",
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

        // Synchronous Agent: the tool result IS the sub-agent's final answer,
        // not a JSON spawn receipt.
        var toolCallResults = messages.OfType<ToolCallResultMessage>().ToList();
        toolCallResults.Should().NotBeEmpty(
            "the Agent tool should have returned a result");

        toolCallResults.Should().Contain(
            r => r.Result.Contains("Sub-agent analysis complete"),
            "synchronous Agent returns the sub-agent's final text as the tool result");

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
    /// Verifies the background spawn + CheckAgent flow: a sub-agent spawned with
    /// run_in_background: true returns a JSON receipt with an agent id, which the
    /// parent then polls with the CheckAgent tool.
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
                        // Spawn a sub-agent in the background so the tool returns a
                        // receipt (agent_id) immediately for CheckAgent to poll.
                        return Task.FromResult(ToAsyncEnumerable([
                            new ToolCallMessage
                            {
                                FunctionName = "Agent",
                                FunctionArgs = JsonSerializer.Serialize(new
                                {
                                    subagent_type = "worker",
                                    prompt = "Do some work",
                                    run_in_background = true,
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

    /// <summary>
    /// #246: a descendant parking on <c>AskUserQuestion</c> must surface to the ROOT conversation as a
    /// DISTINCT <see cref="NotifyKinds.DescendantQuestion"/> notification — never the generic
    /// <see cref="NotifyKinds.ClientNotification"/> kind, and never conflated with the ordinary
    /// <see cref="NotifyKinds.SubAgentCompletion"/> relay that a background spawn also produces — so a
    /// client watching only the root/primary stream can navigate straight to the pending question
    /// without already having the child's own tab open. Exercises the REAL production wiring end to
    /// end: <see cref="MultiTurnAgentLoop"/> (root) -&gt; <see cref="SubAgentManager"/> (child) -&gt;
    /// the loop's <c>DeliverClientNotificationAsync</c> default sink, which both persists the
    /// notification to history and publishes it to subscribers.
    /// </summary>
    [Fact]
    public async Task ParentSpawnsBackgroundSubAgent_WhenChildParksOnAskUserQuestion_RootReceivesExactlyOneDescendantQuestionNotification()
    {
        var childAskArgs = JsonSerializer.Serialize(new
        {
            context = "Need to know which color to use.",
            questions = new[]
            {
                new
                {
                    prompt = "Which color?",
                    options = new object[] { new { label = "Red" }, new { label = "Blue" } },
                },
            },
        });

        var childAgentMock = new Mock<IStreamingAgent>();
        childAgentMock
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(), It.IsAny<GenerateReplyOptions>(), It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(ToAsyncEnumerable([
                new ToolCallMessage
                {
                    FunctionName = AskUserQuestionToolProvider.ToolName,
                    FunctionArgs = childAskArgs,
                    ToolCallId = "tc_child_color",
                    Role = Role.Assistant,
                },
            ])));

        var parentAgentMock = new Mock<IStreamingAgent>();
        var parentCallCount = 0;
        parentAgentMock
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(), It.IsAny<GenerateReplyOptions>(), It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>((_, _, _) =>
            {
                parentCallCount++;
                if (parentCallCount == 1)
                {
                    // Spawn "asker" in the background so the parent's turn finishes immediately with
                    // a receipt, leaving the child free to park on its own AskUserQuestion afterward.
                    return Task.FromResult(ToAsyncEnumerable([
                        new ToolCallMessage
                        {
                            FunctionName = "Agent",
                            FunctionArgs = JsonSerializer.Serialize(new
                            {
                                subagent_type = "asker",
                                prompt = "ask the user which color",
                                run_in_background = true,
                            }),
                            ToolCallId = "call_spawn_asker",
                            Role = Role.Assistant,
                        },
                    ]));
                }

                return Task.FromResult(ToAsyncEnumerable([
                    new TextMessage { Text = "Dispatched the asker sub-agent.", Role = Role.Assistant },
                ]));
            });

        var subAgentOptions = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["asker"] = new SubAgentTemplate
                {
                    Name = "asker",
                    SystemPrompt = "You ask the user a clarifying question.",
                    AgentFactory = () => childAgentMock.Object,
                },
            },
            MaxConcurrentSubAgents = 5,
        };

        var registry = new FunctionRegistry();
        await using var loop = new MultiTurnAgentLoop(
            parentAgentMock.Object,
            registry,
            threadId: "descendant-question-root-thread",
            subAgentOptions: subAgentOptions);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var runTask = loop.RunAsync(cts.Token);

        var descendantQuestionNotifications = new List<NotifyMessage>();
        var observeTask = ObserveAsync(loop, msg =>
        {
            if (msg is NotifyMessage { NotifyKind: NotifyKinds.DescendantQuestion } nm)
            {
                lock (descendantQuestionNotifications)
                {
                    descendantQuestionNotifications.Add(nm);
                }
            }
        }, cts.Token);

        var userInput = new UserInput(
            [new TextMessage { Text = "Please figure out the color, asking me if needed.", Role = Role.User }]);

        string? spawnedAgentId = null;
        await foreach (var msg in loop.ExecuteRunAsync(userInput, cts.Token))
        {
            if (msg is ToolCallResultMessage { Result: var result } && result.Contains("agent_id"))
            {
                using var doc = JsonDocument.Parse(result);
                spawnedAgentId = doc.RootElement.GetProperty("agent_id").GetString();
            }
        }

        spawnedAgentId.Should().NotBeNullOrEmpty(
            "the background spawn must have returned a receipt with an agent id");

        // The background child races the parent's own run; wait deterministically for its
        // notification to land on the root's publish stream.
        await WaitForConditionAsync(
            () =>
            {
                lock (descendantQuestionNotifications)
                {
                    return descendantQuestionNotifications.Count > 0;
                }
            },
            TimeSpan.FromSeconds(10));

        await cts.CancelAsync();
        await observeTask;

        List<NotifyMessage> snapshot;
        lock (descendantQuestionNotifications)
        {
            snapshot = [.. descendantQuestionNotifications];
        }

        snapshot.Should().HaveCount(
            1,
            "the root conversation must receive EXACTLY ONE descendant-question notification for the " +
            "one parked child — never zero, and never a duplicate");
        snapshot[0].SourceToolCallId.Should().Be(
            spawnedAgentId, "the notification must be attributed to the descendant's own agent id");
        snapshot[0].Label.Should().Be("asker");

        // The primary/root's OWN deferred-call registry has nothing parked — the pending question
        // belongs entirely to the child, proving this notification genuinely came from the descendant
        // tree rather than the primary's own AskUserQuestion handling.
        (await loop.GetDeferredToolCallsAsync()).Should().BeEmpty();
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

    private static Task ObserveAsync(MultiTurnAgentLoop loop, Action<IMessage> onMessage, CancellationToken ct)
    {
        var messages = loop.SubscribeAsync(ct).GetAsyncEnumerator(ct);
        var first = messages.MoveNextAsync();

        // Not `ct`: a cancelled token would skip this body entirely, leaving the subscription
        // attached and the pending move unobserved.
        return Task.Run(async () =>
        {
            try
            {
                for (var hasMessage = await first; hasMessage; hasMessage = await messages.MoveNextAsync())
                {
                    onMessage(messages.Current);
                }
            }
            catch (OperationCanceledException)
            {
                // Cancelling the token is how these tests end the subscription.
            }
            finally
            {
                await messages.DisposeAsync();
            }
        }, CancellationToken.None);
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25);
        }
    }

    #endregion
}
