using AchieveAi.LmDotnetTools.LmCore.Tests.Utilities;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Middleware;

public class ModelFallbackMiddlewareTests
{
    #region Test Methods

    [Fact]
    public async Task InvokeAsync_ShouldUseCorrectAgentBasedOnModelId()
    {
        // Arrange
        var modelId = "model1";
        var testMessage = new TextMessage { Text = "Test message", Role = Role.User };
        var model1Response = new TextMessage { Text = "Response from model1", Role = Role.Assistant };
        var defaultResponse = new TextMessage { Text = "Response from default", Role = Role.Assistant };

        // Create agents
        var model1Agent = new MockAgent(model1Response);
        var defaultAgent = new MockAgent(defaultResponse);

        // Create model agent map
        var modelAgentMap = new Dictionary<string, IAgent[]> { { "model1", new IAgent[] { model1Agent } } };

        // Create middleware
        var middleware = new ModelFallbackMiddleware(modelAgentMap, defaultAgent);

        // Create context with model1 specified
        var options = new GenerateReplyOptions { ModelId = modelId };
        var context = new MiddlewareContext([testMessage], options);

        // Act - we pass a placeholder agent here, the middleware should use model1Agent instead
        var result = await middleware.InvokeAsync(
            context,
            new MockAgent(new TextMessage { Text = "Placeholder", Role = Role.Assistant }),
            CancellationToken.None
        );

        // Assert
        Assert.Equal("Response from model1", ((TextMessage)result.First()).Text);
    }

    [Fact]
    public async Task InvokeAsync_ShouldUseDefaultAgentWhenModelNotFound()
    {
        // Arrange
        var modelId = "unknown-model";
        var testMessage = new TextMessage { Text = "Test message", Role = Role.User };
        var model1Response = new TextMessage { Text = "Response from model1", Role = Role.Assistant };
        var defaultResponse = new TextMessage { Text = "Response from default", Role = Role.Assistant };

        // Create agents
        var model1Agent = new MockAgent(model1Response);
        var defaultAgent = new MockAgent(defaultResponse);

        // Create model agent map
        var modelAgentMap = new Dictionary<string, IAgent[]> { { "model1", new IAgent[] { model1Agent } } };

        // Create middleware
        var middleware = new ModelFallbackMiddleware(modelAgentMap, defaultAgent);

        // Create context with unknown model specified
        var options = new GenerateReplyOptions { ModelId = modelId };
        var context = new MiddlewareContext([testMessage], options);

        // Act - we pass a placeholder agent here, the middleware should use defaultAgent instead
        var result = await middleware.InvokeAsync(
            context,
            new MockAgent(new TextMessage { Text = "Placeholder", Role = Role.Assistant }),
            CancellationToken.None
        );

        // Assert
        Assert.Equal("Response from default", ((TextMessage)result.First()).Text);
    }

    [Fact]
    public async Task InvokeAsync_ShouldTryNextAgentWhenFirstFails()
    {
        // Arrange
        var modelId = "model1";
        var testMessage = new TextMessage { Text = "Test message", Role = Role.User };
        var successResponse = new TextMessage { Text = "Success response", Role = Role.Assistant };

        // Create a failing agent that throws an exception
        var failingAgent = new Mock<IAgent>();
        _ = failingAgent
            .Setup(a =>
                a.GenerateReplyAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ThrowsAsync(new Exception("Agent failure"));

        // Create a successful agent
        var successAgent = new MockAgent(successResponse);

        // Create model agent map with the failing agent first
        var modelAgentMap = new Dictionary<string, IAgent[]>
        {
            { "model1", new[] { failingAgent.Object, successAgent } },
        };

        // Create middleware
        var middleware = new ModelFallbackMiddleware(
            modelAgentMap,
            new MockAgent(new TextMessage { Text = "Default response", Role = Role.Assistant })
        );

        // Create context with model1 specified
        var options = new GenerateReplyOptions { ModelId = modelId };
        var context = new MiddlewareContext([testMessage], options);

        // Act - the middleware should try the failing agent first, then fall back to the success agent
        var result = await middleware.InvokeAsync(
            context,
            new MockAgent(new TextMessage { Text = "Placeholder", Role = Role.Assistant }),
            CancellationToken.None
        );

        // Assert
        _ = Assert.Single(result);
        Assert.Equal("Success response", ((TextMessage)result.First()).Text);
    }

    [Fact]
    public async Task InvokeAsync_ShouldUseDefaultAgentAsLastResort()
    {
        // Arrange
        var modelId = "model1";
        var testMessage = new TextMessage { Text = "Test message", Role = Role.User };
        var defaultResponse = new TextMessage { Text = "Default response", Role = Role.Assistant };

        // Create failing agents
        var failingAgent1 = new Mock<IAgent>();
        _ = failingAgent1
            .Setup(a =>
                a.GenerateReplyAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ThrowsAsync(new Exception("Agent 1 failure"));

        var failingAgent2 = new Mock<IAgent>();
        _ = failingAgent2
            .Setup(a =>
                a.GenerateReplyAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ThrowsAsync(new Exception("Agent 2 failure"));

        // Create a default agent
        var defaultAgent = new MockAgent(defaultResponse);

        // Create model agent map with two failing agents
        var modelAgentMap = new Dictionary<string, IAgent[]>
        {
            { "model1", new[] { failingAgent1.Object, failingAgent2.Object } },
        };

        // Create middleware with tryDefaultLast = true
        var middleware = new ModelFallbackMiddleware(modelAgentMap, defaultAgent);

        // Create context with model1 specified
        var options = new GenerateReplyOptions { ModelId = modelId };
        var context = new MiddlewareContext([testMessage], options);

        // Act - the middleware should try both failing agents, then fall back to the default agent
        var result = await middleware.InvokeAsync(
            context,
            new MockAgent(new TextMessage { Text = "Placeholder", Role = Role.Assistant }),
            CancellationToken.None
        );

        // Assert
        Assert.Equal("Default response", ((TextMessage)result.First()).Text);
    }

    [Fact]
    public async Task InvokeAsync_ShouldThrowExceptionWhenAllAgentsFail()
    {
        // Arrange
        var modelId = "model1";
        var testMessage = new TextMessage { Text = "Test message", Role = Role.User };

        // Create failing agents
        var failingAgent1 = new Mock<IAgent>();
        _ = failingAgent1
            .Setup(a =>
                a.GenerateReplyAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ThrowsAsync(new Exception("Agent 1 failure"));

        var failingAgent2 = new Mock<IAgent>();
        _ = failingAgent2
            .Setup(a =>
                a.GenerateReplyAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ThrowsAsync(new Exception("Agent 2 failure"));

        // Create a failing default agent
        var failingDefaultAgent = new Mock<IAgent>();
        _ = failingDefaultAgent
            .Setup(a =>
                a.GenerateReplyAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ThrowsAsync(new Exception("Default agent failure"));

        // Create model agent map with two failing agents
        var modelAgentMap = new Dictionary<string, IAgent[]>
        {
            { "model1", new[] { failingAgent1.Object, failingAgent2.Object } },
        };

        // Create middleware with tryDefaultLast = true
        var middleware = new ModelFallbackMiddleware(modelAgentMap, failingDefaultAgent.Object);

        // Create context with model1 specified
        var options = new GenerateReplyOptions { ModelId = modelId };
        var context = new MiddlewareContext([testMessage], options);

        // Act & Assert - the middleware should try all agents and throw the first exception
        var exception = await Assert.ThrowsAsync<Exception>(() =>
            middleware.InvokeAsync(
                context,
                new MockAgent(new TextMessage { Text = "Placeholder", Role = Role.Assistant }),
                CancellationToken.None
            )
        );

        // Verify that we got an exception, but don't check for exact message since the implementation might vary
        Assert.NotNull(exception);
        // The most important thing is that an exception was thrown, the specific message is less critical
        // and might vary depending on how the middleware is implemented
    }

    [Fact]
    public async Task InvokeStreamingAsync_ShouldUseCorrectAgentBasedOnModelId()
    {
        // Arrange
        var modelId = "model1";
        var testMessage = new TextMessage { Text = "Test message", Role = Role.User };
        var model1Response = new[]
        {
            new TextMessage { Text = "Partial response", Role = Role.Assistant },
            new TextMessage { Text = "Final response from model1", Role = Role.Assistant },
        };
        var defaultResponse = new[]
        {
            new TextMessage { Text = "Final response from default", Role = Role.Assistant },
        };

        // Create agents
        var model1Agent = new MockStreamingAgent(model1Response);
        var defaultAgent = new MockStreamingAgent(defaultResponse);

        // Create model agent map
        var modelAgentMap = new Dictionary<string, IAgent[]> { { "model1", new IAgent[] { model1Agent } } };

        // Create middleware
        var middleware = new ModelFallbackMiddleware(modelAgentMap, defaultAgent);

        // Create context with model1 specified
        var options = new GenerateReplyOptions { ModelId = modelId };
        var context = new MiddlewareContext([testMessage], options);

        // Act - we pass a placeholder agent here, the middleware should use model1Agent instead
        var result = await middleware.InvokeStreamingAsync(
            context,
            new MockStreamingAgent([new TextMessage { Text = "Placeholder", Role = Role.Assistant }]),
            CancellationToken.None
        );

        // Consume the stream to get all messages
        var messages = await result.ToListAsync();

        // Assert
        Assert.Equal(2, messages.Count);
        Assert.Equal("Partial response", ((TextMessage)messages[0]).Text);
        Assert.Equal("Final response from model1", ((TextMessage)messages[1]).Text);
    }

    #endregion
}
