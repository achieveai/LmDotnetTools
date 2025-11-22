using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Agents;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Tests.Agents;

public class ClaudeAgentSdkAgentTests
{
    [Fact]
    public async Task Agent_StartsClientOnFirstCall()
    {
        // Arrange
        var expectedMessages = new List<IMessage>
        {
            new TextMessage { Text = "Hello from agent", Role = Role.Assistant }
        };

        var mockClient = new MockClaudeAgentSdkClient(
            messagesToReplay: expectedMessages,
            validateRequest: req =>
            {
                Assert.Equal("claude-sonnet-4-5-20250929", req.ModelId);
                Assert.Equal(40, req.MaxTurns);
            }
        );

        var options = new ClaudeAgentSdkOptions();
        var agent = new ClaudeAgentSdkAgent("test-agent", mockClient, options);

        // Act
        var inputMessages = new[] { new TextMessage { Text = "Hi", Role = Role.User } };
        var responses = await agent.GenerateReplyAsync(inputMessages);

        // Assert
        Assert.True(mockClient.IsRunning);
        Assert.Single(responses);
        var textMsg = Assert.IsType<TextMessage>(responses.First());
        Assert.Equal("Hello from agent", textMsg.Text);
    }

    [Fact]
    public async Task Agent_StreamsMessagesCorrectly()
    {
        // Arrange
        var expectedMessages = new List<IMessage>
        {
            new ReasoningMessage { Reasoning = "Thinking...", Role = Role.Assistant },
            new TextMessage { Text = "Result", Role = Role.Assistant }
        };

        var mockClient = new MockClaudeAgentSdkClient(messagesToReplay: expectedMessages);
        var options = new ClaudeAgentSdkOptions();
        var agent = new ClaudeAgentSdkAgent("test-agent", mockClient, options);

        // Act
        var inputMessages = new[] { new TextMessage { Text = "Question", Role = Role.User } };
        var streamTask = await agent.GenerateReplyStreamingAsync(inputMessages);

        var streamedMessages = new List<IMessage>();
        await foreach (var msg in streamTask)
        {
            streamedMessages.Add(msg);
        }

        // Assert
        Assert.Equal(2, streamedMessages.Count);
        Assert.IsType<ReasoningMessage>(streamedMessages[0]);
        Assert.IsType<TextMessage>(streamedMessages[1]);
    }

    [Fact]
    public void Agent_ThrowsOnNullClient()
    {
        // Arrange & Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new ClaudeAgentSdkAgent("test", null!, new ClaudeAgentSdkOptions()));
    }

    [Fact]
    public void Agent_ThrowsOnNullOptions()
    {
        // Arrange & Act & Assert
        var mockClient = new MockClaudeAgentSdkClient(messagesToReplay: []);
        Assert.Throws<ArgumentNullException>(() =>
            new ClaudeAgentSdkAgent("test", mockClient, null!));
    }

    [Fact]
    public void Agent_ThrowsOnNullName()
    {
        // Arrange & Act & Assert
        var mockClient = new MockClaudeAgentSdkClient(messagesToReplay: []);
        Assert.Throws<ArgumentNullException>(() =>
            new ClaudeAgentSdkAgent(null!, mockClient, new ClaudeAgentSdkOptions()));
    }

    [Fact]
    public async Task Agent_PassesCorrectModelIdFromOptions()
    {
        // Arrange
        string? capturedModelId = null;
        var mockClient = new MockClaudeAgentSdkClient(
            messagesToReplay: [new TextMessage { Text = "Response", Role = Role.Assistant }],
            validateRequest: req => { capturedModelId = req.ModelId; }
        );

        var options = new ClaudeAgentSdkOptions();
        var agent = new ClaudeAgentSdkAgent("test-agent", mockClient, options);

        var generateOptions = new GenerateReplyOptions
        {
            ModelId = "haiku"
        };

        // Act
        await agent.GenerateReplyAsync(
            [new TextMessage { Text = "Input", Role = Role.User }],
            generateOptions
        );

        // Assert
        Assert.Equal("haiku", capturedModelId);
    }

    [Fact]
    public async Task Agent_UsesDefaultModelWhenNotSpecified()
    {
        // Arrange
        string? capturedModelId = null;
        var mockClient = new MockClaudeAgentSdkClient(
            messagesToReplay: [new TextMessage { Text = "Response", Role = Role.Assistant }],
            validateRequest: req => { capturedModelId = req.ModelId; }
        );

        var options = new ClaudeAgentSdkOptions();
        var agent = new ClaudeAgentSdkAgent("test-agent", mockClient, options);

        // Act
        await agent.GenerateReplyAsync([new TextMessage { Text = "Input", Role = Role.User }]);

        // Assert
        Assert.Equal("claude-sonnet-4-5-20250929", capturedModelId);
    }

    [Fact]
    public async Task Agent_ProperlySetsSessionInfo()
    {
        // Arrange
        var mockClient = new MockClaudeAgentSdkClient(
            messagesToReplay: [new TextMessage { Text = "Response", Role = Role.Assistant }]
        );

        var options = new ClaudeAgentSdkOptions();
        var agent = new ClaudeAgentSdkAgent("test-agent", mockClient, options);

        // Act
        await agent.GenerateReplyAsync([new TextMessage { Text = "Input", Role = Role.User }]);

        // Assert
        Assert.NotNull(mockClient.CurrentSession);
        Assert.NotNull(mockClient.CurrentSession.SessionId);
        Assert.True(mockClient.CurrentSession.CreatedAt <= DateTime.UtcNow);
    }

    [Fact]
    public async Task Agent_ReusesClientAcrossMultipleCalls()
    {
        // Arrange
        var mockClient = new MockClaudeAgentSdkClient(
            messagesToReplay: [
                new TextMessage { Text = "Response 1", Role = Role.Assistant },
                new TextMessage { Text = "Response 2", Role = Role.Assistant }
            ]
        );

        var options = new ClaudeAgentSdkOptions();
        var agent = new ClaudeAgentSdkAgent("test-agent", mockClient, options);

        // Act
        await agent.GenerateReplyAsync([new TextMessage { Text = "Input 1", Role = Role.User }]);
        var isStillRunning = mockClient.IsRunning;

        // Assert - client should still be running after first call
        Assert.True(isStillRunning);
    }

    [Fact]
    public void Agent_HasCorrectName()
    {
        // Arrange & Act
        var mockClient = new MockClaudeAgentSdkClient(messagesToReplay: []);
        var agent = new ClaudeAgentSdkAgent("my-custom-agent", mockClient, new ClaudeAgentSdkOptions());

        // Assert
        Assert.Equal("my-custom-agent", agent.Name);
    }
}
