using System.Net;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmTestUtils.Tests.TestMode;

public class RequestMetadataPlaceholderTests
{
    private readonly InstructionChainParser _parser;

    public RequestMetadataPlaceholderTests()
    {
        var logger = NullLoggerFactory.Instance.CreateLogger<InstructionChainParser>();
        _parser = new InstructionChainParser(logger);
    }

    // ─── Parser tests ────────────────────────────────────────────────

    [Fact]
    public void Parser_RequestUrlEcho_ShouldParseAsPlaceholder()
    {
        var content = """
            <|instruction_start|>
            {"instruction_chain": [
                {"messages":[{"request_url_echo":{}}]}
            ]}
            <|instruction_end|>
            """;

        var result = _parser.ExtractInstructionChain(content);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Single(result[0].Messages);
        Assert.Equal("__REQUEST_URL__", result[0].Messages[0].ExplicitText);
    }

    [Fact]
    public void Parser_RequestHeadersEcho_ShouldParseAsPlaceholder()
    {
        var content = """
            <|instruction_start|>
            {"instruction_chain": [
                {"messages":[{"request_headers_echo":{}}]}
            ]}
            <|instruction_end|>
            """;

        var result = _parser.ExtractInstructionChain(content);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Single(result[0].Messages);
        Assert.Equal("__REQUEST_HEADERS__", result[0].Messages[0].ExplicitText);
    }

    [Fact]
    public void Parser_RequestParamsEcho_WithFields_ShouldEncodeFieldsInPlaceholder()
    {
        var content = """
            <|instruction_start|>
            {"instruction_chain": [
                {"messages":[{"request_params_echo":{"fields":["model","max_tokens"]}}]}
            ]}
            <|instruction_end|>
            """;

        var result = _parser.ExtractInstructionChain(content);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Single(result[0].Messages);
        Assert.Equal("__REQUEST_PARAMS__:model,max_tokens", result[0].Messages[0].ExplicitText);
    }

    [Fact]
    public void Parser_RequestParamsEcho_WithoutFields_ShouldUseGenericPlaceholder()
    {
        var content = """
            <|instruction_start|>
            {"instruction_chain": [
                {"messages":[{"request_params_echo":{}}]}
            ]}
            <|instruction_end|>
            """;

        var result = _parser.ExtractInstructionChain(content);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Single(result[0].Messages);
        Assert.Equal("__REQUEST_PARAMS__", result[0].Messages[0].ExplicitText);
    }

    [Fact]
    public void Parser_MultipleMetadataMessages_ShouldParseAll()
    {
        var content = """
            <|instruction_start|>
            {"instruction_chain": [
                {"messages":[
                    {"request_url_echo":{}},
                    {"request_headers_echo":{}},
                    {"request_params_echo":{"fields":["model"]}}
                ]}
            ]}
            <|instruction_end|>
            """;

        var result = _parser.ExtractInstructionChain(content);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(3, result[0].Messages.Count);
        Assert.Equal("__REQUEST_URL__", result[0].Messages[0].ExplicitText);
        Assert.Equal("__REQUEST_HEADERS__", result[0].Messages[1].ExplicitText);
        Assert.Equal("__REQUEST_PARAMS__:model", result[0].Messages[2].ExplicitText);
    }

    // ─── End-to-end handler tests (OpenAI) ───────────────────────────

    [Fact]
    public async Task OpenAI_RequestUrlEcho_ShouldResolveToActualUrl()
    {
        using var client = TestModeHttpClientFactory.CreateOpenAiTestClient(chunkDelayMs: 0);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "http://test-mode/v1/chat/completions"
        )
        {
            Content = new StringContent(
                """
                {
                  "model": "test-model",
                  "stream": false,
                  "messages": [
                    {"role":"user","content":"test\n<|instruction_start|>{\"instruction_chain\":[{\"messages\":[{\"request_url_echo\":{}}]}]}<|instruction_end|>"}
                  ]
                }
                """,
                Encoding.UTF8,
                "application/json"
            ),
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
        Assert.Contains("/v1/chat/completions", content);
    }

    [Fact]
    public async Task OpenAI_RequestParamsEcho_ShouldReturnFilteredFields()
    {
        using var client = TestModeHttpClientFactory.CreateOpenAiTestClient(chunkDelayMs: 0);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "http://test-mode/v1/chat/completions"
        )
        {
            Content = new StringContent(
                """
                {
                  "model": "gpt-4o",
                  "stream": false,
                  "max_tokens": 1024,
                  "messages": [
                    {"role":"user","content":"test\n<|instruction_start|>{\"instruction_chain\":[{\"messages\":[{\"request_params_echo\":{\"fields\":[\"model\",\"max_tokens\"]}}]}]}<|instruction_end|>"}
                  ]
                }
                """,
                Encoding.UTF8,
                "application/json"
            ),
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
        Assert.Contains("gpt-4o", content);
        Assert.Contains("1024", content);
    }

    // ─── End-to-end handler tests (Anthropic) ────────────────────────

    [Fact]
    public async Task Anthropic_RequestUrlEcho_ShouldResolveToActualUrl()
    {
        using var client = TestModeHttpClientFactory.CreateAnthropicTestClient(chunkDelayMs: 0);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "http://test-mode/v1/messages"
        )
        {
            Content = new StringContent(
                """
                {
                  "model": "claude-sonnet-4-5-20250929",
                  "stream": false,
                  "max_tokens": 1024,
                  "messages": [
                    {"role":"user","content":"test\n<|instruction_start|>{\"instruction_chain\":[{\"messages\":[{\"request_url_echo\":{}}]}]}<|instruction_end|>"}
                  ]
                }
                """,
                Encoding.UTF8,
                "application/json"
            ),
        };

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        var contentArray = json.RootElement.GetProperty("content");
        Assert.True(contentArray.GetArrayLength() > 0);

        var text = contentArray[0].GetProperty("text").GetString();
        Assert.NotNull(text);
        Assert.Contains("/v1/messages", text);
    }

    [Fact]
    public async Task Anthropic_RequestParamsEcho_ShouldReturnFilteredFields()
    {
        using var client = TestModeHttpClientFactory.CreateAnthropicTestClient(chunkDelayMs: 0);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "http://test-mode/v1/messages"
        )
        {
            Content = new StringContent(
                """
                {
                  "model": "claude-sonnet-4-5-20250929",
                  "stream": false,
                  "max_tokens": 2048,
                  "messages": [
                    {"role":"user","content":"test\n<|instruction_start|>{\"instruction_chain\":[{\"messages\":[{\"request_params_echo\":{\"fields\":[\"model\",\"max_tokens\"]}}]}]}<|instruction_end|>"}
                  ]
                }
                """,
                Encoding.UTF8,
                "application/json"
            ),
        };

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        var contentArray = json.RootElement.GetProperty("content");
        Assert.True(contentArray.GetArrayLength() > 0);

        var text = contentArray[0].GetProperty("text").GetString();
        Assert.NotNull(text);
        Assert.Contains("claude-sonnet-4-5-20250929", text);
        Assert.Contains("2048", text);
    }

    [Fact]
    public async Task OpenAI_RequestHeadersEcho_ShouldIncludeContentType()
    {
        using var client = TestModeHttpClientFactory.CreateOpenAiTestClient(chunkDelayMs: 0);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "http://test-mode/v1/chat/completions"
        )
        {
            Content = new StringContent(
                """
                {
                  "model": "test-model",
                  "stream": false,
                  "messages": [
                    {"role":"user","content":"test\n<|instruction_start|>{\"instruction_chain\":[{\"messages\":[{\"request_headers_echo\":{}}]}]}<|instruction_end|>"}
                  ]
                }
                """,
                Encoding.UTF8,
                "application/json"
            ),
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
        Assert.Contains("Content-Type", content);
    }
}
