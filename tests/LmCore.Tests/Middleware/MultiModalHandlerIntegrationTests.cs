using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Middleware;

/// <summary>
///     Verifies that a unified <see cref="ToolHandlerResult.Resolved"/> handler whose wrapped
///     <see cref="ToolCallResult"/> carries <see cref="ToolResultContentBlock"/>s propagates
///     those blocks through <see cref="FunctionCallMiddleware"/> and <see cref="FunctionRegistry"/>
///     to the result message.
/// </summary>
public class MultiModalHandlerIntegrationTests
{
    [Fact]
    public void FunctionRegistry_Build_RetainsHandlerThatReturnsContentBlocks()
    {
        var multiModalHandler = new ToolHandler((_, _) =>
            Task.FromResult<ToolHandlerResult>(new ToolHandlerResult.Resolved(
                new ToolCallResult(null, "multimodal", new List<ToolResultContentBlock>
                {
                    new TextToolResultBlock { Text = "some text" },
                    new ImageToolResultBlock { Data = "base64img", MimeType = "image/jpeg" },
                }))));

        var registry = new FunctionRegistry();
        registry.AddFunction(
            new FunctionContract { Name = "mcp_tool", Description = "MCP tool with images" },
            multiModalHandler);

        var (contracts, handlers) = registry.Build();

        Assert.Single(contracts);
        Assert.Single(handlers);
        Assert.True(handlers.ContainsKey("mcp_tool"));
    }

    [Fact]
    public async Task FunctionCallMiddleware_PropagatesContentBlocksFromUnifiedHandler()
    {
        var functions = new List<FunctionContract>
        {
            new() { Name = "image_tool", Description = "Returns images" },
        };

        var functionMap = new Dictionary<string, ToolHandler>
        {
            ["image_tool"] = (_, _) => Task.FromResult<ToolHandlerResult>(
                new ToolHandlerResult.Resolved(
                    new ToolCallResult(null, "multimodal text", new List<ToolResultContentBlock>
                    {
                        new TextToolResultBlock { Text = "rich text" },
                        new ImageToolResultBlock { Data = "aW1hZ2VkYXRh", MimeType = "image/png" },
                    }))),
        };

        var middleware = new FunctionCallMiddleware(functions, functionMap);

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

        var result = await middleware.InvokeAsync(context, mockAgent.Object);

        var resultMessage = Assert.IsType<ToolsCallResultMessage>(result.First());
        var toolCallResult = resultMessage.ToolCallResults.First();
        Assert.Equal("multimodal text", toolCallResult.Result);
        Assert.NotNull(toolCallResult.ContentBlocks);
        Assert.Equal(2, toolCallResult.ContentBlocks!.Count);
        Assert.IsType<TextToolResultBlock>(toolCallResult.ContentBlocks[0]);
        Assert.IsType<ImageToolResultBlock>(toolCallResult.ContentBlocks[1]);
    }

    [Fact]
    public void FunctionRegistry_BuildMiddleware_AcceptsContentBlockHandlers()
    {
        var multiModalHandler = new ToolHandler((_, _) =>
            Task.FromResult<ToolHandlerResult>(
                new ToolHandlerResult.Resolved(new ToolCallResult(null, "mm result")))
        );

        var registry = new FunctionRegistry();
        registry.AddFunction(
            new FunctionContract { Name = "mm_func", Description = "Multimodal function" },
            multiModalHandler);

        var middleware = registry.BuildMiddleware("TestMiddleware");

        Assert.NotNull(middleware);
        Assert.Equal("TestMiddleware", middleware.Name);
    }
}
