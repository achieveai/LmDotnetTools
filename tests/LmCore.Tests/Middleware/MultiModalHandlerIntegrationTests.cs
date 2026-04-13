using AchieveAi.LmDotnetTools.LmCore.Core;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Middleware;

/// <summary>
///     Tests verifying that FunctionDescriptor's MultiModalHandler flows through
///     FunctionRegistry and FunctionCallMiddleware correctly.
///     These tests simulate the pattern used by McpClientFunctionProvider.CreateAsync
///     after Step 4 changes: both Handler and MultiModalHandler are populated.
/// </summary>
public class MultiModalHandlerIntegrationTests
{
    [Fact]
    public void FunctionDescriptor_WithMultiModalHandler_PreservesHandler()
    {
        // Arrange & Act
        var descriptor = new FunctionDescriptor
        {
            Contract = new FunctionContract { Name = "test_tool", Description = "A test tool" },
            Handler = _ => Task.FromResult("text result"),
            MultiModalHandler = _ =>
                Task.FromResult(
                    new ToolCallResult(null, "text result", new List<ToolResultContentBlock>
                    {
                        new ImageToolResultBlock { Data = "base64data", MimeType = "image/png" },
                    })
                ),
            ProviderName = "McpClient",
        };

        // Assert
        Assert.True(descriptor.HasMultiModalHandler);
        Assert.NotNull(descriptor.MultiModalHandler);
        Assert.NotNull(descriptor.Handler);
    }

    [Fact]
    public void FunctionDescriptor_WithoutMultiModalHandler_HasNoHandler()
    {
        // Arrange & Act - simulates a non-MCP function
        var descriptor = new FunctionDescriptor
        {
            Contract = new FunctionContract { Name = "local_func", Description = "A local function" },
            Handler = _ => Task.FromResult("text result"),
            ProviderName = "Local",
        };

        // Assert
        Assert.False(descriptor.HasMultiModalHandler);
        Assert.Null(descriptor.MultiModalHandler);
    }

    [Fact]
    public void FunctionRegistry_BuildWithMultiModal_IncludesMultiModalHandlers()
    {
        // Arrange - simulates McpClientFunctionProvider producing descriptors with both handlers
        var textHandler = new Func<string, Task<string>>(_ => Task.FromResult("text only"));
        var multiModalHandler = new Func<string, Task<ToolCallResult>>(_ =>
            Task.FromResult(new ToolCallResult(null, "multimodal", new List<ToolResultContentBlock>
            {
                new TextToolResultBlock { Text = "some text" },
                new ImageToolResultBlock { Data = "base64img", MimeType = "image/jpeg" },
            }))
        );

        var provider = new TestFunctionProvider("McpClient", new[]
        {
            new FunctionDescriptor
            {
                Contract = new FunctionContract { Name = "mcp_tool", Description = "MCP tool with images" },
                Handler = textHandler,
                MultiModalHandler = multiModalHandler,
                ProviderName = "McpClient",
            },
            new FunctionDescriptor
            {
                Contract = new FunctionContract { Name = "local_tool", Description = "Local tool, text only" },
                Handler = _ => Task.FromResult("local result"),
                ProviderName = "Local",
            },
        });

        var registry = new FunctionRegistry();
        registry.AddProvider(provider);

        // Act
        var (contracts, textHandlers, multiModalHandlers) = registry.BuildWithMultiModal();

        // Assert
        Assert.Equal(2, contracts.Count());
        Assert.Equal(2, textHandlers.Count);
        // Only the MCP tool should have a multimodal handler
        Assert.Single(multiModalHandlers);
        Assert.True(multiModalHandlers.ContainsKey("mcp_tool"));
        Assert.False(multiModalHandlers.ContainsKey("local_tool"));
    }

    [Fact]
    public async Task FunctionCallMiddleware_WithMultiModalMap_PrefersMultiModalHandler()
    {
        // Arrange - simulates the middleware receiving both maps (as produced by FunctionRegistry.BuildWithMultiModal)
        var functions = new List<FunctionContract>
        {
            new() { Name = "image_tool", Description = "Returns images" },
        };

        var functionMap = new Dictionary<string, Func<string, Task<string>>>
        {
            ["image_tool"] = _ => Task.FromResult("fallback text"),
        };

        var multiModalMap = new Dictionary<string, Func<string, Task<ToolCallResult>>>
        {
            ["image_tool"] = _ =>
                Task.FromResult(new ToolCallResult(null, "multimodal text", new List<ToolResultContentBlock>
                {
                    new TextToolResultBlock { Text = "rich text" },
                    new ImageToolResultBlock { Data = "aW1hZ2VkYXRh", MimeType = "image/png" },
                })),
        };

        var middleware = new FunctionCallMiddleware(functions, functionMap, multiModalMap);

        var toolCallMessage = new ToolsCallMessage
        {
            ToolCalls =
            [
                new ToolCall
                {
                    FunctionName = "image_tool",
                    FunctionArgs = "{}",
                    ToolCallId = "call_1",
                },
            ],
            Role = Role.Assistant,
        };
        var context = new MiddlewareContext([toolCallMessage]);
        var mockAgent = new Mock<IAgent>();

        // Act
        var result = await middleware.InvokeAsync(context, mockAgent.Object);

        // Assert - should use the multimodal handler, so result should contain ContentBlocks
        Assert.NotNull(result);
        var resultMessage = Assert.IsType<ToolsCallResultMessage>(result.First());
        var toolCallResult = resultMessage.ToolCallResults.First();
        Assert.Equal("multimodal text", toolCallResult.Result);
        Assert.NotNull(toolCallResult.ContentBlocks);
        Assert.Equal(2, toolCallResult.ContentBlocks!.Count);
        Assert.IsType<TextToolResultBlock>(toolCallResult.ContentBlocks[0]);
        Assert.IsType<ImageToolResultBlock>(toolCallResult.ContentBlocks[1]);
    }

    [Fact]
    public async Task FunctionCallMiddleware_WithMixedTools_UsesCorrectHandlerForEach()
    {
        // Arrange - one tool has multimodal, the other is text-only
        var functions = new List<FunctionContract>
        {
            new() { Name = "image_tool", Description = "Returns images" },
            new() { Name = "text_tool", Description = "Returns text only" },
        };

        var functionMap = new Dictionary<string, Func<string, Task<string>>>
        {
            ["image_tool"] = _ => Task.FromResult("image fallback"),
            ["text_tool"] = _ => Task.FromResult("text result"),
        };

        var multiModalMap = new Dictionary<string, Func<string, Task<ToolCallResult>>>
        {
            ["image_tool"] = _ =>
                Task.FromResult(new ToolCallResult(null, "image multimodal", new List<ToolResultContentBlock>
                {
                    new ImageToolResultBlock { Data = "cG5nZGF0YQ==", MimeType = "image/png" },
                })),
        };

        var middleware = new FunctionCallMiddleware(functions, functionMap, multiModalMap);

        // Call text_tool (no multimodal handler)
        var textToolCall = new ToolsCallMessage
        {
            ToolCalls =
            [
                new ToolCall { FunctionName = "text_tool", FunctionArgs = "{}", ToolCallId = "call_text" },
            ],
            Role = Role.Assistant,
        };
        var textContext = new MiddlewareContext([textToolCall]);
        var mockAgent = new Mock<IAgent>();

        var textResult = await middleware.InvokeAsync(textContext, mockAgent.Object);
        var textResultMessage = Assert.IsType<ToolsCallResultMessage>(textResult.First());
        var textToolResult = textResultMessage.ToolCallResults.First();
        Assert.Equal("text result", textToolResult.Result);
        Assert.Null(textToolResult.ContentBlocks);

        // Call image_tool (has multimodal handler)
        var imageToolCall = new ToolsCallMessage
        {
            ToolCalls =
            [
                new ToolCall { FunctionName = "image_tool", FunctionArgs = "{}", ToolCallId = "call_image" },
            ],
            Role = Role.Assistant,
        };
        var imageContext = new MiddlewareContext([imageToolCall]);

        var imageResult = await middleware.InvokeAsync(imageContext, mockAgent.Object);
        var imageResultMessage = Assert.IsType<ToolsCallResultMessage>(imageResult.First());
        var imageToolResult = imageResultMessage.ToolCallResults.First();
        Assert.Equal("image multimodal", imageToolResult.Result);
        Assert.NotNull(imageToolResult.ContentBlocks);
        Assert.Single(imageToolResult.ContentBlocks!);
    }

    [Fact]
    public void FunctionRegistry_BuildMiddleware_PassesMultiModalHandlersThrough()
    {
        // Arrange - verify BuildMiddleware creates a middleware that has multimodal support
        var multiModalHandler = new Func<string, Task<ToolCallResult>>(_ =>
            Task.FromResult(new ToolCallResult(null, "mm result"))
        );

        var provider = new TestFunctionProvider("Test", new[]
        {
            new FunctionDescriptor
            {
                Contract = new FunctionContract { Name = "mm_func", Description = "Multimodal function" },
                Handler = _ => Task.FromResult("text"),
                MultiModalHandler = multiModalHandler,
                ProviderName = "Test",
            },
        });

        var registry = new FunctionRegistry();
        registry.AddProvider(provider);

        // Act - should not throw
        var middleware = registry.BuildMiddleware("TestMiddleware");

        // Assert
        Assert.NotNull(middleware);
        Assert.Equal("TestMiddleware", middleware.Name);
    }

    /// <summary>
    ///     Tests the streaming path (InvokeStreamingAsync) of FunctionCallMiddleware
    ///     when a multimodal tool is invoked. When the last message in context is a
    ///     ToolsCallMessage with a tool that has a multimodal handler, the streaming
    ///     middleware should execute the multimodal handler and return ContentBlocks.
    /// </summary>
    [Fact]
    public async Task FunctionCallMiddleware_InvokeStreamingAsync_WithMultiModalTool_ReturnsContentBlocks()
    {
        // Arrange - same tool setup as the non-streaming test, exercised through the streaming API
        var functions = new List<FunctionContract>
        {
            new() { Name = "screenshot_tool", Description = "Captures a screenshot" },
        };

        var functionMap = new Dictionary<string, Func<string, Task<string>>>
        {
            ["screenshot_tool"] = _ => Task.FromResult("fallback text"),
        };

        var multiModalMap = new Dictionary<string, Func<string, Task<ToolCallResult>>>
        {
            ["screenshot_tool"] = _ =>
                Task.FromResult(new ToolCallResult(null, "screenshot captured",
                    [
                        new TextToolResultBlock { Text = "Screenshot of the page" },
                        new ImageToolResultBlock { Data = "cG5nZGF0YQ==", MimeType = "image/png" },
                    ])),
        };

        var middleware = new FunctionCallMiddleware(functions, functionMap, multiModalMap);

        // The last message is a ToolsCallMessage, which triggers the pending-tool-calls path
        var toolCallMessage = new ToolsCallMessage
        {
            ToolCalls =
            [
                new ToolCall
                {
                    FunctionName = "screenshot_tool",
                    FunctionArgs = "{}",
                    ToolCallId = "call_stream_1",
                },
            ],
            Role = Role.Assistant,
        };
        var context = new MiddlewareContext([toolCallMessage]);

        // MockStreamingAgent is not invoked because the middleware handles pending tool calls directly
        var mockAgent = new MockStreamingAgent([]);

        // Act - use the streaming API
        var responseStream = await middleware.InvokeStreamingAsync(context, mockAgent);

        var results = new List<IMessage>();
        await foreach (var message in responseStream)
        {
            results.Add(message);
        }

        // Assert - the result should be a ToolsCallResultMessage with ContentBlocks
        Assert.Single(results);
        var resultMessage = Assert.IsType<ToolsCallResultMessage>(results[0]);
        var toolCallResult = resultMessage.ToolCallResults.First();

        Assert.Equal("screenshot captured", toolCallResult.Result);
        Assert.NotNull(toolCallResult.ContentBlocks);
        Assert.Equal(2, toolCallResult.ContentBlocks!.Count);
        Assert.IsType<TextToolResultBlock>(toolCallResult.ContentBlocks[0]);
        Assert.IsType<ImageToolResultBlock>(toolCallResult.ContentBlocks[1]);

        // Verify the image data is preserved
        var imageBlock = (ImageToolResultBlock)toolCallResult.ContentBlocks[1];
        Assert.Equal("cG5nZGF0YQ==", imageBlock.Data);
        Assert.Equal("image/png", imageBlock.MimeType);
    }

    /// <summary>
    ///     Simple test function provider for unit testing.
    /// </summary>
    private sealed class TestFunctionProvider : IFunctionProvider
    {
        private readonly IEnumerable<FunctionDescriptor> _functions;

        public TestFunctionProvider(string providerName, IEnumerable<FunctionDescriptor> functions)
        {
            ProviderName = providerName;
            _functions = functions;
        }

        public string ProviderName { get; }

        public int Priority => 100;

        public IEnumerable<FunctionDescriptor> GetFunctions() => _functions;
    }
}
