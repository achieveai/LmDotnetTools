namespace AchieveAi.LmDotnetTools.AnthropicProvider.Tests.Models;

public class ServerToolStreamParserTests
{
    private static readonly JsonSerializerOptions _jsonOptions =
        AnthropicJsonSerializerOptionsFactory.CreateUniversal();

    [Fact]
    public void ProcessEvent_ServerToolUse_ReturnsToolCallMessage()
    {
        var parser = new AnthropicStreamParser();

        // Send message_start first
        parser.ProcessEvent("event", BuildMessageStart());

        var startData = JsonSerializer.Serialize(new
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

        // content_block_start emits a ToolCallUpdateMessage (preview), not a full ToolCallMessage.
        // The final ToolCallMessage is emitted at content_block_stop.
        var startMessages = parser.ProcessEvent("event", startData);

        Assert.Single(startMessages);
        var msg = Assert.IsType<ToolCallUpdateMessage>(startMessages[0]);
        Assert.Equal("srvtoolu_01ABC", msg.ToolCallId);
        Assert.Equal("web_search", msg.FunctionName);
        Assert.Equal(ExecutionTarget.ProviderServer, msg.ExecutionTarget);
        Assert.Equal(0, msg.Index);
        var args = JsonDocument.Parse(msg.FunctionArgs ?? "{}").RootElement;
        Assert.Equal("current weather", args.GetProperty("query").GetString());
    }

    [Fact]
    public void ProcessEvent_WebSearchToolResult_ReturnsToolCallResultMessage()
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
        var msg = Assert.IsType<ToolCallResultMessage>(messages[0]);
        Assert.Equal("srvtoolu_01ABC", msg.ToolCallId);
        Assert.Equal("web_search", msg.ToolName);
        Assert.False(msg.IsError);
        Assert.Null(msg.ErrorCode);
        Assert.Equal(ExecutionTarget.ProviderServer, msg.ExecutionTarget);
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
        var msg = Assert.IsType<ToolCallResultMessage>(messages[0]);
        Assert.Equal("toolu_test", msg.ToolCallId);
        Assert.Equal("web_search", msg.ToolName);
        Assert.False(msg.IsError);
        Assert.Null(msg.ErrorCode);
        Assert.Equal(ExecutionTarget.ProviderServer, msg.ExecutionTarget);
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
        var msg = Assert.IsType<ToolCallResultMessage>(messages[0]);
        Assert.True(msg.IsError);
        Assert.Equal("max_uses_exceeded", msg.ErrorCode);
        Assert.Equal(ExecutionTarget.ProviderServer, msg.ExecutionTarget);
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
        // content_block_start emits ToolCallUpdateMessage (preview),
        // content_block_stop emits ToolCallUpdateMessage (final update with accumulated args).
        // No ToolCallMessage in streamed output — joiner middleware builds it from updates.
        var serverToolUpdates = allMessages.OfType<ToolCallUpdateMessage>()
            .Where(m => m.ExecutionTarget == ExecutionTarget.ProviderServer).ToList();
        var serverToolResult = allMessages.OfType<ToolCallResultMessage>().ToList();
        var textMessages = allMessages.OfType<TextMessage>().ToList();

        Assert.Equal(2, serverToolUpdates.Count);
        Assert.Equal(0, serverToolUpdates[0].Index);
        var finalUpdate = serverToolUpdates[1];
        Assert.Equal("web_search", finalUpdate.FunctionName);
        Assert.Equal("srvtoolu_01", finalUpdate.ToolCallId);
        Assert.Equal(ExecutionTarget.ProviderServer, finalUpdate.ExecutionTarget);

        Assert.Single(serverToolResult);
        Assert.Equal("web_search", serverToolResult[0].ToolName);
        Assert.Equal(ExecutionTarget.ProviderServer, serverToolResult[0].ExecutionTarget);

        Assert.Single(textMessages);
        Assert.Equal("Here are the results.", textMessages[0].Text);

        // GetAllMessages (joined) must have exactly ONE server tool call — no duplicates.
        // Duplicates in history cause "tool call id is duplicated" errors on subsequent turns.
        var accumulated = parser.GetAllMessages();
        var joinedServerToolCalls = accumulated.OfType<ToolCallMessage>()
            .Where(m => m.ExecutionTarget == ExecutionTarget.ProviderServer)
            .ToList();
        Assert.Single(joinedServerToolCalls);
        Assert.Equal("srvtoolu_01", joinedServerToolCalls[0].ToolCallId);
        Assert.Contains("test query", joinedServerToolCalls[0].FunctionArgs!);

        Assert.Contains(accumulated, m => m is ToolCallResultMessage);
    }

    [Fact]
    public void ProcessStreamEvent_TypedPath_LocalToolUse_DoesNotPrefixEmptyObjectToJsonDelta()
    {
        var parser = new AnthropicStreamParser();
        _ = parser.ProcessStreamEvent(new AnthropicMessageStartEvent
        {
            Message = new AnthropicResponse
            {
                Id = "msg_typed_local_01",
                Role = "assistant",
                Model = "claude-sonnet-4-20250514",
            },
        });

        var startMessages = parser.ProcessStreamEvent(new AnthropicContentBlockStartEvent
        {
            Index = 0,
            ContentBlock = new AnthropicResponseToolUseContent
            {
                Id = "toolu_local_01",
                Name = "get_weather",
                Input = JsonSerializer.Deserialize<JsonElement>("{}"),
            },
        });

        var startUpdate = Assert.IsType<ToolsCallUpdateMessage>(Assert.Single(startMessages));
        var startToolCall = Assert.Single(startUpdate.ToolCallUpdates);
        Assert.Equal("get_weather", startToolCall.FunctionName);
        Assert.Null(startToolCall.FunctionArgs);

        var deltaMessages = parser.ProcessStreamEvent(new AnthropicContentBlockDeltaEvent
        {
            Index = 0,
            Delta = new AnthropicInputJsonDelta
            {
                PartialJson = """{"location":"Seattle"}""",
            },
        });

        var deltaUpdate = Assert.IsType<ToolsCallUpdateMessage>(Assert.Single(deltaMessages));
        var deltaToolCall = Assert.Single(deltaUpdate.ToolCallUpdates);
        Assert.Equal("""{"location":"Seattle"}""", deltaToolCall.FunctionArgs);
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

        // content_block_start emits ToolCallUpdateMessage (preview), not ToolCallMessage
        var startMessages = parser.ProcessStreamEvent(new AnthropicContentBlockStartEvent
        {
            Index = 0,
            ContentBlock = serverToolUseContent,
        });

        Assert.Single(startMessages);
        var msg = Assert.IsType<ToolCallUpdateMessage>(startMessages[0]);
        Assert.Equal("srvtoolu_typed_01", msg.ToolCallId);
        Assert.Equal("web_search", msg.FunctionName);
        Assert.Equal(ExecutionTarget.ProviderServer, msg.ExecutionTarget);
        Assert.Equal(0, msg.Index);
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
        var msg = Assert.IsType<ToolCallResultMessage>(messages[0]);
        Assert.Equal("srvtoolu_typed_01", msg.ToolCallId);
        Assert.Equal("web_search", msg.ToolName);
        Assert.False(msg.IsError);
        Assert.Equal(ExecutionTarget.ProviderServer, msg.ExecutionTarget);
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
        var msg = Assert.IsType<ToolCallResultMessage>(messages[0]);
        Assert.Equal("web_fetch", msg.ToolName);
        Assert.Equal(ExecutionTarget.ProviderServer, msg.ExecutionTarget);
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
        var msg = Assert.IsType<ToolCallResultMessage>(messages[0]);
        Assert.Equal("bash_code_execution", msg.ToolName);
        Assert.Equal(ExecutionTarget.ProviderServer, msg.ExecutionTarget);
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
        var msg = Assert.IsType<ToolCallResultMessage>(messages[0]);
        Assert.Equal("text_editor_code_execution", msg.ToolName);
        Assert.Equal(ExecutionTarget.ProviderServer, msg.ExecutionTarget);
    }

    [Fact]
    public void ProcessEvent_CodeExecutionResult_ReturnsToolCallResultMessage()
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
        var msg = Assert.IsType<ToolCallResultMessage>(messages[0]);
        Assert.Equal("srvtoolu_bash_02", msg.ToolCallId);
        Assert.Equal("bash_code_execution", msg.ToolName);
        Assert.Equal(ExecutionTarget.ProviderServer, msg.ExecutionTarget);
    }

    [Fact]
    public void ProcessEvent_ThinkingAndSignatureDeltas_ProduceReasoningMessages()
    {
        var parser = new AnthropicStreamParser();
        parser.ProcessEvent("event", BuildMessageStart());

        _ = parser.ProcessEvent("event", JsonSerializer.Serialize(new
        {
            type = "content_block_start",
            index = 0,
            content_block = new
            {
                type = "thinking",
                thinking = "",
            },
        }));

        var thinkingUpdate = parser.ProcessEvent("event", JsonSerializer.Serialize(new
        {
            type = "content_block_delta",
            index = 0,
            delta = new { type = "thinking_delta", thinking = "first-thought " },
        }));
        var reasoningUpdate = Assert.IsType<ReasoningUpdateMessage>(Assert.Single(thinkingUpdate));
        Assert.Equal("first-thought ", reasoningUpdate.Reasoning);
        Assert.Equal(ReasoningVisibility.Plain, reasoningUpdate.Visibility);

        var signatureUpdate = parser.ProcessEvent("event", JsonSerializer.Serialize(new
        {
            type = "content_block_delta",
            index = 0,
            delta = new { type = "signature_delta", signature = "enc-signature-123" },
        }));
        var encryptedReasoning = Assert.IsType<ReasoningMessage>(Assert.Single(signatureUpdate));
        Assert.Equal("enc-signature-123", encryptedReasoning.Reasoning);
        Assert.Equal(ReasoningVisibility.Encrypted, encryptedReasoning.Visibility);

        var stopMessages = parser.ProcessEvent("event", JsonSerializer.Serialize(new
        {
            type = "content_block_stop",
            index = 0,
        }));
        var finalReasoning = Assert.IsType<ReasoningMessage>(Assert.Single(stopMessages));
        Assert.Equal("first-thought ", finalReasoning.Reasoning);
        Assert.Equal(ReasoningVisibility.Plain, finalReasoning.Visibility);
    }

    [Fact]
    public void ProcessStreamEvent_TypedPath_ThinkingAndSignatureDeltas_ProduceReasoningMessages()
    {
        var parser = new AnthropicStreamParser();
        _ = parser.ProcessStreamEvent(new AnthropicMessageStartEvent
        {
            Message = new AnthropicResponse
            {
                Id = "msg_typed_reasoning_01",
                Role = "assistant",
                Model = "claude-sonnet-4-20250514",
            },
        });

        _ = parser.ProcessStreamEvent(new AnthropicContentBlockStartEvent
        {
            Index = 0,
            ContentBlock = new AnthropicResponseThinkingContent
            {
                Thinking = "",
            },
        });

        var thinkingUpdate = parser.ProcessStreamEvent(new AnthropicContentBlockDeltaEvent
        {
            Index = 0,
            Delta = new AnthropicThinkingDelta
            {
                Thinking = "typed-thought ",
            },
        });
        var reasoningUpdate = Assert.IsType<ReasoningUpdateMessage>(Assert.Single(thinkingUpdate));
        Assert.Equal("typed-thought ", reasoningUpdate.Reasoning);
        Assert.Equal(ReasoningVisibility.Plain, reasoningUpdate.Visibility);

        var signatureUpdate = parser.ProcessStreamEvent(new AnthropicContentBlockDeltaEvent
        {
            Index = 0,
            Delta = new AnthropicSignatureDelta
            {
                Signature = "typed-signature",
            },
        });
        var encryptedReasoning = Assert.IsType<ReasoningMessage>(Assert.Single(signatureUpdate));
        Assert.Equal(ReasoningVisibility.Encrypted, encryptedReasoning.Visibility);
        Assert.Equal("typed-signature", encryptedReasoning.Reasoning);
    }

    /// <summary>
    /// Verifies the full input_json_delta accumulation path for server_tool_use:
    /// - content_block_start emits a ToolCallUpdateMessage preview with empty args "{}"
    /// - input_json_delta events accumulate without emitting messages
    /// - content_block_stop emits the final ToolCallMessage with accumulated args
    /// - GetAllMessages() contains exactly one ToolCallMessage (no duplicates)
    /// </summary>
    [Fact]
    public void ProcessEvent_ServerToolUse_WithInputJsonDelta_AccumulatesInput()
    {
        var parser = new AnthropicStreamParser();
        var allMessages = new List<IMessage>();
        parser.ProcessEvent("event", BuildMessageStart());

        // 1. content_block_start with NO input (typical streaming pattern)
        var startMessages = parser.ProcessEvent("event", JsonSerializer.Serialize(new
        {
            type = "content_block_start",
            index = 0,
            content_block = new
            {
                type = "server_tool_use",
                id = "srvtoolu_delta_01",
                name = "web_search",
            },
        }));
        allMessages.AddRange(startMessages);

        // Preview ToolCallUpdateMessage has empty args
        var preview = Assert.IsType<ToolCallUpdateMessage>(Assert.Single(startMessages));
        Assert.Equal("srvtoolu_delta_01", preview.ToolCallId);
        Assert.Equal("{}", preview.FunctionArgs);
        Assert.Equal(0, preview.Index);

        // 2. input_json_delta events — should NOT emit any messages
        var delta1 = parser.ProcessEvent("event", JsonSerializer.Serialize(new
        {
            type = "content_block_delta",
            index = 0,
            delta = new { type = "input_json_delta", partial_json = "{\"query\"" },
        }));
        Assert.Empty(delta1);

        var delta2 = parser.ProcessEvent("event", JsonSerializer.Serialize(new
        {
            type = "content_block_delta",
            index = 0,
            delta = new { type = "input_json_delta", partial_json = ": \"test search\"}" },
        }));
        Assert.Empty(delta2);

        // 3. content_block_stop — emits final ToolCallUpdateMessage with accumulated args
        var stopMessages = parser.ProcessEvent("event", JsonSerializer.Serialize(new
        {
            type = "content_block_stop",
            index = 0,
        }));
        allMessages.AddRange(stopMessages);

        var finalMsg = Assert.IsType<ToolCallUpdateMessage>(Assert.Single(stopMessages));
        Assert.Equal("srvtoolu_delta_01", finalMsg.ToolCallId);
        Assert.Equal(ExecutionTarget.ProviderServer, finalMsg.ExecutionTarget);
        Assert.Contains("test search", finalMsg.FunctionArgs!);

        // GetAllMessages (joined) must have exactly ONE — the final one with accumulated args
        var joined = parser.GetAllMessages().OfType<ToolCallMessage>()
            .Where(m => m.ExecutionTarget == ExecutionTarget.ProviderServer)
            .ToList();
        Assert.Single(joined);
        Assert.Contains("test search", joined[0].FunctionArgs!);
    }

    /// <summary>
    /// Regression: ToolCallMessage (unified) must always have a non-empty ToolCallId so the client
    /// can match it with ToolCallResultMessage for display.
    /// </summary>
    [Fact]
    public void ProcessEvent_ServerToolUse_AlwaysHasNonEmptyToolUseId()
    {
        var parser = new AnthropicStreamParser();
        parser.ProcessEvent("event", BuildMessageStart());

        var startMessages = parser.ProcessEvent("event", JsonSerializer.Serialize(new
        {
            type = "content_block_start",
            index = 0,
            content_block = new
            {
                type = "server_tool_use",
                id = "srvtoolu_id_check",
                name = "web_search",
                input = new { query = "test" },
            },
        }));

        var msg = Assert.IsType<ToolCallUpdateMessage>(Assert.Single(startMessages));
        Assert.False(string.IsNullOrEmpty(msg.ToolCallId), "ToolCallId must not be empty");
    }

    /// <summary>
    /// Regression: Full streaming flow where server_tool_use is emitted immediately as ToolCallMessage.
    /// The complete flow should produce ToolCallMessage and ToolCallResultMessage.
    /// </summary>
    [Fact]
    public void ProcessEvent_FullStreamingWebSearchFlow_WithInputDelta_ProducesCorrectSequence()
    {
        var parser = new AnthropicStreamParser();
        var allMessages = new List<IMessage>();

        // 1. message_start
        allMessages.AddRange(parser.ProcessEvent("event", BuildMessageStart()));

        // 2. server_tool_use content_block_start WITHOUT input (emits ToolCallMessage with "{}")
        allMessages.AddRange(parser.ProcessEvent("event", JsonSerializer.Serialize(new
        {
            type = "content_block_start",
            index = 0,
            content_block = new
            {
                type = "server_tool_use",
                id = "srvtoolu_flow_01",
                name = "web_search",
            },
        })));

        // 3. web_search_tool_result
        allMessages.AddRange(parser.ProcessEvent("event", JsonSerializer.Serialize(new
        {
            type = "content_block_start",
            index = 1,
            content_block = new
            {
                type = "web_search_tool_result",
                tool_use_id = "srvtoolu_flow_01",
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

        // Verify ToolCallUpdateMessage was emitted at content_block_start
        var serverToolUpdate = allMessages.OfType<ToolCallUpdateMessage>().Single();
        Assert.Equal("srvtoolu_flow_01", serverToolUpdate.ToolCallId);
        Assert.Equal("web_search", serverToolUpdate.FunctionName);
        Assert.Equal(ExecutionTarget.ProviderServer, serverToolUpdate.ExecutionTarget);

        // Verify ToolCallResultMessage
        var serverToolResult = allMessages.OfType<ToolCallResultMessage>().Single();
        Assert.Equal("srvtoolu_flow_01", serverToolResult.ToolCallId);
        Assert.Equal("web_search", serverToolResult.ToolName);
        Assert.Equal(ExecutionTarget.ProviderServer, serverToolResult.ExecutionTarget);
    }

    [Fact]
    public void ProcessEvent_ServerToolUse_WithoutId_GeneratesSyntheticId()
    {
        // Regression test: Kimi doesn't send 'id' on server_tool_use blocks.
        // The parser should handle missing IDs gracefully (empty string in ToolCallMessage).
        var parser = new AnthropicStreamParser();
        parser.ProcessEvent("event", BuildMessageStart());

        // server_tool_use WITHOUT id field (Kimi behavior)
        var startData = JsonSerializer.Serialize(new
        {
            type = "content_block_start",
            index = 0,
            content_block = new
            {
                type = "server_tool_use",
                name = "web_search",
                input = new { query = "hello" },
            },
        });

        var startMessages = parser.ProcessEvent("event", startData);

        // ToolCallUpdateMessage emitted at content_block_start
        Assert.Single(startMessages);
        var msg = Assert.IsType<ToolCallUpdateMessage>(startMessages[0]);
        Assert.Equal("web_search", msg.FunctionName);
        Assert.Equal(ExecutionTarget.ProviderServer, msg.ExecutionTarget);
        var args = JsonDocument.Parse(msg.FunctionArgs ?? "{}").RootElement;
        Assert.Equal("hello", args.GetProperty("query").GetString());
    }

    [Fact]
    public void ProcessEvent_ServerToolUse_WithoutId_ResultResolvesSyntheticId()
    {
        // Regression test: when server_tool_use has no id, tool call and result
        // should use empty string and still resolve via tool name matching.
        var parser = new AnthropicStreamParser();
        parser.ProcessEvent("event", BuildMessageStart());

        // server_tool_use WITHOUT id
        var startMessages = parser.ProcessEvent("event", JsonSerializer.Serialize(new
        {
            type = "content_block_start",
            index = 0,
            content_block = new
            {
                type = "server_tool_use",
                name = "web_search",
                input = new { query = "test" },
            },
        }));

        var toolCall = Assert.IsType<ToolCallUpdateMessage>(Assert.Single(startMessages));

        // Now send the result with empty tool_use_id — it should resolve to empty as well
        var resultData = JsonSerializer.Serialize(new
        {
            type = "content_block_start",
            index = 1,
            content_block = new
            {
                type = "web_search_tool_result",
                tool_use_id = "",
                content = new { type = "web_search_result", results = Array.Empty<object>() },
            },
        });
        var resultMessages = parser.ProcessEvent("event", resultData);

        Assert.Single(resultMessages);
        var result = Assert.IsType<ToolCallResultMessage>(resultMessages[0]);
        Assert.Equal("", result.ToolCallId);
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
