using System.Collections.Immutable;

using AchieveAi.LmDotnetTools.LmCore.Core;
namespace AchieveAi.LmDotnetTools.LmCore.Tests.Middleware;

public class OptionsOverridingMiddlewareTests
{
    #region Test Methods

    [Fact]
    public async Task InvokeAsync_ShouldOverrideOptions_WhenProvidedWithOverrides()
    {
        // Arrange
        // Set up original options and overriding options
        var originalOptions = new GenerateReplyOptions
        {
            Temperature = 0.7f,
            MaxToken = 100,
            TopP = 0.8f,
        };

        var overridingOptions = new GenerateReplyOptions
        {
            Temperature = 0.3f,
            TopP = 0.9f,
            // Intentionally not setting MaxToken to test partial overrides
        };

        // Create the middleware with overriding options
        var middleware = new OptionsOverridingMiddleware(overridingOptions);

        // Create a message for the context
        var message = new TextMessage { Text = "Test message", Role = Role.User };

        // Create the context with original options
        var context = new MiddlewareContext([message], originalOptions);

        // Set up mock agent to capture the options that are passed to it
        var mockAgent = new Mock<IAgent>();
        GenerateReplyOptions? capturedOptions = null;

        _ = mockAgent
            .Setup(a =>
                a.GenerateReplyAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (msgs, options, token) => capturedOptions = options
            )
            .ReturnsAsync([new TextMessage { Text = "Response", Role = Role.Assistant }]);

        // Act
        _ = await middleware.InvokeAsync(context, mockAgent.Object);

        // Assert
        Assert.NotNull(capturedOptions);
        Assert.Equal(overridingOptions.Temperature, capturedOptions.Temperature);
        Assert.Equal(overridingOptions.TopP, capturedOptions.TopP);
        Assert.Equal(originalOptions.MaxToken, capturedOptions.MaxToken); // Should keep original value
    }

    [Fact]
    public async Task InvokeAsync_ShouldUseOverridingOptions_WhenOriginalOptionsAreNull()
    {
        // Arrange
        var overridingOptions = new GenerateReplyOptions
        {
            Temperature = 0.3f,
            MaxToken = 200,
            TopP = 0.9f,
        };

        // Create the middleware with overriding options
        var middleware = new OptionsOverridingMiddleware(overridingOptions);

        // Create a message for the context
        var message = new TextMessage { Text = "Test message", Role = Role.User };

        // Create the context with null options
        var context = new MiddlewareContext([message]);

        // Set up mock agent to capture the options that are passed to it
        var mockAgent = new Mock<IAgent>();
        GenerateReplyOptions? capturedOptions = null;

        _ = mockAgent
            .Setup(a =>
                a.GenerateReplyAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (msgs, options, token) => capturedOptions = options
            )
            .ReturnsAsync([new TextMessage { Text = "Response", Role = Role.Assistant }]);

        // Act
        _ = await middleware.InvokeAsync(context, mockAgent.Object);

        // Assert
        Assert.NotNull(capturedOptions);
        Assert.Equal(overridingOptions.Temperature, capturedOptions.Temperature);
        Assert.Equal(overridingOptions.MaxToken, capturedOptions.MaxToken);
        Assert.Equal(overridingOptions.TopP, capturedOptions.TopP);
    }

    [Fact]
    public async Task InvokeAsync_ShouldPreserveArrayOptions_WhenMergingOptions()
    {
        // Arrange
        // Set up original options with functions array
        var originalFunctions = new[]
        {
            new FunctionContract { Name = "originalFunction", Description = "Original function" },
        };

        var originalOptions = new GenerateReplyOptions { Functions = originalFunctions };

        // Set up overriding options with different functions array
        var overridingFunctions = new[]
        {
            new FunctionContract { Name = "overridingFunction", Description = "Overriding function" },
        };

        var overridingOptions = new GenerateReplyOptions { Functions = overridingFunctions };

        // Create the middleware with overriding options
        var middleware = new OptionsOverridingMiddleware(overridingOptions);

        // Create a message for the context
        var message = new TextMessage { Text = "Test message", Role = Role.User };

        // Create the context with original options
        var context = new MiddlewareContext([message], originalOptions);

        // Set up mock agent to capture the options that are passed to it
        var mockAgent = new Mock<IAgent>();
        GenerateReplyOptions? capturedOptions = null;

        _ = mockAgent
            .Setup(a =>
                a.GenerateReplyAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (msgs, options, token) => capturedOptions = options
            )
            .ReturnsAsync([new TextMessage { Text = "Response", Role = Role.Assistant }]);

        // Act
        _ = await middleware.InvokeAsync(context, mockAgent.Object);

        // Assert
        Assert.NotNull(capturedOptions);
        Assert.NotNull(capturedOptions.Functions);
        Assert.Contains(capturedOptions.Functions, f => f.Name == "overridingFunction");
        Assert.DoesNotContain(capturedOptions.Functions, f => f.Name == "originalFunction");
    }

    [Fact]
    public async Task InvokeStreamingAsync_ShouldOverrideOptions_WhenProvidedWithOverrides()
    {
        // Arrange
        // Set up original options and overriding options
        var originalOptions = new GenerateReplyOptions
        {
            Temperature = 0.7f,
            MaxToken = 100,
            TopP = 0.8f,
        };

        var overridingOptions = new GenerateReplyOptions
        {
            Temperature = 0.3f,
            TopP = 0.9f,
            // Intentionally not setting MaxToken to test partial overrides
        };

        // Create the middleware with overriding options
        var middleware = new OptionsOverridingMiddleware(overridingOptions);

        // Create a message for the context
        var message = new TextMessage { Text = "Test message", Role = Role.User };

        // Create the context with original options
        var context = new MiddlewareContext([message], originalOptions);

        // Set up mock streaming agent to capture the options that are passed to it
        var mockStreamingAgent = new Mock<IStreamingAgent>();
        GenerateReplyOptions? capturedOptions = null;

        _ = mockStreamingAgent
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (msgs, options, token) => capturedOptions = options
            )
            .ReturnsAsync(
                new[]
                {
                    new TextMessage { Text = "Response", Role = Role.Assistant },
                }.ToAsyncEnumerable()
            );

        // Act
        _ = await middleware.InvokeStreamingAsync(context, mockStreamingAgent.Object);

        // Assert
        Assert.NotNull(capturedOptions);
        Assert.Equal(overridingOptions.Temperature, capturedOptions.Temperature);
        Assert.Equal(overridingOptions.TopP, capturedOptions.TopP);
        Assert.Equal(originalOptions.MaxToken, capturedOptions.MaxToken); // Should keep original value
    }

    [Fact]
    public async Task InvokeAsync_ShouldHandleExtraProperties_WithProperMerging()
    {
        // Arrange
        // Set up original options with ExtraProperties
        var originalOptions = new GenerateReplyOptions
        {
            ExtraProperties = new Dictionary<string, object?>
            {
                ["originalProp1"] = "value1",
                ["originalProp2"] = 123,
                ["propToOverride"] = "originalValue",
            }.ToImmutableDictionary(),
        };

        // Set up overriding options that should add new properties,
        // and override an existing property
        var overridingOptions = new GenerateReplyOptions
        {
            ExtraProperties = new Dictionary<string, object?>
            {
                ["newProp1"] = "newValue1",
                ["propToOverride"] = "overriddenValue",
                ["strProp"] = "strProp", // This will remain null in the merged result
            }.ToImmutableDictionary(),
        };

        // Create the middleware with overriding options
        var middleware = new OptionsOverridingMiddleware(overridingOptions);

        // Create a message for the context
        var message = new TextMessage { Text = "Test message", Role = Role.User };

        // Create the context with original options
        var context = new MiddlewareContext([message], originalOptions);

        // Set up mock agent to capture the options that are passed to it
        var mockAgent = new Mock<IAgent>();
        GenerateReplyOptions? capturedOptions = null;

        _ = mockAgent
            .Setup(a =>
                a.GenerateReplyAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (msgs, options, token) => capturedOptions = options
            )
            .ReturnsAsync([new TextMessage { Text = "Response", Role = Role.Assistant }]);

        // Act
        _ = await middleware.InvokeAsync(context, mockAgent.Object);

        // Assert
        Assert.NotNull(capturedOptions);
        Assert.NotNull(capturedOptions.ExtraProperties);

        // Check original properties are preserved
        Assert.Equal("value1", capturedOptions.ExtraProperties["originalProp1"]);
        Assert.Equal(123, capturedOptions.ExtraProperties["originalProp2"]);

        // Check new property is added
        Assert.Equal("newValue1", capturedOptions.ExtraProperties["newProp1"]);

        // Check overridden property has new value
        Assert.Equal("overriddenValue", capturedOptions.ExtraProperties["propToOverride"]);
    }

    [Fact]
    public async Task InvokeAsync_ShouldHandleNestedExtraProperties_WithProperMerging()
    {
        // Arrange
        // Set up original options with nested ExtraProperties
        var originalOptions = new GenerateReplyOptions
        {
            ExtraProperties = new Dictionary<string, object?>
            {
                ["simple"] = "value",
                ["nested"] = new Dictionary<string, object?>
                {
                    ["original1"] = "originalNested1",
                    ["original2"] = "originalNested2",
                    ["toOverride"] = "originalNestedOverride",
                },
            }.ToImmutableDictionary(),
        };

        // Set up overriding options with nested dictionary
        var overridingOptions = new GenerateReplyOptions
        {
            ExtraProperties = new Dictionary<string, object?>
            {
                ["nested"] = new Dictionary<string, object?>
                {
                    ["new1"] = "newNested1",
                    ["toOverride"] = "newNestedOverride",
                    ["nullValue"] = null,
                },
            }.ToImmutableDictionary(),
        };

        // Create the middleware with overriding options
        var middleware = new OptionsOverridingMiddleware(overridingOptions);

        // Create a message for the context
        var message = new TextMessage { Text = "Test message", Role = Role.User };

        // Create the context with original options
        var context = new MiddlewareContext([message], originalOptions);

        // Set up mock agent to capture the options that are passed to it
        var mockAgent = new Mock<IAgent>();
        GenerateReplyOptions? capturedOptions = null;

        _ = mockAgent
            .Setup(a =>
                a.GenerateReplyAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (msgs, options, token) => capturedOptions = options
            )
            .ReturnsAsync([new TextMessage { Text = "Response", Role = Role.Assistant }]);

        // Act
        _ = await middleware.InvokeAsync(context, mockAgent.Object);

        // Assert
        Assert.NotNull(capturedOptions);
        Assert.NotNull(capturedOptions.ExtraProperties);

        // Verify top-level property is preserved
        Assert.Equal("value", capturedOptions.ExtraProperties["simple"]);

        // Get the nested dictionary
        var nested = capturedOptions.ExtraProperties["nested"] as Dictionary<string, object?>;
        Assert.NotNull(nested);

        // Verify original nested properties are preserved in deep merge
        Assert.Equal("originalNested1", nested["original1"]);
        Assert.Equal("originalNested2", nested["original2"]);

        // Verify new nested property is added
        Assert.Equal("newNested1", nested["new1"]);

        // Verify overridden nested property has new value
        Assert.Equal("newNestedOverride", nested["toOverride"]);

        // Verify null property is present in the nested dictionary
        Assert.True(nested.ContainsKey("nullValue"));
        Assert.Null(nested["nullValue"]);
    }

    #endregion
}
