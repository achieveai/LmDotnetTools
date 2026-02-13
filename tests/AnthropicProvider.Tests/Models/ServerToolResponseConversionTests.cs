using System.Reflection;
using AchieveAi.LmDotnetTools.AnthropicProvider.Utils;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Tests.Models;

public class ServerToolResponseConversionTests
{
    private static readonly JsonSerializerOptions _jsonOptions =
        AnthropicJsonSerializerOptionsFactory.CreateUniversal();

    [Fact]
    public void ToMessages_WithServerToolUse_ReturnsServerToolUseMessage()
    {
        var response = new AnthropicResponse
        {
            Id = "msg_01",
            Role = "assistant",
            Content =
            [
                new AnthropicResponseServerToolUseContent
                {
                    Id = "srvtoolu_01",
                    Name = "web_search",
                    Input = JsonSerializer.Deserialize<JsonElement>("""{"query": "test"}"""),
                },
            ],
            Usage = new AnthropicUsage { InputTokens = 100, OutputTokens = 50 },
        };

        var messages = response.ToMessages("test-agent");

        var serverToolUseMsg = messages.OfType<ToolCallMessage>().SingleOrDefault();
        Assert.NotNull(serverToolUseMsg);
        Assert.Equal("srvtoolu_01", serverToolUseMsg!.ToolCallId);
        Assert.Equal("web_search", serverToolUseMsg.FunctionName);
        Assert.Equal(ExecutionTarget.ProviderServer, serverToolUseMsg.ExecutionTarget);
        var args = JsonDocument.Parse(serverToolUseMsg.FunctionArgs ?? "{}").RootElement;
        Assert.Equal("test", args.GetProperty("query").GetString());
        Assert.Equal("test-agent", serverToolUseMsg.FromAgent);
    }

    [Fact]
    public void ToMessages_WithWebSearchToolResult_ReturnsServerToolResultMessage()
    {
        var response = new AnthropicResponse
        {
            Id = "msg_02",
            Role = "assistant",
            Content =
            [
                new AnthropicWebSearchToolResultContent
                {
                    ToolUseId = "srvtoolu_01",
                    Content = JsonSerializer.Deserialize<JsonElement>(
                        """[{"type":"web_search_result","url":"https://example.com","title":"Test"}]"""
                    ),
                },
            ],
            Usage = new AnthropicUsage { InputTokens = 100, OutputTokens = 50 },
        };

        var messages = response.ToMessages("test-agent");

        var resultMsg = messages.OfType<ToolCallResultMessage>().SingleOrDefault();
        Assert.NotNull(resultMsg);
        Assert.Equal("srvtoolu_01", resultMsg!.ToolCallId);
        Assert.Equal("web_search", resultMsg.ToolName);
        Assert.Equal(ExecutionTarget.ProviderServer, resultMsg.ExecutionTarget);
        Assert.False(resultMsg.IsError);
    }

    [Fact]
    public void ToMessages_WithWebFetchToolResult_ReturnsServerToolResultMessage()
    {
        var response = new AnthropicResponse
        {
            Id = "msg_03",
            Role = "assistant",
            Content =
            [
                new AnthropicWebFetchToolResultContent
                {
                    ToolUseId = "srvtoolu_wf_01",
                    Content = JsonSerializer.Deserialize<JsonElement>(
                        """{"url":"https://example.com","content":"fetched"}"""
                    ),
                },
            ],
            Usage = new AnthropicUsage { InputTokens = 100, OutputTokens = 50 },
        };

        var messages = response.ToMessages("test-agent");

        var resultMsg = messages.OfType<ToolCallResultMessage>().SingleOrDefault();
        Assert.NotNull(resultMsg);
        Assert.Equal("web_fetch", resultMsg!.ToolName);
        Assert.Equal(ExecutionTarget.ProviderServer, resultMsg.ExecutionTarget);
    }

    [Fact]
    public void ToMessages_WithCodeExecutionResult_ReturnsServerToolResultMessage()
    {
        var response = new AnthropicResponse
        {
            Id = "msg_04",
            Role = "assistant",
            Content =
            [
                new AnthropicBashCodeExecutionToolResultContent
                {
                    ToolUseId = "srvtoolu_bash_01",
                    Content = JsonSerializer.Deserialize<JsonElement>(
                        """{"stdout":"hello","return_code":0}"""
                    ),
                },
            ],
            Usage = new AnthropicUsage { InputTokens = 100, OutputTokens = 50 },
        };

        var messages = response.ToMessages("test-agent");

        var resultMsg = messages.OfType<ToolCallResultMessage>().SingleOrDefault();
        Assert.NotNull(resultMsg);
        Assert.Equal("bash_code_execution", resultMsg!.ToolName);
        Assert.Equal(ExecutionTarget.ProviderServer, resultMsg.ExecutionTarget);
    }

    [Fact]
    public void ToMessages_WithErrorResult_SetsIsErrorAndErrorCode()
    {
        var response = new AnthropicResponse
        {
            Id = "msg_err_01",
            Role = "assistant",
            Content =
            [
                new AnthropicWebSearchToolResultContent
                {
                    ToolUseId = "srvtoolu_err_01",
                    Content = JsonSerializer.Deserialize<JsonElement>(
                        """{"type":"web_search_tool_result_error","error_code":"max_uses_exceeded"}"""
                    ),
                },
            ],
            Usage = new AnthropicUsage { InputTokens = 100, OutputTokens = 50 },
        };

        var messages = response.ToMessages("test-agent");

        var resultMsg = messages.OfType<ToolCallResultMessage>().SingleOrDefault();
        Assert.NotNull(resultMsg);
        Assert.True(resultMsg!.IsError);
        Assert.Equal("max_uses_exceeded", resultMsg.ErrorCode);
        Assert.Equal(ExecutionTarget.ProviderServer, resultMsg.ExecutionTarget);
    }

    [Fact]
    public void ToMessages_WithMixedContent_ReturnsAllMessageTypes()
    {
        var response = new AnthropicResponse
        {
            Id = "msg_mixed_01",
            Role = "assistant",
            Content =
            [
                new AnthropicResponseTextContent { Text = "Let me search." },
                new AnthropicResponseServerToolUseContent
                {
                    Id = "srvtoolu_01",
                    Name = "web_search",
                    Input = JsonSerializer.Deserialize<JsonElement>("""{"query":"test"}"""),
                },
                new AnthropicWebSearchToolResultContent
                {
                    ToolUseId = "srvtoolu_01",
                    Content = JsonSerializer.Deserialize<JsonElement>(
                        """[{"type":"web_search_result","url":"https://example.com","title":"Test"}]"""
                    ),
                },
                new AnthropicResponseTextContent { Text = "Here are the results." },
            ],
            Usage = new AnthropicUsage { InputTokens = 100, OutputTokens = 200 },
        };

        var messages = response.ToMessages("test-agent");

        // Should have: 2 text + 1 server_tool_use + 1 server_tool_result + 1 usage = 5
        Assert.Equal(5, messages.Count);

        var textMessages = messages.OfType<TextMessage>().ToList();
        Assert.Equal(2, textMessages.Count);
        Assert.Equal("Let me search.", textMessages[0].Text);
        Assert.Equal("Here are the results.", textMessages[1].Text);

        Assert.Single(messages.OfType<ToolCallMessage>());
        Assert.Single(messages.OfType<ToolCallResultMessage>());
    }

    [Fact]
    public void ToMessages_TextWithCitations_ReturnsTextWithCitationsMessage()
    {
        var response = new AnthropicResponse
        {
            Id = "msg_cit_01",
            Role = "assistant",
            Content =
            [
                new AnthropicResponseTextContent
                {
                    Text = "The answer is 42.",
                    Citations =
                    [
                        new Citation
                        {
                            Type = "web_search_result_location",
                            Url = "https://example.com",
                            Title = "Example",
                            CitedText = "The answer is 42.",
                            StartCharIndex = 0,
                            EndCharIndex = 17,
                        },
                    ],
                },
            ],
            Usage = new AnthropicUsage { InputTokens = 100, OutputTokens = 50 },
        };

        var messages = response.ToMessages("test-agent");

        var citMsg = messages.OfType<TextWithCitationsMessage>().SingleOrDefault();
        Assert.NotNull(citMsg);
        Assert.Equal("The answer is 42.", citMsg!.Text);
        Assert.NotNull(citMsg.Citations);
        Assert.Single(citMsg.Citations);
        Assert.Equal("web_search_result_location", citMsg.Citations[0].Type);
        Assert.Equal("https://example.com", citMsg.Citations[0].Url);
    }

    [Fact]
    public void ToMessages_FullWebSearchResponse_FromFixtureJson()
    {
        var json = GetTestFileContent("server_tool_web_search_response.json");
        var response = JsonSerializer.Deserialize<AnthropicResponse>(json, _jsonOptions);

        Assert.NotNull(response);
        var messages = response!.ToMessages("test-agent");

        // The fixture has: text + server_tool_use + web_search_tool_result + text_with_citations + usage
        Assert.True(messages.Count >= 4, $"Expected at least 4 messages, got {messages.Count}");

        // Verify server tool use
        var toolUse = messages.OfType<ToolCallMessage>().SingleOrDefault();
        Assert.NotNull(toolUse);
        Assert.Equal("web_search", toolUse!.FunctionName);
        Assert.Equal("srvtoolu_01ABC", toolUse.ToolCallId);
        Assert.Equal(ExecutionTarget.ProviderServer, toolUse.ExecutionTarget);

        // Verify server tool result
        var toolResult = messages.OfType<ToolCallResultMessage>().SingleOrDefault();
        Assert.NotNull(toolResult);
        Assert.Equal("web_search", toolResult!.ToolName);
        Assert.False(toolResult.IsError);
        Assert.Equal(ExecutionTarget.ProviderServer, toolResult.ExecutionTarget);

        // Verify text with citations
        var citMsg = messages.OfType<TextWithCitationsMessage>().SingleOrDefault();
        Assert.NotNull(citMsg);
        Assert.NotNull(citMsg!.Citations);
        Assert.Single(citMsg.Citations);
        Assert.Equal("web_search_result_location", citMsg.Citations[0].Type);
    }

    [Fact]
    public void ToMessages_ErrorResponse_FromFixtureJson()
    {
        var json = GetTestFileContent("server_tool_web_search_error_response.json");
        var response = JsonSerializer.Deserialize<AnthropicResponse>(json, _jsonOptions);

        Assert.NotNull(response);
        var messages = response!.ToMessages("test-agent");

        var toolResult = messages.OfType<ToolCallResultMessage>().SingleOrDefault();
        Assert.NotNull(toolResult);
        Assert.True(toolResult!.IsError);
        Assert.Equal("max_uses_exceeded", toolResult.ErrorCode);
        Assert.Equal(ExecutionTarget.ProviderServer, toolResult.ExecutionTarget);
    }

    private static string GetTestFileContent(string filename)
    {
        var assemblyLocation = Assembly.GetExecutingAssembly().Location;
        var currentDir = Path.GetDirectoryName(assemblyLocation);

        while (
            currentDir != null
            && !Directory.Exists(Path.Combine(currentDir, ".git"))
            && !File.Exists(Path.Combine(currentDir, "LmDotnetTools.sln"))
        )
        {
            currentDir = Directory.GetParent(currentDir)?.FullName;
        }

        var rootDir = currentDir ?? throw new InvalidOperationException("Could not find repository root");
        return File.ReadAllText(
            Path.Combine(rootDir, "tests", "AnthropicProvider.Tests", "TestFiles", filename)
        );
    }
}
