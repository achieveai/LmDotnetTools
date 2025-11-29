using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Agents;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.LmCore.Core;
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
            new TextMessage { Text = "Hello from agent", Role = Role.Assistant },
        };

        var mockClient = new MockClaudeAgentSdkClient(
            expectedMessages,
            req =>
            {
                Assert.Equal("claude-sonnet-4-5-20250929", req.ModelId);
                Assert.Equal(40, req.MaxTurns);
            }
        );

        var options = new ClaudeAgentSdkOptions();
        var agent = new ClaudeAgentSdkAgent("test-agent", mockClient, options);

        // Act
        var inputMessages = new[]
        {
            new TextMessage { Text = "Hi", Role = Role.User },
        };
        var responses = await agent.GenerateReplyAsync(inputMessages);

        // Assert
        Assert.True(mockClient.IsRunning);
        _ = Assert.Single(responses);
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
            new TextMessage { Text = "Result", Role = Role.Assistant },
        };

        var mockClient = new MockClaudeAgentSdkClient(expectedMessages);
        var options = new ClaudeAgentSdkOptions();
        var agent = new ClaudeAgentSdkAgent("test-agent", mockClient, options);

        // Act
        var inputMessages = new[]
        {
            new TextMessage { Text = "Question", Role = Role.User },
        };
        var streamTask = await agent.GenerateReplyStreamingAsync(inputMessages);

        var streamedMessages = new List<IMessage>();
        await foreach (var msg in streamTask)
        {
            streamedMessages.Add(msg);
        }

        // Assert
        Assert.Equal(2, streamedMessages.Count);
        _ = Assert.IsType<ReasoningMessage>(streamedMessages[0]);
        _ = Assert.IsType<TextMessage>(streamedMessages[1]);
    }

    [Fact]
    public void Agent_ThrowsOnNullClient()
    {
        // Arrange & Act & Assert
        _ = Assert.Throws<ArgumentNullException>(() =>
            new ClaudeAgentSdkAgent("test", null!, new ClaudeAgentSdkOptions())
        );
    }

    [Fact]
    public void Agent_ThrowsOnNullOptions()
    {
        // Arrange & Act & Assert
        var mockClient = new MockClaudeAgentSdkClient([]);
        _ = Assert.Throws<ArgumentNullException>(() => new ClaudeAgentSdkAgent("test", mockClient, null!));
    }

    [Fact]
    public void Agent_ThrowsOnNullName()
    {
        // Arrange & Act & Assert
        var mockClient = new MockClaudeAgentSdkClient([]);
        _ = Assert.Throws<ArgumentNullException>(() =>
            new ClaudeAgentSdkAgent(null!, mockClient, new ClaudeAgentSdkOptions())
        );
    }

    [Fact]
    public async Task Agent_PassesCorrectModelIdFromOptions()
    {
        // Arrange
        string? capturedModelId = null;
        var mockClient = new MockClaudeAgentSdkClient(
            [new TextMessage { Text = "Response", Role = Role.Assistant }],
            req =>
            {
                capturedModelId = req.ModelId;
            }
        );

        var options = new ClaudeAgentSdkOptions();
        var agent = new ClaudeAgentSdkAgent("test-agent", mockClient, options);

        var generateOptions = new GenerateReplyOptions { ModelId = "haiku" };

        // Act
        _ = await agent.GenerateReplyAsync([new TextMessage { Text = "Input", Role = Role.User }], generateOptions);

        // Assert
        Assert.Equal("haiku", capturedModelId);
    }

    [Fact]
    public async Task Agent_UsesDefaultModelWhenNotSpecified()
    {
        // Arrange
        string? capturedModelId = null;
        var mockClient = new MockClaudeAgentSdkClient(
            [new TextMessage { Text = "Response", Role = Role.Assistant }],
            req =>
            {
                capturedModelId = req.ModelId;
            }
        );

        var options = new ClaudeAgentSdkOptions();
        var agent = new ClaudeAgentSdkAgent("test-agent", mockClient, options);

        // Act
        _ = await agent.GenerateReplyAsync([new TextMessage { Text = "Input", Role = Role.User }]);

        // Assert
        Assert.Equal("claude-sonnet-4-5-20250929", capturedModelId);
    }

    [Fact]
    public async Task Agent_ProperlySetsSessionInfo()
    {
        // Arrange
        var mockClient = new MockClaudeAgentSdkClient([new TextMessage { Text = "Response", Role = Role.Assistant }]);

        var options = new ClaudeAgentSdkOptions();
        var agent = new ClaudeAgentSdkAgent("test-agent", mockClient, options);

        // Act
        _ = await agent.GenerateReplyAsync([new TextMessage { Text = "Input", Role = Role.User }]);

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
            [
                new TextMessage { Text = "Response 1", Role = Role.Assistant },
                new TextMessage { Text = "Response 2", Role = Role.Assistant },
            ]
        );

        var options = new ClaudeAgentSdkOptions();
        var agent = new ClaudeAgentSdkAgent("test-agent", mockClient, options);

        // Act
        _ = await agent.GenerateReplyAsync([new TextMessage { Text = "Input 1", Role = Role.User }]);
        var isStillRunning = mockClient.IsRunning;

        // Assert - client should still be running after first call
        Assert.True(isStillRunning);
    }

    [Fact]
    public void Agent_HasCorrectName()
    {
        // Arrange & Act
        var mockClient = new MockClaudeAgentSdkClient([]);
        var agent = new ClaudeAgentSdkAgent("my-custom-agent", mockClient, new ClaudeAgentSdkOptions());

        // Assert
        Assert.Equal("my-custom-agent", agent.Name);
    }

    [Fact]
    public async Task InteractiveMode_ClientStaysRunningAcrossMultipleCalls()
    {
        // Arrange - Interactive mode: client stays running (default behavior)
        var mockClient = new MockClaudeAgentSdkClient(
            [new TextMessage { Text = "Response", Role = Role.Assistant }],
            simulateOneShotMode: false // Interactive mode
        );

        var options = new ClaudeAgentSdkOptions { Mode = ClaudeAgentSdkMode.Interactive };
        var agent = new ClaudeAgentSdkAgent("test-agent", mockClient, options);

        // Act - Make multiple calls
        _ = await agent.GenerateReplyAsync([new TextMessage { Text = "Input 1", Role = Role.User }]);
        _ = await agent.GenerateReplyAsync([new TextMessage { Text = "Input 2", Role = Role.User }]);
        _ = await agent.GenerateReplyAsync([new TextMessage { Text = "Input 3", Role = Role.User }]);

        // Assert - Client should be started only once
        Assert.Equal(1, mockClient.StartCallCount);
        Assert.True(mockClient.IsRunning);
    }

    [Fact]
    public async Task OneShotMode_PreservesSessionIdAcrossProcessRestarts()
    {
        // Arrange - OneShot mode: process exits after each call, must preserve sessionId
        var mockClient = new MockClaudeAgentSdkClient(
            [new TextMessage { Text = "Response", Role = Role.Assistant }],
            simulateOneShotMode: true // Process exits after each call
        );

        var options = new ClaudeAgentSdkOptions { Mode = ClaudeAgentSdkMode.OneShot };
        var agent = new ClaudeAgentSdkAgent("test-agent", mockClient, options);

        // Act - Make multiple calls (each should restart the process)
        _ = await agent.GenerateReplyAsync([new TextMessage { Text = "Input 1", Role = Role.User }]);

        // Capture the sessionId from first run
        var firstSessionId = mockClient.CurrentSession?.SessionId;
        Assert.NotNull(firstSessionId);

        // Second call should restart the client but use the same sessionId
        _ = await agent.GenerateReplyAsync([new TextMessage { Text = "Input 2", Role = Role.User }]);

        // Third call
        _ = await agent.GenerateReplyAsync([new TextMessage { Text = "Input 3", Role = Role.User }]);

        // Assert
        // Client should be started 3 times (once per call in OneShot mode)
        Assert.Equal(3, mockClient.StartCallCount);

        // Second and third requests should include the sessionId from first run
        Assert.Equal(3, mockClient.RequestHistory.Count);

        // First request has no sessionId (new session)
        Assert.Null(mockClient.RequestHistory[0].SessionId);

        // Second request should have the sessionId from first run (--resume)
        Assert.Equal(firstSessionId, mockClient.RequestHistory[1].SessionId);

        // Third request should also have the same sessionId
        Assert.Equal(firstSessionId, mockClient.RequestHistory[2].SessionId);
    }

    [Fact]
    public async Task OneShotMode_ExplicitSessionIdTakesPrecedence()
    {
        // Arrange
        var mockClient = new MockClaudeAgentSdkClient(
            [new TextMessage { Text = "Response", Role = Role.Assistant }],
            simulateOneShotMode: true
        );

        var options = new ClaudeAgentSdkOptions { Mode = ClaudeAgentSdkMode.OneShot };
        var agent = new ClaudeAgentSdkAgent("test-agent", mockClient, options);

        // Act - First call without explicit sessionId
        _ = await agent.GenerateReplyAsync([new TextMessage { Text = "Input 1", Role = Role.User }]);

        // Second call with explicit sessionId in options
        var explicitSessionId = "explicit-session-123";
        var optionsWithSession = new GenerateReplyOptions
        {
            ModelId = "claude-sonnet-4-5-20250929",
            ExtraProperties = new Dictionary<string, object?> { ["sessionId"] = explicitSessionId }.ToImmutableDictionary(),
        };
        _ = await agent.GenerateReplyAsync(
            [new TextMessage { Text = "Input 2", Role = Role.User }],
            optionsWithSession
        );

        // Assert - Explicit sessionId should take precedence
        Assert.Equal(2, mockClient.RequestHistory.Count);
        Assert.Equal(explicitSessionId, mockClient.RequestHistory[1].SessionId);
    }
}
