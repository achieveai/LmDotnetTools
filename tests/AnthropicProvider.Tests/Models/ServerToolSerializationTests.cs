using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Tests.Models;

public class ServerToolSerializationTests
{
    private static readonly JsonSerializerOptions _jsonOptions =
        AnthropicJsonSerializerOptionsFactory.CreateUniversal();

    [Fact]
    public void WebSearchTool_Serializes_CorrectType()
    {
        var tool = new AnthropicWebSearchTool();
        var json = JsonSerializer.Serialize<object>(tool, _jsonOptions);
        var doc = JsonDocument.Parse(json);

        Assert.Equal("web_search_20250305", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("web_search", doc.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public void WebSearchTool_Serializes_OptionalProperties()
    {
        var tool = new AnthropicWebSearchTool
        {
            MaxUses = 5,
            AllowedDomains = ["example.com", "docs.example.com"],
            UserLocation = new UserLocation
            {
                City = "San Francisco",
                Region = "California",
                Country = "US",
                Timezone = "America/Los_Angeles",
            },
        };

        var json = JsonSerializer.Serialize<object>(tool, _jsonOptions);
        var doc = JsonDocument.Parse(json);

        Assert.Equal(5, doc.RootElement.GetProperty("max_uses").GetInt32());

        var domains = doc.RootElement.GetProperty("allowed_domains");
        Assert.Equal(2, domains.GetArrayLength());
        Assert.Equal("example.com", domains[0].GetString());

        var location = doc.RootElement.GetProperty("user_location");
        Assert.Equal("approximate", location.GetProperty("type").GetString());
        Assert.Equal("San Francisco", location.GetProperty("city").GetString());
        Assert.Equal("California", location.GetProperty("region").GetString());
        Assert.Equal("US", location.GetProperty("country").GetString());
    }

    [Fact]
    public void WebSearchTool_OmitsNullProperties()
    {
        var tool = new AnthropicWebSearchTool();
        var json = JsonSerializer.Serialize<object>(tool, _jsonOptions);
        var doc = JsonDocument.Parse(json);

        Assert.False(doc.RootElement.TryGetProperty("max_uses", out _));
        Assert.False(doc.RootElement.TryGetProperty("allowed_domains", out _));
        Assert.False(doc.RootElement.TryGetProperty("blocked_domains", out _));
        Assert.False(doc.RootElement.TryGetProperty("user_location", out _));
    }

    [Fact]
    public void WebFetchTool_Serializes_CorrectType()
    {
        var tool = new AnthropicWebFetchTool();
        var json = JsonSerializer.Serialize<object>(tool, _jsonOptions);
        var doc = JsonDocument.Parse(json);

        Assert.Equal("web_fetch_20250910", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("web_fetch", doc.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public void WebFetchTool_Serializes_Citations_And_MaxContentTokens()
    {
        var tool = new AnthropicWebFetchTool
        {
            Citations = new CitationsConfig { Enabled = true },
            MaxContentTokens = 10000,
        };

        var json = JsonSerializer.Serialize<object>(tool, _jsonOptions);
        var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.GetProperty("citations").GetProperty("enabled").GetBoolean());
        Assert.Equal(10000, doc.RootElement.GetProperty("max_content_tokens").GetInt32());
    }

    [Fact]
    public void CodeExecutionTool_Serializes_CorrectType()
    {
        var tool = new AnthropicCodeExecutionTool();
        var json = JsonSerializer.Serialize<object>(tool, _jsonOptions);
        var doc = JsonDocument.Parse(json);

        Assert.Equal("code_execution_20250825", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("code_execution", doc.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public void MapFunctionsAndBuiltInTools_Combines_Both()
    {
        var builtInTools = new List<object> { new AnthropicWebSearchTool() };
        var functions = new[]
        {
            new FunctionContract
            {
                Name = "get_weather",
                Description = "Get weather",
                Parameters =
                [
                    new FunctionParameterContract
                    {
                        Name = "location",
                        ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(string)),
                    },
                ],
            },
        };

        var tools = AnthropicRequest.MapFunctionsAndBuiltInTools(functions, builtInTools);

        Assert.NotNull(tools);
        Assert.Equal(2, tools.Count);
        Assert.IsType<AnthropicWebSearchTool>(tools[0]);
        Assert.IsType<AnthropicTool>(tools[1]);
    }

    [Fact]
    public void MapFunctionsAndBuiltInTools_BuiltInOnly()
    {
        var builtInTools = new List<object>
        {
            new AnthropicWebSearchTool(),
            new AnthropicCodeExecutionTool(),
        };

        var tools = AnthropicRequest.MapFunctionsAndBuiltInTools(null, builtInTools);

        Assert.NotNull(tools);
        Assert.Equal(2, tools.Count);
    }

    [Fact]
    public void MapFunctionsAndBuiltInTools_NullBoth_ReturnsNull()
    {
        var tools = AnthropicRequest.MapFunctionsAndBuiltInTools(null, null);
        Assert.Null(tools);
    }

    [Fact]
    public void ServerToolUseContent_Deserializes_FromJson()
    {
        var json = """
            {
                "type": "server_tool_use",
                "id": "srvtoolu_01ABC",
                "name": "web_search",
                "input": {"query": "current weather"}
            }
            """;

        var content = JsonSerializer.Deserialize<AnthropicResponseContent>(json, _jsonOptions);

        Assert.IsType<AnthropicResponseServerToolUseContent>(content);
        var serverToolUse = (AnthropicResponseServerToolUseContent)content!;
        Assert.Equal("srvtoolu_01ABC", serverToolUse.Id);
        Assert.Equal("web_search", serverToolUse.Name);
        Assert.Equal("current weather", serverToolUse.Input.GetProperty("query").GetString());
    }

    [Fact]
    public void WebSearchToolResult_Deserializes_FromJson()
    {
        var json = """
            {
                "type": "web_search_tool_result",
                "tool_use_id": "srvtoolu_01ABC",
                "content": [
                    {
                        "type": "web_search_result",
                        "url": "https://example.com",
                        "title": "Example",
                        "encrypted_content": "enc123",
                        "page_age": "1 hour ago"
                    }
                ]
            }
            """;

        var content = JsonSerializer.Deserialize<AnthropicResponseContent>(json, _jsonOptions);

        Assert.IsType<AnthropicWebSearchToolResultContent>(content);
        var result = (AnthropicWebSearchToolResultContent)content!;
        Assert.Equal("srvtoolu_01ABC", result.ToolUseId);
        Assert.Equal(JsonValueKind.Array, result.Content.ValueKind);
        Assert.Equal(1, result.Content.GetArrayLength());
    }

    [Fact]
    public void WebFetchToolResult_Deserializes_FromJson()
    {
        var json = """
            {
                "type": "web_fetch_tool_result",
                "tool_use_id": "srvtoolu_02DEF",
                "content": {"type": "web_fetch_result", "url": "https://example.com", "content": "page content"}
            }
            """;

        var content = JsonSerializer.Deserialize<AnthropicResponseContent>(json, _jsonOptions);

        Assert.IsType<AnthropicWebFetchToolResultContent>(content);
        var result = (AnthropicWebFetchToolResultContent)content!;
        Assert.Equal("srvtoolu_02DEF", result.ToolUseId);
    }

    [Fact]
    public void BashCodeExecutionToolResult_Deserializes_FromJson()
    {
        var json = """
            {
                "type": "bash_code_execution_tool_result",
                "tool_use_id": "srvtoolu_03GHI",
                "content": {"stdout": "hello world", "stderr": "", "return_code": 0}
            }
            """;

        var content = JsonSerializer.Deserialize<AnthropicResponseContent>(json, _jsonOptions);

        Assert.IsType<AnthropicBashCodeExecutionToolResultContent>(content);
        var result = (AnthropicBashCodeExecutionToolResultContent)content!;
        Assert.Equal("srvtoolu_03GHI", result.ToolUseId);
    }

    [Fact]
    public void AnthropicResponse_WithServerTools_DeserializesAllContentBlocks()
    {
        var json = """
            {
                "id": "msg_01",
                "type": "message",
                "role": "assistant",
                "model": "claude-sonnet-4-20250514",
                "content": [
                    {"type": "text", "text": "Let me search."},
                    {"type": "server_tool_use", "id": "srvtoolu_01", "name": "web_search", "input": {"query": "test"}},
                    {"type": "web_search_tool_result", "tool_use_id": "srvtoolu_01", "content": []},
                    {"type": "text", "text": "Here are the results."}
                ],
                "stop_reason": "end_turn",
                "usage": {"input_tokens": 100, "output_tokens": 200}
            }
            """;

        var response = JsonSerializer.Deserialize<AnthropicResponse>(json, _jsonOptions);

        Assert.NotNull(response);
        Assert.Equal(4, response!.Content.Count);
        Assert.IsType<AnthropicResponseTextContent>(response.Content[0]);
        Assert.IsType<AnthropicResponseServerToolUseContent>(response.Content[1]);
        Assert.IsType<AnthropicWebSearchToolResultContent>(response.Content[2]);
        Assert.IsType<AnthropicResponseTextContent>(response.Content[3]);
    }

    [Fact]
    public void TextContent_WithCitations_Deserializes()
    {
        var json = """
            {
                "type": "text",
                "text": "The answer is 42.",
                "citations": [
                    {
                        "type": "web_search_result_location",
                        "url": "https://example.com",
                        "title": "Example",
                        "cited_text": "The answer is 42.",
                        "encrypted_index": "enc01",
                        "start_char_index": 0,
                        "end_char_index": 17
                    }
                ]
            }
            """;

        var content = JsonSerializer.Deserialize<AnthropicResponseContent>(json, _jsonOptions);

        Assert.IsType<AnthropicResponseTextContent>(content);
        var textContent = (AnthropicResponseTextContent)content!;
        Assert.Equal("The answer is 42.", textContent.Text);
        Assert.NotNull(textContent.Citations);
        Assert.Single(textContent.Citations);
        Assert.Equal("web_search_result_location", textContent.Citations[0].Type);
        Assert.Equal("https://example.com", textContent.Citations[0].Url);
        Assert.Equal("The answer is 42.", textContent.Citations[0].CitedText);
    }

    /// <summary>
    ///     Regression: serializing a ServerToolUseMessage with default(JsonElement) Input
    ///     via IMessageJsonConverter previously threw InvalidOperationException because
    ///     ShadowPropertiesJsonConverter attempted to write an Undefined JsonElement.
    /// </summary>
    [Fact]
    public void ServerToolUseMessage_WithDefaultInput_SerializesWithoutCrash()
    {
        var message = new ServerToolUseMessage
        {
            ToolUseId = "srvtoolu_01ABC",
            ToolName = "web_search",
        };

        // Input is default(JsonElement) with ValueKind == Undefined
        Assert.Equal(JsonValueKind.Undefined, message.Input.ValueKind);

        var productionOptions = JsonSerializerOptionsFactory.CreateForProduction();
        var json = JsonSerializer.Serialize<IMessage>(message, productionOptions);

        Assert.NotNull(json);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("srvtoolu_01ABC", doc.RootElement.GetProperty("tool_use_id").GetString());
        Assert.Equal("web_search", doc.RootElement.GetProperty("tool_name").GetString());
        Assert.False(
            doc.RootElement.TryGetProperty("input", out _),
            "Undefined JsonElement 'input' should be omitted from serialization");
    }

    /// <summary>
    ///     Regression: serializing a ServerToolResultMessage with default(JsonElement) Result
    ///     via IMessageJsonConverter previously threw InvalidOperationException because
    ///     ShadowPropertiesJsonConverter attempted to write an Undefined JsonElement.
    /// </summary>
    [Fact]
    public void ServerToolResultMessage_WithDefaultResult_SerializesWithoutCrash()
    {
        var message = new ServerToolResultMessage
        {
            ToolUseId = "srvtoolu_02DEF",
            ToolName = "web_search",
        };

        // Result is default(JsonElement) with ValueKind == Undefined
        Assert.Equal(JsonValueKind.Undefined, message.Result.ValueKind);

        var productionOptions = JsonSerializerOptionsFactory.CreateForProduction();
        var json = JsonSerializer.Serialize<IMessage>(message, productionOptions);

        Assert.NotNull(json);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("srvtoolu_02DEF", doc.RootElement.GetProperty("tool_use_id").GetString());
        Assert.Equal("web_search", doc.RootElement.GetProperty("tool_name").GetString());
        Assert.False(
            doc.RootElement.TryGetProperty("result", out _),
            "Undefined JsonElement 'result' should be omitted from serialization");
    }
}
