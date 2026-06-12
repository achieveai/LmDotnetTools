using System.Net;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmTestUtils.Tests.TestMode;

public class ToolSchemaPlaceholderTests
{
    private readonly InstructionChainParser _parser;

    public ToolSchemaPlaceholderTests()
    {
        var logger = NullLoggerFactory.Instance.CreateLogger<InstructionChainParser>();
        _parser = new InstructionChainParser(logger);
    }

    // ─── Parser tests ────────────────────────────────────────────────

    [Fact]
    public void Parser_ToolSchema_WithName_ShouldEncodeNameInPlaceholder()
    {
        var content = """
            <|instruction_start|>
            {"instruction_chain": [
                {"messages":[{"tool_schema":{"name":"get_weather"}}]}
            ]}
            <|instruction_end|>
            """;

        var result = _parser.ExtractInstructionChain(content);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Single(result[0].Messages);
        Assert.Equal("__TOOL_SCHEMA__:get_weather", result[0].Messages[0].ExplicitText);
    }

    [Fact]
    public void Parser_ToolSchema_WithoutName_ShouldUseGenericPlaceholder()
    {
        var content = """
            <|instruction_start|>
            {"instruction_chain": [
                {"messages":[{"tool_schema":{}}]}
            ]}
            <|instruction_end|>
            """;

        var result = _parser.ExtractInstructionChain(content);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Single(result[0].Messages);
        Assert.Equal("__TOOL_SCHEMA__", result[0].Messages[0].ExplicitText);
    }

    // ─── End-to-end handler tests (OpenAI) ───────────────────────────

    [Fact]
    public async Task OpenAI_ToolSchema_ShouldReturnSingleToolDescriptionAndSchema()
    {
        using var client = TestModeHttpClientFactory.CreateOpenAiTestClient(chunkDelayMs: 0);

        var content = await SendOpenAiAsync(client, "get_weather");

        // Markdown structure.
        Assert.Contains("# get_weather", content);
        Assert.Contains("## Description", content);
        Assert.Contains("## Schema", content);
        Assert.Contains("```json", content);

        // Targeted tool's description is present.
        Assert.Contains("Get current weather conditions for a specific location", content);

        // The JSON schema block is valid, indented JSON describing the tool's parameters.
        var schemaJson = ExtractJsonBlock(content);
        using var schema = JsonDocument.Parse(schemaJson);
        Assert.True(schema.RootElement.GetProperty("properties").TryGetProperty("location", out _));
        Assert.Contains("\n  ", schemaJson); // indentation proves it is pretty-printed

        // The other tool is NOT included (single-tool focus).
        Assert.DoesNotContain("calculate", content);
        Assert.DoesNotContain("Perform basic arithmetic", content);
    }

    [Fact]
    public async Task OpenAI_ToolSchema_UnknownTool_ShouldReturnNotFound()
    {
        using var client = TestModeHttpClientFactory.CreateOpenAiTestClient(chunkDelayMs: 0);

        var content = await SendOpenAiAsync(client, "does_not_exist");

        Assert.Contains("not found", content);
    }

    // ─── End-to-end handler tests (Anthropic) ────────────────────────

    [Fact]
    public async Task Anthropic_ToolSchema_ShouldReturnSingleToolDescriptionAndSchema()
    {
        using var client = TestModeHttpClientFactory.CreateAnthropicTestClient(chunkDelayMs: 0);

        var text = await SendAnthropicAsync(client, "get_weather");

        Assert.Contains("# get_weather", text);
        Assert.Contains("## Description", text);
        Assert.Contains("## Schema", text);
        Assert.Contains("```json", text);
        Assert.Contains("Get current weather conditions for a specific location", text);

        var schemaJson = ExtractJsonBlock(text);
        using var schema = JsonDocument.Parse(schemaJson);
        Assert.True(schema.RootElement.GetProperty("properties").TryGetProperty("location", out _));

        Assert.DoesNotContain("calculate", text);
        Assert.DoesNotContain("Perform basic arithmetic", text);
    }

    // ─── Helpers ─────────────────────────────────────────────────────

    private static string ExtractJsonBlock(string markdown)
    {
        const string fence = "```json\n";
        var start = markdown.IndexOf(fence, StringComparison.Ordinal);
        Assert.True(start >= 0, "Expected a ```json code block.");
        start += fence.Length;
        var end = markdown.IndexOf("\n```", start, StringComparison.Ordinal);
        Assert.True(end > start, "Expected a closing code fence.");
        return markdown[start..end];
    }

    private static string BuildUserContent(string toolName)
    {
        var instruction = new
        {
            instruction_chain = new[]
            {
                new { messages = new[] { new { tool_schema = new { name = toolName } } } },
            },
        };

        var instructionJson = JsonSerializer.Serialize(instruction);
        return $"test\n<|instruction_start|>{instructionJson}<|instruction_end|>";
    }

    private static async Task<string> SendOpenAiAsync(HttpClient client, string toolName)
    {
        var requestObj = new
        {
            model = "gpt-4o",
            stream = false,
            tools = new object[]
            {
                new
                {
                    type = "function",
                    function = new
                    {
                        name = "get_weather",
                        description = "Get current weather conditions for a specific location",
                        parameters = new
                        {
                            type = "object",
                            properties = new { location = new { type = "string" } },
                            required = new[] { "location" },
                        },
                    },
                },
                new
                {
                    type = "function",
                    function = new
                    {
                        name = "calculate",
                        description = "Perform basic arithmetic",
                        parameters = new
                        {
                            type = "object",
                            properties = new { expression = new { type = "string" } },
                            required = new[] { "expression" },
                        },
                    },
                },
            },
            messages = new[] { new { role = "user", content = BuildUserContent(toolName) } },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "http://test-mode/v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(requestObj), Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        var content = json.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        Assert.NotNull(content);
        return content;
    }

    private static async Task<string> SendAnthropicAsync(HttpClient client, string toolName)
    {
        var requestObj = new
        {
            model = "claude-sonnet-4-5-20250929",
            stream = false,
            max_tokens = 1024,
            tools = new object[]
            {
                new
                {
                    name = "get_weather",
                    description = "Get current weather conditions for a specific location",
                    input_schema = new
                    {
                        type = "object",
                        properties = new { location = new { type = "string" } },
                        required = new[] { "location" },
                    },
                },
                new
                {
                    name = "calculate",
                    description = "Perform basic arithmetic",
                    input_schema = new
                    {
                        type = "object",
                        properties = new { expression = new { type = "string" } },
                        required = new[] { "expression" },
                    },
                },
            },
            messages = new[] { new { role = "user", content = BuildUserContent(toolName) } },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "http://test-mode/v1/messages")
        {
            Content = new StringContent(JsonSerializer.Serialize(requestObj), Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        var contentArray = json.RootElement.GetProperty("content");
        Assert.True(contentArray.GetArrayLength() > 0);

        var text = contentArray[0].GetProperty("text").GetString();
        Assert.NotNull(text);
        return text;
    }
}
