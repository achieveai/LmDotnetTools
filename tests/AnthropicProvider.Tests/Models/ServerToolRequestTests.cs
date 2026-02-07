using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Tests.Models;

public class ServerToolRequestTests
{
    private static readonly JsonSerializerOptions _jsonOptions =
        AnthropicJsonSerializerOptionsFactory.CreateUniversal();

    [Fact]
    public void FromMessages_WithBuiltInTools_IncludesToolsInRequest()
    {
        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.User, Text = "What's the weather?" },
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "claude-sonnet-4-20250514",
            BuiltInTools = new List<object>
            {
                new AnthropicWebSearchTool { MaxUses = 5 },
            },
        };

        var request = AnthropicRequest.FromMessages(messages, options);

        Assert.NotNull(request.Tools);
        Assert.Single(request.Tools);

        // Serialize the request and verify the tool type
        var json = JsonSerializer.Serialize(request, _jsonOptions);
        var doc = JsonDocument.Parse(json);
        var tools = doc.RootElement.GetProperty("tools");
        Assert.Equal(1, tools.GetArrayLength());

        var tool = tools[0];
        Assert.Equal("web_search_20250305", tool.GetProperty("type").GetString());
        Assert.Equal("web_search", tool.GetProperty("name").GetString());
        Assert.Equal(5, tool.GetProperty("max_uses").GetInt32());
    }

    [Fact]
    public void FromMessages_WithBuiltInAndFunctionTools_CombinesBoth()
    {
        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.User, Text = "Search and get weather." },
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "claude-sonnet-4-20250514",
            BuiltInTools = new List<object>
            {
                new AnthropicWebSearchTool(),
            },
            Functions =
            [
                new FunctionContract
                {
                    Name = "get_weather",
                    Description = "Get weather info",
                    Parameters =
                    [
                        new FunctionParameterContract
                        {
                            Name = "location",
                            ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(string)),
                        },
                    ],
                },
            ],
        };

        var request = AnthropicRequest.FromMessages(messages, options);

        Assert.NotNull(request.Tools);
        Assert.Equal(2, request.Tools.Count);

        // Serialize and verify both tool types are present
        var json = JsonSerializer.Serialize(request, _jsonOptions);
        var doc = JsonDocument.Parse(json);
        var tools = doc.RootElement.GetProperty("tools");
        Assert.Equal(2, tools.GetArrayLength());

        // First should be built-in (added first)
        Assert.Equal("web_search_20250305", tools[0].GetProperty("type").GetString());
        // Second should be function tool
        Assert.Equal("get_weather", tools[1].GetProperty("name").GetString());
        Assert.True(tools[1].TryGetProperty("input_schema", out _));
    }

    [Fact]
    public void FromMessages_ContainingServerToolUseMessage_RoundTrips()
    {
        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.User, Text = "What's the weather?" },
            new TextMessage { Role = Role.Assistant, Text = "Let me search." },
            new ServerToolUseMessage
            {
                ToolUseId = "srvtoolu_01",
                ToolName = "web_search",
                Input = JsonSerializer.Deserialize<JsonElement>("""{"query":"weather SF"}"""),
                Role = Role.Assistant,
            },
            new ServerToolResultMessage
            {
                ToolUseId = "srvtoolu_01",
                ToolName = "web_search",
                Result = JsonSerializer.Deserialize<JsonElement>(
                    """[{"type":"web_search_result","url":"https://example.com","title":"Weather"}]"""
                ),
                Role = Role.Assistant,
            },
            new TextMessage { Role = Role.Assistant, Text = "The weather is sunny." },
            new TextMessage { Role = Role.User, Text = "What about tomorrow?" },
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "claude-sonnet-4-20250514",
            BuiltInTools = new List<object> { new AnthropicWebSearchTool() },
        };

        var request = AnthropicRequest.FromMessages(messages, options);

        // Serialize and inspect the messages
        var json = JsonSerializer.Serialize(request, _jsonOptions);
        var doc = JsonDocument.Parse(json);
        var requestMessages = doc.RootElement.GetProperty("messages");

        // Find assistant message with server_tool_use content
        var hasServerToolUse = false;
        var hasServerToolResult = false;

        foreach (var msg in requestMessages.EnumerateArray())
        {
            if (msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out var blockType))
                    {
                        if (blockType.GetString() == "server_tool_use")
                        {
                            hasServerToolUse = true;
                            Assert.Equal("srvtoolu_01", block.GetProperty("id").GetString());
                            Assert.Equal("web_search", block.GetProperty("name").GetString());
                        }

                        if (blockType.GetString() == "web_search_tool_result")
                        {
                            hasServerToolResult = true;
                            Assert.Equal("srvtoolu_01", block.GetProperty("tool_use_id").GetString());
                        }
                    }
                }
            }
        }

        Assert.True(hasServerToolUse, "Expected server_tool_use content block in serialized request");
        Assert.True(hasServerToolResult, "Expected web_search_tool_result content block in serialized request");
    }
}
