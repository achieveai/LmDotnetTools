using AchieveAi.LmDotnetTools.AnthropicProvider.Utils;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Tests.Models;

public class ServerToolStreamParserTests
{
    private static readonly JsonSerializerOptions _jsonOptions =
        AnthropicJsonSerializerOptionsFactory.CreateUniversal();

    [Fact]
    public void ProcessEvent_ServerToolUse_ReturnsServerToolUseMessage()
    {
        var parser = new AnthropicStreamParser();

        // Send message_start first
        parser.ProcessEvent("event", BuildMessageStart());

        var data = JsonSerializer.Serialize(new
        {
            type = "content_block_start",
            index = 0,
            content_block = new
            {
                type = "server_tool_use",
                id = "srvtoolu_01ABC",
                name = "web_search",
                input = new { query = "current weather" },
            },
        });

        var messages = parser.ProcessEvent("event", data);

        Assert.Single(messages);
        var msg = Assert.IsType<ServerToolUseMessage>(messages[0]);
        Assert.Equal("srvtoolu_01ABC", msg.ToolUseId);
        Assert.Equal("web_search", msg.ToolName);
        Assert.Equal("current weather", msg.Input.GetProperty("query").GetString());
    }

    [Fact]
    public void ProcessEvent_WebSearchToolResult_ReturnsServerToolResultMessage()
    {
        var parser = new AnthropicStreamParser();
        parser.ProcessEvent("event", BuildMessageStart());

        var data = JsonSerializer.Serialize(new
        {
            type = "content_block_start",
            index = 1,
            content_block = new
            {
                type = "web_search_tool_result",
                tool_use_id = "srvtoolu_01ABC",
                content = new[]
                {
                    new
                    {
                        type = "web_search_result",
                        url = "https://example.com",
                        title = "Example Result",
                        encrypted_content = "enc123",
                    },
                },
            },
        });

        var messages = parser.ProcessEvent("event", data);

        Assert.Single(messages);
        var msg = Assert.IsType<ServerToolResultMessage>(messages[0]);
        Assert.Equal("srvtoolu_01ABC", msg.ToolUseId);
        Assert.Equal("web_search", msg.ToolName);
        Assert.False(msg.IsError);
        Assert.Null(msg.ErrorCode);
    }

    /// <summary>
    /// Regression: HandleContentBlockStart crashed with InvalidOperationException
    /// when web_search_tool_result had an array content (the normal success case with
    /// multiple search results). The parser called resultContent["type"] on a JsonArray
    /// instead of first checking that it was a JsonObject.
    /// </summary>
    [Fact]
    public void ProcessEvent_WebSearchToolResult_WithArrayContent_DoesNotCrash()
    {
        var parser = new AnthropicStreamParser();
        parser.ProcessEvent("event", BuildMessageStart());

        var data = JsonSerializer.Serialize(new
        {
            type = "content_block_start",
            index = 1,
            content_block = new
            {
                type = "web_search_tool_result",
                tool_use_id = "toolu_test",
                content = new[]
                {
                    new
                    {
                        type = "web_search_result",
                        url = "https://example.com",
                        title = "Test",
                        encrypted_content = "abc",
                    },
                },
            },
        });

        List<IMessage>? messages = null;
        var exception = Record.Exception(() => messages = parser.ProcessEvent("event", data));

        Assert.Null(exception);
        Assert.NotNull(messages);
        Assert.Single(messages);
        var msg = Assert.IsType<ServerToolResultMessage>(messages[0]);
        Assert.Equal("toolu_test", msg.ToolUseId);
        Assert.Equal("web_search", msg.ToolName);
        Assert.False(msg.IsError);
        Assert.Null(msg.ErrorCode);
    }

    [Fact]
    public void ProcessEvent_WebSearchToolResult_WithError_SetsIsErrorAndErrorCode()
    {
        var parser = new AnthropicStreamParser();
        parser.ProcessEvent("event", BuildMessageStart());

        var data = JsonSerializer.Serialize(new
        {
            type = "content_block_start",
            index = 1,
            content_block = new
            {
                type = "web_search_tool_result",
                tool_use_id = "srvtoolu_err_01",
                content = new
                {
                    type = "web_search_tool_result_error",
                    error_code = "max_uses_exceeded",
                },
            },
        });

        var messages = parser.ProcessEvent("event", data);

        Assert.Single(messages);
        var msg = Assert.IsType<ServerToolResultMessage>(messages[0]);
        Assert.True(msg.IsError);
        Assert.Equal("max_uses_exceeded", msg.ErrorCode);
    }

    [Fact]
    public void ProcessEvent_TextWithCitations_ReturnsTextWithCitationsMessage()
    {
        var parser = new AnthropicStreamParser();
        parser.ProcessEvent("event", BuildMessageStart());

        // Start text block with citations
        var startData = JsonSerializer.Serialize(new
        {
            type = "content_block_start",
            index = 0,
            content_block = new
            {
                type = "text",
                text = "",
                citations = new[]
                {
                    new
                    {
                        type = "web_search_result_location",
                        url = "https://example.com",
                        title = "Example",
                        cited_text = "The answer is 42.",
                        start_char_index = 0,
                        end_char_index = 17,
                    },
                },
            },
        });
        parser.ProcessEvent("event", startData);

        // Send text delta
        var deltaData = JsonSerializer.Serialize(new
        {
            type = "content_block_delta",
            index = 0,
            delta = new { type = "text_delta", text = "The answer is 42." },
        });
        parser.ProcessEvent("event", deltaData);

        // Send content_block_stop
        var stopData = JsonSerializer.Serialize(new
        {
            type = "content_block_stop",
            index = 0,
        });
        var stopMessages = parser.ProcessEvent("event", stopData);

        // The final block stop should produce a TextWithCitationsMessage
        var citationsMsg = stopMessages.OfType<TextWithCitationsMessage>().FirstOrDefault();
        Assert.NotNull(citationsMsg);
        Assert.Equal("The answer is 42.", citationsMsg!.Text);
        Assert.NotNull(citationsMsg.Citations);
        Assert.Single(citationsMsg.Citations);
        Assert.Equal("web_search_result_location", citationsMsg.Citations[0].Type);
    }

    [Fact]
    public void ProcessEvent_FullWebSearchFlow_ProducesCorrectMessageSequence()
    {
        var parser = new AnthropicStreamParser();
        var allMessages = new List<IMessage>();

        // 1. message_start
        allMessages.AddRange(parser.ProcessEvent("event", BuildMessageStart()));

        // 2. server_tool_use content_block_start
        allMessages.AddRange(parser.ProcessEvent("event", JsonSerializer.Serialize(new
        {
            type = "content_block_start",
            index = 0,
            content_block = new
            {
                type = "server_tool_use",
                id = "srvtoolu_01",
                name = "web_search",
                input = new { query = "test query" },
            },
        })));

        // 3. content_block_stop for server_tool_use
        allMessages.AddRange(parser.ProcessEvent("event", JsonSerializer.Serialize(new
        {
            type = "content_block_stop",
            index = 0,
        })));

        // 4. web_search_tool_result content_block_start
        allMessages.AddRange(parser.ProcessEvent("event", JsonSerializer.Serialize(new
        {
            type = "content_block_start",
            index = 1,
            content_block = new
            {
                type = "web_search_tool_result",
                tool_use_id = "srvtoolu_01",
                content = new[]
                {
                    new
                    {
                        type = "web_search_result",
                        url = "https://example.com",
                        title = "Example",
                        encrypted_content = "enc...",
                    },
                },
            },
        })));

        // 5. content_block_stop for result
        allMessages.AddRange(parser.ProcessEvent("event", JsonSerializer.Serialize(new
        {
            type = "content_block_stop",
            index = 1,
        })));

        // 6. text content_block_start
        allMessages.AddRange(parser.ProcessEvent("event", JsonSerializer.Serialize(new
        {
            type = "content_block_start",
            index = 2,
            content_block = new { type = "text", text = "" },
        })));

        // 7. text_delta
        allMessages.AddRange(parser.ProcessEvent("event", JsonSerializer.Serialize(new
        {
            type = "content_block_delta",
            index = 2,
            delta = new { type = "text_delta", text = "Here are the results." },
        })));

        // 8. content_block_stop for text
        allMessages.AddRange(parser.ProcessEvent("event", JsonSerializer.Serialize(new
        {
            type = "content_block_stop",
            index = 2,
        })));

        // Verify message types in sequence
        var serverToolUse = allMessages.OfType<ServerToolUseMessage>().ToList();
        var serverToolResult = allMessages.OfType<ServerToolResultMessage>().ToList();
        var textMessages = allMessages.OfType<TextMessage>().ToList();

        Assert.Single(serverToolUse);
        Assert.Equal("web_search", serverToolUse[0].ToolName);
        Assert.Equal("srvtoolu_01", serverToolUse[0].ToolUseId);

        Assert.Single(serverToolResult);
        Assert.Equal("web_search", serverToolResult[0].ToolName);

        Assert.Single(textMessages);
        Assert.Equal("Here are the results.", textMessages[0].Text);

        // Also verify GetAllMessages contains all server tool messages
        var accumulated = parser.GetAllMessages();
        Assert.Contains(accumulated, m => m is ServerToolUseMessage);
        Assert.Contains(accumulated, m => m is ServerToolResultMessage);
    }

    [Fact]
    public void ProcessStreamEvent_TypedPath_ServerToolUse_Works()
    {
        var parser = new AnthropicStreamParser();
        parser.ProcessStreamEvent(new AnthropicMessageStartEvent
        {
            Message = new AnthropicResponse
            {
                Id = "msg_typed_01",
                Role = "assistant",
                Model = "claude-sonnet-4-20250514",
            },
        });

        var serverToolUseContent = new AnthropicResponseServerToolUseContent
        {
            Id = "srvtoolu_typed_01",
            Name = "web_search",
            Input = JsonSerializer.Deserialize<JsonElement>("""{"query": "typed test"}"""),
        };

        var messages = parser.ProcessStreamEvent(new AnthropicContentBlockStartEvent
        {
            Index = 0,
            ContentBlock = serverToolUseContent,
        });

        Assert.Single(messages);
        var msg = Assert.IsType<ServerToolUseMessage>(messages[0]);
        Assert.Equal("srvtoolu_typed_01", msg.ToolUseId);
        Assert.Equal("web_search", msg.ToolName);
    }

    [Fact]
    public void ProcessStreamEvent_TypedPath_WebSearchResult_Works()
    {
        var parser = new AnthropicStreamParser();
        parser.ProcessStreamEvent(new AnthropicMessageStartEvent
        {
            Message = new AnthropicResponse
            {
                Id = "msg_typed_02",
                Role = "assistant",
                Model = "claude-sonnet-4-20250514",
            },
        });

        var resultContent = new AnthropicWebSearchToolResultContent
        {
            ToolUseId = "srvtoolu_typed_01",
            Content = JsonSerializer.Deserialize<JsonElement>("""[{"type":"web_search_result","url":"https://example.com","title":"Test"}]"""),
        };

        var messages = parser.ProcessStreamEvent(new AnthropicContentBlockStartEvent
        {
            Index = 1,
            ContentBlock = resultContent,
        });

        Assert.Single(messages);
        var msg = Assert.IsType<ServerToolResultMessage>(messages[0]);
        Assert.Equal("srvtoolu_typed_01", msg.ToolUseId);
        Assert.Equal("web_search", msg.ToolName);
        Assert.False(msg.IsError);
    }

    [Fact]
    public void ProcessStreamEvent_TypedPath_WebFetchResult_Works()
    {
        var parser = new AnthropicStreamParser();
        parser.ProcessStreamEvent(new AnthropicMessageStartEvent
        {
            Message = new AnthropicResponse
            {
                Id = "msg_typed_03",
                Role = "assistant",
                Model = "claude-sonnet-4-20250514",
            },
        });

        var resultContent = new AnthropicWebFetchToolResultContent
        {
            ToolUseId = "srvtoolu_wf_01",
            Content = JsonSerializer.Deserialize<JsonElement>("""{"url":"https://example.com","content":"fetched content"}"""),
        };

        var messages = parser.ProcessStreamEvent(new AnthropicContentBlockStartEvent
        {
            Index = 0,
            ContentBlock = resultContent,
        });

        Assert.Single(messages);
        var msg = Assert.IsType<ServerToolResultMessage>(messages[0]);
        Assert.Equal("web_fetch", msg.ToolName);
    }

    [Fact]
    public void ProcessStreamEvent_TypedPath_BashCodeExecution_Works()
    {
        var parser = new AnthropicStreamParser();
        parser.ProcessStreamEvent(new AnthropicMessageStartEvent
        {
            Message = new AnthropicResponse
            {
                Id = "msg_typed_04",
                Role = "assistant",
                Model = "claude-sonnet-4-20250514",
            },
        });

        var resultContent = new AnthropicBashCodeExecutionToolResultContent
        {
            ToolUseId = "srvtoolu_bash_01",
            Content = JsonSerializer.Deserialize<JsonElement>("""{"stdout":"hello","stderr":"","return_code":0}"""),
        };

        var messages = parser.ProcessStreamEvent(new AnthropicContentBlockStartEvent
        {
            Index = 0,
            ContentBlock = resultContent,
        });

        Assert.Single(messages);
        var msg = Assert.IsType<ServerToolResultMessage>(messages[0]);
        Assert.Equal("bash_code_execution", msg.ToolName);
    }

    [Fact]
    public void ProcessStreamEvent_TypedPath_TextEditorCodeExecution_Works()
    {
        var parser = new AnthropicStreamParser();
        parser.ProcessStreamEvent(new AnthropicMessageStartEvent
        {
            Message = new AnthropicResponse
            {
                Id = "msg_typed_05",
                Role = "assistant",
                Model = "claude-sonnet-4-20250514",
            },
        });

        var resultContent = new AnthropicTextEditorCodeExecutionToolResultContent
        {
            ToolUseId = "srvtoolu_te_01",
            Content = JsonSerializer.Deserialize<JsonElement>("""{"result":"edit applied"}"""),
        };

        var messages = parser.ProcessStreamEvent(new AnthropicContentBlockStartEvent
        {
            Index = 0,
            ContentBlock = resultContent,
        });

        Assert.Single(messages);
        var msg = Assert.IsType<ServerToolResultMessage>(messages[0]);
        Assert.Equal("text_editor_code_execution", msg.ToolName);
    }

    [Fact]
    public void ProcessEvent_CodeExecutionResult_ReturnsServerToolResultMessage()
    {
        var parser = new AnthropicStreamParser();
        parser.ProcessEvent("event", BuildMessageStart());

        var data = JsonSerializer.Serialize(new
        {
            type = "content_block_start",
            index = 0,
            content_block = new
            {
                type = "bash_code_execution_tool_result",
                tool_use_id = "srvtoolu_bash_02",
                content = new { stdout = "output", stderr = "", return_code = 0 },
            },
        });

        var messages = parser.ProcessEvent("event", data);

        Assert.Single(messages);
        var msg = Assert.IsType<ServerToolResultMessage>(messages[0]);
        Assert.Equal("srvtoolu_bash_02", msg.ToolUseId);
        Assert.Equal("bash_code_execution", msg.ToolName);
    }

    private static string BuildMessageStart()
    {
        return JsonSerializer.Serialize(new
        {
            type = "message_start",
            message = new
            {
                id = "msg_stream_01",
                type = "message",
                role = "assistant",
                model = "claude-sonnet-4-20250514",
                content = Array.Empty<object>(),
                usage = new { input_tokens = 100, output_tokens = 0 },
            },
        });
    }
}
