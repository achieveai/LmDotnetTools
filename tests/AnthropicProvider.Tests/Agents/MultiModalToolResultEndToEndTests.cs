using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Tests.Agents;

/// <summary>
/// End-to-end test that verifies the full round-trip for multimodal tool results:
/// LLM returns tool_use -> FunctionCallMiddleware executes multimodal handler ->
/// tool_result with images sent back to LLM -> verify the outbound HTTP request
/// has correct Anthropic API format (array content with image blocks).
/// </summary>
public class MultiModalToolResultEndToEndTests : LoggingTestBase
{
    public MultiModalToolResultEndToEndTests(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public async Task MultiModalToolResult_SentToAnthropicWithImageContentBlocks()
    {
        // Arrange: create agent with request capture to inspect outbound HTTP bodies
        var requestCapture = new RequestCapture();
        var httpClient = TestModeHttpClientFactory.CreateAnthropicTestClient(
            LoggerFactory, requestCapture, chunkDelayMs: 0);
        var anthropicClient = new AnthropicClient("test-api-key", httpClient: httpClient);
        var agent = new AnthropicAgent("TestAgent", anthropicClient);

        // Define a tool contract for a search tool
        var toolContract = new FunctionContract
        {
            Name = "search_tool",
            Description = "Search content",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "query",
                    IsRequired = true,
                    ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(string)),
                },
            ],
        };

        // Text-only function map (required by FunctionCallMiddleware validation)
        var functionMap = new Dictionary<string, Func<string, Task<string>>>
        {
            ["search_tool"] = _ => Task.FromResult("Search result text"),
        };

        // Multimodal function map: simulates an MCP tool returning text + image
        var mmFunctionMap = new Dictionary<string, Func<string, Task<ToolCallResult>>>
        {
            ["search_tool"] = _ => Task.FromResult(new ToolCallResult(
                null,
                "Here is a medical diagram:",
                [
                    new TextToolResultBlock { Text = "Here is a medical diagram:" },
                    new ImageToolResultBlock
                    {
                        Data = CreateMinimalPngBase64(),
                        MimeType = "image/png",
                    },
                ])),
        };

        // Build middleware and wrap agent for the full pipeline
        var middleware = new FunctionCallMiddleware(
            [toolContract], functionMap, mmFunctionMap);
        var agentWithMiddleware = agent.WithMiddleware(middleware);
        Logger.LogDebug("Agent wrapped with FunctionCallMiddleware (multimodal enabled)");

        // Instruction chain: turn 1 triggers a tool_use response from the test handler
        var userMessage = """
            Search for cardiac anatomy
            <|instruction_start|>
            {"instruction_chain":[
                {"id_message":"tool-call","messages":[
                    {"tool_call":[{"name":"search_tool","args":{"query":"cardiac anatomy"}}]}
                ]},
                {"id_message":"echo-request","messages":[
                    {"explicit_text":"__REQUEST_PARAMS__:messages"}
                ]}
            ]}
            <|instruction_end|>
            """;

        var options = new GenerateReplyOptions
        {
            ModelId = "claude-sonnet-4-20250514",
            Functions = [toolContract],
        };

        // Act - Turn 1: user message -> LLM returns tool_use -> middleware executes
        // multimodal handler -> returns ToolsCallAggregateMessage with image content
        Logger.LogInformation("Turn 1: Sending user message to trigger tool_use");
        var turn1Response = await agentWithMiddleware.GenerateReplyAsync(
            [new TextMessage { Role = Role.User, Text = userMessage }],
            options);

        var turn1Messages = turn1Response.ToList();
        Logger.LogInformation("Turn 1 returned {Count} messages", turn1Messages.Count);

        // Verify turn 1 produced a ToolsCallAggregateMessage
        var aggregateMsg = turn1Messages.OfType<ToolsCallAggregateMessage>().FirstOrDefault();
        Assert.NotNull(aggregateMsg);
        Logger.LogDebug("Turn 1: Got ToolsCallAggregateMessage with {Count} tool results",
            aggregateMsg.ToolsCallResult.ToolCallResults.Count);

        // Act - Turn 2: send conversation history (user + aggregate) back to agent.
        // This triggers HTTP request #2, which contains the tool_result with images.
        Logger.LogInformation("Turn 2: Sending conversation with tool_result to LLM");
        var turn2Response = await agentWithMiddleware.GenerateReplyAsync(
            [
                new TextMessage { Role = Role.User, Text = userMessage },
                aggregateMsg,
            ],
            options);

        var turn2Messages = turn2Response.ToList();
        Logger.LogInformation("Turn 2 returned {Count} messages", turn2Messages.Count);

        // Assert: at least 2 HTTP requests were made (turn 1 + turn 2)
        Assert.True(requestCapture.RequestCount >= 2,
            $"Expected >= 2 HTTP requests, got {requestCapture.RequestCount}");

        // Inspect the 2nd HTTP request body, which carries the tool_result
        var secondRequestBody = requestCapture.RequestBodies[1];
        Logger.LogDebug("Second request body length: {Length}", secondRequestBody.Length);

        var doc = JsonDocument.Parse(secondRequestBody);
        var messagesArray = doc.RootElement.GetProperty("messages");

        // Find the user message containing the tool_result content block
        JsonElement? toolResultBlock = FindToolResultBlock(messagesArray);
        Assert.NotNull(toolResultBlock);
        Logger.LogDebug("Found tool_result block in second HTTP request");

        // Verify tool_result "content" is an array (multimodal format, not string)
        var toolContent = toolResultBlock.Value.GetProperty("content");
        Assert.Equal(JsonValueKind.Array, toolContent.ValueKind);
        Logger.LogDebug("tool_result content is array with {Count} elements",
            toolContent.GetArrayLength());

        // Verify text block is present with correct content
        var textBlock = toolContent.EnumerateArray()
            .First(b => b.GetProperty("type").GetString() == "text");
        Assert.Equal("Here is a medical diagram:", textBlock.GetProperty("text").GetString());

        // Verify image block has correct base64 source structure
        var imageBlock = toolContent.EnumerateArray()
            .First(b => b.GetProperty("type").GetString() == "image");
        var source = imageBlock.GetProperty("source");
        Assert.Equal("base64", source.GetProperty("type").GetString());
        Assert.Equal("image/png", source.GetProperty("media_type").GetString());
        Assert.False(string.IsNullOrEmpty(source.GetProperty("data").GetString()),
            "Image data should not be empty");

        Logger.LogInformation("All assertions passed: multimodal tool_result correctly formatted");
    }

    /// <summary>
    /// Searches the Anthropic messages array for a user message containing a tool_result block.
    /// </summary>
    private static JsonElement? FindToolResultBlock(JsonElement messagesArray)
    {
        foreach (var msg in messagesArray.EnumerateArray())
        {
            if (msg.GetProperty("role").GetString() != "user")
            {
                continue;
            }

            if (!msg.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var t)
                    && t.GetString() == "tool_result")
                {
                    return block;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Creates a minimal valid 1x1 transparent PNG encoded as base64.
    /// Used as test image data for multimodal tool results.
    /// </summary>
    private static string CreateMinimalPngBase64()
    {
        byte[] png =
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
            0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
            0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41,
            0x54, 0x78, 0x9C, 0x62, 0x00, 0x00, 0x00, 0x02,
            0x00, 0x01, 0xE5, 0x27, 0xDE, 0xFC, 0x00, 0x00,
            0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42,
            0x60, 0x82,
        ];
        return Convert.ToBase64String(png);
    }
}
