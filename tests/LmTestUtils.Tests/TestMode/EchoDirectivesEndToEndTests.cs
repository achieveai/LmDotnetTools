using System.Net;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;

namespace AchieveAi.LmDotnetTools.LmTestUtils.Tests.TestMode;

/// <summary>
///     End-to-end coverage for the introspection echo directives that resolve through the
///     mock SSE handlers: <c>system_prompt_echo</c>, <c>tools_list</c>, and <c>tools_echo</c>.
///     (<c>tool_schema</c> and the <c>request_*_echo</c> directives are covered by
///     <see cref="ToolSchemaPlaceholderTests"/> and <see cref="RequestMetadataPlaceholderTests"/>.)
/// </summary>
public class EchoDirectivesEndToEndTests
{
    private const string SystemPrompt = "You are a meticulous test assistant for directive verification.";

    private static string BuildUserContent(string directive)
    {
        // directive is a raw JSON object property, e.g. "tools_list":{}
        return $"test\\n<|instruction_start|>{{\"instruction_chain\":[{{\"messages\":[{{{directive}}}]}}]}}<|instruction_end|>";
    }

    // ─── OpenAI handler ──────────────────────────────────────────────

    private static async Task<string> SendOpenAiAsync(HttpClient client, string directive)
    {
        var requestObj = new
        {
            model = "gpt-4o",
            stream = false,
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = BuildUserContent(directive) },
            },
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
        return content!;
    }

    [Fact]
    public async Task OpenAI_SystemPromptEcho_ShouldReturnSystemPrompt()
    {
        using var client = TestModeHttpClientFactory.CreateOpenAiTestClient(chunkDelayMs: 0);
        var content = await SendOpenAiAsync(client, "\"system_prompt_echo\":{}");
        Assert.Contains(SystemPrompt, content);
    }

    [Fact]
    public async Task OpenAI_ToolsList_ShouldReturnToolNames()
    {
        using var client = TestModeHttpClientFactory.CreateOpenAiTestClient(chunkDelayMs: 0);
        var content = await SendOpenAiAsync(client, "\"tools_list\":{}");
        Assert.Contains("get_weather", content);
        Assert.Contains("calculate", content);
    }

    [Fact]
    public async Task OpenAI_ToolsEcho_ShouldReturnNamesAndDescriptions()
    {
        using var client = TestModeHttpClientFactory.CreateOpenAiTestClient(chunkDelayMs: 0);
        var content = await SendOpenAiAsync(client, "\"tools_echo\":{}");
        Assert.Contains("get_weather", content);
        Assert.Contains("Get current weather conditions for a specific location", content);
        Assert.Contains("Perform basic arithmetic", content);
    }

    // ─── Anthropic handler ───────────────────────────────────────────

    private static async Task<string> SendAnthropicAsync(HttpClient client, string directive)
    {
        var requestObj = new
        {
            model = "claude-sonnet-4-5-20250929",
            stream = false,
            max_tokens = 1024,
            system = SystemPrompt,
            messages = new object[]
            {
                new { role = "user", content = BuildUserContent(directive) },
            },
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
        return text!;
    }

    [Fact]
    public async Task Anthropic_SystemPromptEcho_ShouldReturnSystemPrompt()
    {
        using var client = TestModeHttpClientFactory.CreateAnthropicTestClient(chunkDelayMs: 0);
        var text = await SendAnthropicAsync(client, "\"system_prompt_echo\":{}");
        Assert.Contains(SystemPrompt, text);
    }

    [Fact]
    public async Task Anthropic_ToolsList_ShouldReturnToolNames()
    {
        using var client = TestModeHttpClientFactory.CreateAnthropicTestClient(chunkDelayMs: 0);
        var text = await SendAnthropicAsync(client, "\"tools_list\":{}");
        Assert.Contains("get_weather", text);
        Assert.Contains("calculate", text);
    }

    [Fact]
    public async Task Anthropic_ToolsEcho_ShouldReturnNamesAndDescriptions()
    {
        using var client = TestModeHttpClientFactory.CreateAnthropicTestClient(chunkDelayMs: 0);
        var text = await SendAnthropicAsync(client, "\"tools_echo\":{}");
        Assert.Contains("get_weather", text);
        Assert.Contains("Get current weather conditions for a specific location", text);
        Assert.Contains("Perform basic arithmetic", text);
    }
}
