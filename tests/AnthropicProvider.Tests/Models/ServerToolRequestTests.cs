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
    public void FromMessages_ContainingProviderServerToolMessages_RoundTrips()
    {
        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.User, Text = "What's the weather?" },
            new TextMessage { Role = Role.Assistant, Text = "Let me search." },
            new ToolCallMessage
            {
                ToolCallId = "srvtoolu_01",
                FunctionName = "web_search",
                FunctionArgs = """{"query":"weather SF"}""",
                ExecutionTarget = ExecutionTarget.ProviderServer,
                Role = Role.Assistant,
            },
            new ToolCallResultMessage
            {
                ToolCallId = "srvtoolu_01",
                ToolName = "web_search",
                Result = """[{"type":"web_search_result","url":"https://example.com","title":"Weather"}]""",
                ExecutionTarget = ExecutionTarget.ProviderServer,
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

        // Find assistant message with server_tool_use and user message with tool_result
        var hasServerToolUse = false;
        var hasToolResult = false;

        foreach (var msg in requestMessages.EnumerateArray())
        {
            var role = msg.GetProperty("role").GetString();
            if (msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out var blockType))
                    {
                        if (blockType.GetString() == "server_tool_use")
                        {
                            hasServerToolUse = true;
                            Assert.Equal("assistant", role);
                            Assert.Equal("srvtoolu_01", block.GetProperty("id").GetString());
                            Assert.Equal("web_search", block.GetProperty("name").GetString());
                        }

                        // Provider server tool results are converted to tool_result in a user message,
                        // since providers like Kimi treat server_tool_use as regular tool_use
                        // and require a matching tool_result in the next user turn.
                        if (blockType.GetString() == "tool_result"
                            && block.TryGetProperty("tool_use_id", out var toolUseId)
                            && toolUseId.GetString() == "srvtoolu_01")
                        {
                            hasToolResult = true;
                            Assert.Equal("user", role);
                        }
                    }
                }
            }
        }

        Assert.True(hasServerToolUse, "Expected server_tool_use content block in serialized request");
        Assert.True(hasToolResult, "Expected tool_result content block in user message for server tool result");
    }

    [Fact]
    public void FromMessages_FullKimiLikeFlow_ProducesCorrectMergedWireJson()
    {
        // Full IMessage sequence: User text, Assistant text + ServerToolUseMessage,
        // ServerToolResultMessage, Assistant text, User text
        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.User, Text = "Search for AI companies." },
            new TextMessage { Role = Role.Assistant, Text = "Let me search for that." },
            new ServerToolUseMessage
            {
                ToolUseId = "srvtoolu_kimi_01",
                ToolName = "web_search",
                Input = JsonSerializer.Deserialize<JsonElement>("""{"query":"top AI companies 2026"}"""),
                Role = Role.Assistant,
            },
            new ServerToolResultMessage
            {
                ToolUseId = "srvtoolu_kimi_01",
                ToolName = "web_search",
                Result = JsonSerializer.Deserialize<JsonElement>(
                    """[{"type":"web_search_result","content":null,"url":"https://example.com","title":"AI Companies"}]"""
                ),
                Role = Role.Assistant,
            },
            new TextMessage { Role = Role.Assistant, Text = "Based on my search, here are the top AI companies." },
            new TextMessage { Role = Role.User, Text = "Tell me more about Anthropic." },
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "claude-sonnet-4-20250514",
            BuiltInTools = new List<object> { new AnthropicWebSearchTool() },
        };

        var request = AnthropicRequest.FromMessages(messages, options);
        var json = JsonSerializer.Serialize(request, _jsonOptions);
        var doc = JsonDocument.Parse(json);
        var requestMessages = doc.RootElement.GetProperty("messages");

        // Verify no consecutive same-role messages
        string? previousRole = null;
        var hasServerToolUse = false;
        var hasToolResult = false;

        foreach (var msg in requestMessages.EnumerateArray())
        {
            var role = msg.GetProperty("role").GetString()!;
            Assert.NotEqual(previousRole, role, StringComparer.Ordinal);
            previousRole = role;

            if (msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    if (!block.TryGetProperty("type", out var blockType))
                    {
                        continue;
                    }

                    var typeStr = blockType.GetString();
                    if (typeStr == "server_tool_use")
                    {
                        hasServerToolUse = true;
                        Assert.Equal("assistant", role);
                        Assert.Equal("srvtoolu_kimi_01", block.GetProperty("id").GetString());
                        Assert.Equal("web_search", block.GetProperty("name").GetString());
                    }

                    if (typeStr == "tool_result"
                        && block.TryGetProperty("tool_use_id", out var toolUseId)
                        && toolUseId.GetString() == "srvtoolu_kimi_01")
                    {
                        hasToolResult = true;
                        Assert.Equal("user", role);
                    }
                }
            }
        }

        Assert.True(hasServerToolUse, "Expected server_tool_use in assistant message");
        Assert.True(hasToolResult, "Expected tool_result in user message");
    }

    [Fact]
    public void FromMessages_WithContentNull_SerializesCorrectly()
    {
        // ServerToolResultMessage with Result containing content: null
        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.User, Text = "Search for something." },
            new ServerToolUseMessage
            {
                ToolUseId = "srvtoolu_null_01",
                ToolName = "web_search",
                Input = JsonSerializer.Deserialize<JsonElement>("""{"query":"test"}"""),
                Role = Role.Assistant,
            },
            new ServerToolResultMessage
            {
                ToolUseId = "srvtoolu_null_01",
                ToolName = "web_search",
                Result = JsonSerializer.Deserialize<JsonElement>(
                    """[{"type":"web_search_result","content":null,"url":"https://example.com","title":"Test"}]"""
                ),
                Role = Role.Assistant,
            },
            new TextMessage { Role = Role.User, Text = "What did you find?" },
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "claude-sonnet-4-20250514",
            BuiltInTools = new List<object> { new AnthropicWebSearchTool() },
        };

        var request = AnthropicRequest.FromMessages(messages, options);
        var json = JsonSerializer.Serialize(request, _jsonOptions);

        // Parse and find the tool_result block
        var doc = JsonDocument.Parse(json);
        var requestMessages = doc.RootElement.GetProperty("messages");

        var foundToolResult = false;
        foreach (var msg in requestMessages.EnumerateArray())
        {
            if (msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out var blockType)
                        && blockType.GetString() == "tool_result"
                        && block.TryGetProperty("tool_use_id", out var tuid)
                        && tuid.GetString() == "srvtoolu_null_01")
                    {
                        foundToolResult = true;

                        // The content field should contain the serialized result with content: null preserved
                        Assert.True(
                            block.TryGetProperty("content", out var resultContent),
                            "tool_result should have content field"
                        );

                        var resultStr = resultContent.GetString()!;
                        Assert.Contains("content", resultStr);
                        Assert.Contains("null", resultStr);
                    }
                }
            }
        }

        Assert.True(foundToolResult, "Expected tool_result block with null content in serialized request");
    }

    [Fact]
    public void FromMessages_TextWithCitations_BecomesPlainTextInHistory()
    {
        // TextWithCitationsMessage should serialize as {"type":"text","text":"..."} without citations
        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.User, Text = "Search for AI news." },
            new TextWithCitationsMessage
            {
                Text = "Based on my search, AI developments in 2026 are significant.",
                Citations =
                [
                    new CitationInfo
                    {
                        Type = "web_search_result_location",
                        Url = "https://example.com/ai",
                        Title = "AI News",
                        CitedText = "AI developments in 2026",
                    },
                ],
                Role = Role.Assistant,
            },
            new TextMessage { Role = Role.User, Text = "Tell me more." },
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "claude-sonnet-4-20250514",
        };

        var request = AnthropicRequest.FromMessages(messages, options);
        var json = JsonSerializer.Serialize(request, _jsonOptions);
        var doc = JsonDocument.Parse(json);
        var requestMessages = doc.RootElement.GetProperty("messages");

        // Find the assistant message and verify it has plain text, not citations
        var foundAssistantText = false;
        foreach (var msg in requestMessages.EnumerateArray())
        {
            if (msg.GetProperty("role").GetString() != "assistant")
            {
                continue;
            }

            if (msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out var blockType)
                        && blockType.GetString() == "text")
                    {
                        foundAssistantText = true;
                        Assert.Equal(
                            "Based on my search, AI developments in 2026 are significant.",
                            block.GetProperty("text").GetString()
                        );

                        // Should NOT have citations in the serialized form
                        Assert.False(
                            block.TryGetProperty("citations", out _),
                            "TextWithCitationsMessage should not include citations when serialized in history"
                        );
                    }
                }
            }
        }

        Assert.True(foundAssistantText, "Expected assistant text block in serialized request");
    }
}
