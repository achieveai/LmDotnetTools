using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Tests.Agents;

public class ServerToolIntegrationTests : LoggingTestBase
{
    public ServerToolIntegrationTests(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public async Task WebSearch_RequestCapture_VerifiesBuiltInToolsSent()
    {
        Logger.LogTrace("Starting WebSearch_RequestCapture_VerifiesBuiltInToolsSent");

        var requestCapture = new RequestCapture();
        var httpClient = TestModeHttpClientFactory.CreateAnthropicTestClient(
            LoggerFactory, requestCapture, chunkDelayMs: 0
        );
        var anthropicClient = new AnthropicClient("test-api-key", httpClient: httpClient);
        var agent = new AnthropicAgent("TestAgent", anthropicClient, LoggerFactory.CreateLogger<AnthropicAgent>());

        var messages = new[]
        {
            new TextMessage { Role = Role.User, Text = "What's the weather in San Francisco today?" },
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "claude-sonnet-4-20250514",
            BuiltInTools = new List<object>
            {
                new AnthropicWebSearchTool
                {
                    MaxUses = 3,
                    AllowedDomains = ["weather.com"],
                    UserLocation = new UserLocation
                    {
                        City = "San Francisco",
                        Region = "California",
                        Country = "US",
                        Timezone = "America/Los_Angeles",
                    },
                },
            },
        };

        Logger.LogTrace("Calling GenerateReplyAsync with web_search built-in tool");
        _ = await agent.GenerateReplyAsync(messages, options);

        // Assert the request was captured
        Assert.Equal(1, requestCapture.RequestCount);

        var capturedRequest = requestCapture.GetAnthropicRequest();
        Assert.NotNull(capturedRequest);
        Assert.Equal("claude-sonnet-4-20250514", capturedRequest.Model);

        // Verify tools contain the web_search built-in tool
        var tools = capturedRequest.Tools.ToList();
        Assert.NotEmpty(tools);

        // The built-in tool should have a Type property
        var webSearchTool = tools.FirstOrDefault(t => t.Type == "web_search_20250305");
        Assert.NotNull(webSearchTool);
        Assert.Equal("web_search", webSearchTool!.Name);

        Logger.LogTrace("Successfully verified built-in web_search tool was sent in request");
    }

    [Fact]
    public async Task WebSearch_WithFunctionTools_BothAppearInRequest()
    {
        Logger.LogTrace("Starting WebSearch_WithFunctionTools_BothAppearInRequest");

        var requestCapture = new RequestCapture();
        var httpClient = TestModeHttpClientFactory.CreateAnthropicTestClient(
            LoggerFactory, requestCapture, chunkDelayMs: 0
        );
        var anthropicClient = new AnthropicClient("test-api-key", httpClient: httpClient);
        var agent = new AnthropicAgent("TestAgent", anthropicClient, LoggerFactory.CreateLogger<AnthropicAgent>());

        var messages = new[]
        {
            new TextMessage { Role = Role.User, Text = "Search the web and get weather for SF." },
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "claude-sonnet-4-20250514",
            BuiltInTools = new List<object>
            {
                new AnthropicWebSearchTool { MaxUses = 5 },
            },
            Functions =
            [
                new FunctionContract
                {
                    Name = "get_weather",
                    Description = "Get current weather",
                    Parameters =
                    [
                        new FunctionParameterContract
                        {
                            Name = "location",
                            ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(string)),
                            Description = "City name",
                        },
                    ],
                },
            ],
        };

        _ = await agent.GenerateReplyAsync(messages, options);

        var capturedRequest = requestCapture.GetAnthropicRequest();
        Assert.NotNull(capturedRequest);

        var tools = capturedRequest.Tools.ToList();
        Assert.Equal(2, tools.Count);

        // Verify we have both a built-in tool and a function tool
        Assert.Contains(tools, t => t.Type == "web_search_20250305");
        Assert.Contains(tools, t => t.Name == "get_weather" && t.Description != null);

        Logger.LogTrace("Verified both built-in and function tools in request");
    }

    [Fact]
    public async Task ToolsListPlaceholder_WithBuiltInAndFunctionTools_IncludesAllToolNames()
    {
        Logger.LogTrace("Starting ToolsListPlaceholder_WithBuiltInAndFunctionTools_IncludesAllToolNames");

        var httpClient = TestModeHttpClientFactory.CreateAnthropicTestClient(LoggerFactory, chunkDelayMs: 0);
        var anthropicClient = new AnthropicClient("test-api-key", httpClient: httpClient);
        var agent = new AnthropicAgent("TestAgent", anthropicClient, LoggerFactory.CreateLogger<AnthropicAgent>());

        var instructionChainMessage = """
            <|instruction_start|>{"instruction_chain":[{"id_message":"tools-list","messages":[{"tools_list":{}}]}]}<|instruction_end|>
            """;

        var messages = new[]
        {
            new TextMessage { Role = Role.User, Text = instructionChainMessage },
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "claude-sonnet-4-20250514",
            BuiltInTools = new List<object> { new AnthropicWebSearchTool() },
            Functions =
            [
                new FunctionContract
                {
                    Name = "get_weather",
                    Description = "Get weather information for a location",
                },
                new FunctionContract
                {
                    Name = "calculate",
                    Description = "Calculate a math expression",
                },
            ],
        };

        var responseMessages = await agent.GenerateReplyAsync(messages, options);
        var textMessages = responseMessages.OfType<TextMessage>().Select(m => m.Text ?? string.Empty).ToList();
        var mergedText = string.Join("\n", textMessages);

        Assert.Contains("get_weather", mergedText);
        Assert.Contains("calculate", mergedText);
        Assert.Contains("web_search", mergedText);

        Logger.LogTrace("Verified tools_list placeholder includes function tools and built-in web_search");
    }

    [Fact]
    public async Task WebSearch_NonStreaming_EndToEnd()
    {
        Logger.LogTrace("Starting WebSearch_NonStreaming_EndToEnd");

        // Create a canned server tool response
        var responseJson = """
            {
                "id": "msg_e2e_01",
                "type": "message",
                "role": "assistant",
                "model": "claude-sonnet-4-20250514",
                "content": [
                    {"type": "text", "text": "Let me search for that."},
                    {"type": "server_tool_use", "id": "srvtoolu_e2e_01", "name": "web_search", "input": {"query": "weather SF"}},
                    {"type": "web_search_tool_result", "tool_use_id": "srvtoolu_e2e_01", "content": [
                        {"type": "web_search_result", "url": "https://weather.example.com", "title": "SF Weather", "encrypted_content": "enc...", "page_age": "1 hour ago"}
                    ]},
                    {"type": "text", "text": "The weather in SF is 65°F and sunny."}
                ],
                "stop_reason": "end_turn",
                "usage": {"input_tokens": 150, "output_tokens": 100}
            }
            """;

        var handler = FakeHttpMessageHandler.CreateSimpleJsonHandler(responseJson);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.anthropic.com/v1") };
        var anthropicClient = new AnthropicClient("test-api-key", httpClient: httpClient);
        var agent = new AnthropicAgent("TestAgent", anthropicClient, LoggerFactory.CreateLogger<AnthropicAgent>());

        var messages = new[]
        {
            new TextMessage { Role = Role.User, Text = "What's the weather in SF?" },
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "claude-sonnet-4-20250514",
            BuiltInTools = new List<object> { new AnthropicWebSearchTool() },
        };

        Logger.LogTrace("Calling GenerateReplyAsync with canned response");
        var responseMessages = await agent.GenerateReplyAsync(messages, options);

        Assert.NotNull(responseMessages);
        var responseList = responseMessages.ToList();
        Assert.NotEmpty(responseList);

        Logger.LogTrace("Got {Count} response messages", responseList.Count);
        foreach (var msg in responseList)
        {
            Logger.LogTrace("Response message: {MessageType}, Role={Role}",
                msg.GetType().Name, msg.Role);
        }

        // Verify we got server tool messages
        Assert.Contains(responseList, m => m is ToolCallMessage);
        Assert.Contains(responseList, m => m is ToolCallResultMessage);
        Assert.Contains(responseList, m => m is TextMessage);

        // Verify specifics
        var toolUse = responseList.OfType<ToolCallMessage>().First();
        Assert.Equal("web_search", toolUse.FunctionName);
        Assert.Equal("srvtoolu_e2e_01", toolUse.ToolCallId);
        Assert.Equal(ExecutionTarget.ProviderServer, toolUse.ExecutionTarget);

        var toolResult = responseList.OfType<ToolCallResultMessage>().First();
        Assert.Equal("web_search", toolResult.ToolName);
        Assert.False(toolResult.IsError);
        Assert.Equal(ExecutionTarget.ProviderServer, toolResult.ExecutionTarget);
    }

    [Fact]
    public async Task WebSearch_Streaming_EndToEnd_WithSseEvents()
    {
        Logger.LogTrace("Starting WebSearch_Streaming_EndToEnd_WithSseEvents");

        // Build SSE events that simulate a full web_search streaming flow
        var sseEvents = BuildWebSearchSseEvents();
        Logger.LogTrace("Built {EventCount} SSE events for streaming simulation", sseEvents.Count);

        var handler = FakeHttpMessageHandler.CreateSseStreamHandler(sseEvents);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.anthropic.com/v1") };
        var anthropicClient = new AnthropicClient("test-api-key", httpClient: httpClient);
        var agent = new AnthropicAgent("TestAgent", anthropicClient, LoggerFactory.CreateLogger<AnthropicAgent>());

        var messages = new[]
        {
            new TextMessage { Role = Role.User, Text = "Search for latest AI news" },
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "claude-sonnet-4-20250514",
            BuiltInTools = new List<object> { new AnthropicWebSearchTool() },
        };

        // Use streaming to collect messages
        var responseMessages = new List<IMessage>();
        await foreach (var msg in await agent.GenerateReplyStreamingAsync(messages, options))
        {
            responseMessages.Add(msg);
            Logger.LogTrace("Streamed message: {MessageType}, Role={Role}",
                msg.GetType().Name, msg.Role);
        }

        Logger.LogTrace("Streaming complete, got {Count} messages", responseMessages.Count);

        // Verify the full message sequence
        Assert.NotEmpty(responseMessages);

        // Should have: text, server_tool_use, server_tool_result, text (with citations)
        var serverToolUse = responseMessages.OfType<ToolCallMessage>().FirstOrDefault();
        Assert.NotNull(serverToolUse);
        Assert.Equal("web_search", serverToolUse!.FunctionName);
        Assert.Equal("srvtoolu_stream_01", serverToolUse.ToolCallId);
        Assert.Equal(ExecutionTarget.ProviderServer, serverToolUse.ExecutionTarget);
        Logger.LogTrace(
            "ServerToolUse verified: {ToolName}, Id={ToolUseId}",
            serverToolUse.FunctionName,
            serverToolUse.ToolCallId
        );

        var serverToolResult = responseMessages.OfType<ToolCallResultMessage>().FirstOrDefault();
        Assert.NotNull(serverToolResult);
        Assert.Equal("web_search", serverToolResult!.ToolName);
        Assert.False(serverToolResult.IsError);
        Assert.Equal(ExecutionTarget.ProviderServer, serverToolResult.ExecutionTarget);
        Logger.LogTrace(
            "ServerToolResult verified: {ToolName}, IsError={IsError}",
            serverToolResult.ToolName,
            serverToolResult.IsError
        );

        // Verify text with citations
        var citationMessages = responseMessages.OfType<TextWithCitationsMessage>().ToList();
        if (citationMessages.Count > 0)
        {
            var cited = citationMessages.First();
            Assert.NotNull(cited.Citations);
            Assert.NotEmpty(cited.Citations!);
            Logger.LogTrace("TextWithCitations verified: {CitationCount} citations",
                cited.Citations!.Count);
        }
    }

    [Fact]
    public async Task WebSearch_Streaming_ErrorResponse_SetsIsError()
    {
        Logger.LogTrace("Starting WebSearch_Streaming_ErrorResponse_SetsIsError");

        var sseEvents = BuildWebSearchErrorSseEvents();
        Logger.LogTrace("Built {EventCount} SSE events for error simulation", sseEvents.Count);

        var handler = FakeHttpMessageHandler.CreateSseStreamHandler(sseEvents);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.anthropic.com/v1") };
        var anthropicClient = new AnthropicClient("test-api-key", httpClient: httpClient);
        var agent = new AnthropicAgent("TestAgent", anthropicClient, LoggerFactory.CreateLogger<AnthropicAgent>());

        var messages = new[]
        {
            new TextMessage { Role = Role.User, Text = "Search for something that fails" },
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "claude-sonnet-4-20250514",
            BuiltInTools = new List<object> { new AnthropicWebSearchTool() },
        };

        var responseMessages = new List<IMessage>();
        await foreach (var msg in await agent.GenerateReplyStreamingAsync(messages, options))
        {
            responseMessages.Add(msg);
            Logger.LogTrace("Streamed message: {MessageType}", msg.GetType().Name);
        }

        // Verify error result
        var toolResult = responseMessages.OfType<ToolCallResultMessage>().FirstOrDefault();
        Assert.NotNull(toolResult);
        Assert.True(toolResult!.IsError);
        Assert.Equal("max_uses_exceeded", toolResult.ErrorCode);
        Assert.Equal(ExecutionTarget.ProviderServer, toolResult.ExecutionTarget);
        Logger.LogTrace("Error result verified: IsError={IsError}, ErrorCode={ErrorCode}",
            toolResult.IsError, toolResult.ErrorCode);
    }

    [Fact]
    public async Task WebSearch_Streaming_ViaAnthropicTestSseHandler_InstructionChain()
    {
        Logger.LogTrace("Starting WebSearch_Streaming_ViaAnthropicTestSseHandler_InstructionChain");

        // Use AnthropicTestSseMessageHandler with an instruction chain that includes server tool content
        // The instruction chain JSON must be wrapped in <|instruction_start|> and <|instruction_end|> tags
        var instructionChainMessage = """
            <|instruction_start|>{"instruction_chain": [
                {"messages": [
                    {"server_tool_use": {"name": "web_search", "id": "srvtoolu_chain_01", "input": {"query": "test query"}}},
                    {"server_tool_result": {"name": "web_search", "tool_use_id": "srvtoolu_chain_01"}},
                    {"text_with_citations": {"text": "Based on my search, here are the results.", "citations": [{"type": "web_search_result_location", "url": "https://example.com", "title": "Example", "cited_text": "relevant text"}]}}
                ]}
            ]}<|instruction_end|>
            """;

        var requestCapture = new RequestCapture();
        var httpClient = TestModeHttpClientFactory.CreateAnthropicTestClient(
            LoggerFactory, requestCapture, chunkDelayMs: 0
        );
        var anthropicClient = new AnthropicClient("test-api-key", httpClient: httpClient);
        var agent = new AnthropicAgent("TestAgent", anthropicClient, LoggerFactory.CreateLogger<AnthropicAgent>());

        var messages = new[]
        {
            new TextMessage { Role = Role.User, Text = instructionChainMessage },
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "claude-sonnet-4-20250514",
            BuiltInTools = new List<object> { new AnthropicWebSearchTool() },
        };

        // Use streaming
        var responseMessages = new List<IMessage>();
        await foreach (var msg in await agent.GenerateReplyStreamingAsync(messages, options))
        {
            responseMessages.Add(msg);
            Logger.LogTrace("Instruction chain streamed: {MessageType}, Role={Role}",
                msg.GetType().Name, msg.Role);
        }

        Logger.LogTrace("Instruction chain streaming complete, got {Count} messages", responseMessages.Count);

        // Verify request was captured with built-in tools
        Assert.Equal(1, requestCapture.RequestCount);
        var capturedRequest = requestCapture.GetAnthropicRequest();
        Assert.NotNull(capturedRequest);
        var tools = capturedRequest.Tools.ToList();
        Assert.Contains(tools, t => t.Type == "web_search_20250305");
        Logger.LogTrace("Captured request has {ToolCount} tools, web_search present", tools.Count);

        // Verify the streamed response contains server tool messages
        Assert.NotEmpty(responseMessages);

        var serverToolUse = responseMessages.OfType<ToolCallMessage>().FirstOrDefault();
        Assert.NotNull(serverToolUse);
        Assert.Equal("web_search", serverToolUse!.FunctionName);
        Assert.Equal("srvtoolu_chain_01", serverToolUse.ToolCallId);
        Assert.Equal(ExecutionTarget.ProviderServer, serverToolUse.ExecutionTarget);

        var serverToolResult = responseMessages.OfType<ToolCallResultMessage>().FirstOrDefault();
        Assert.NotNull(serverToolResult);
        Assert.Equal("web_search", serverToolResult!.ToolName);
        Assert.Equal(ExecutionTarget.ProviderServer, serverToolResult.ExecutionTarget);

        var citedText = responseMessages.OfType<TextWithCitationsMessage>().FirstOrDefault();
        Assert.NotNull(citedText);
        Assert.NotNull(citedText!.Citations);
        Assert.NotEmpty(citedText.Citations!);
        Assert.Equal("https://example.com", citedText.Citations![0].Url);

        Logger.LogTrace("Instruction chain test passed: ServerToolUse, ServerToolResult, TextWithCitations all verified");
    }

    [Fact]
    public async Task MultipleBuiltInTools_AllAppearInRequest()
    {
        Logger.LogTrace("Starting MultipleBuiltInTools_AllAppearInRequest");

        var requestCapture = new RequestCapture();
        var httpClient = TestModeHttpClientFactory.CreateAnthropicTestClient(
            LoggerFactory, requestCapture, chunkDelayMs: 0
        );
        var anthropicClient = new AnthropicClient("test-api-key", httpClient: httpClient);
        var agent = new AnthropicAgent("TestAgent", anthropicClient, LoggerFactory.CreateLogger<AnthropicAgent>());

        var messages = new[]
        {
            new TextMessage { Role = Role.User, Text = "Search, fetch, and run code." },
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "claude-sonnet-4-20250514",
            BuiltInTools = new List<object>
            {
                new AnthropicWebSearchTool { MaxUses = 5 },
                new AnthropicWebFetchTool { MaxUses = 3 },
                new AnthropicCodeExecutionTool(),
            },
        };

        _ = await agent.GenerateReplyAsync(messages, options);

        var capturedRequest = requestCapture.GetAnthropicRequest();
        Assert.NotNull(capturedRequest);

        var tools = capturedRequest.Tools.ToList();
        Assert.Equal(3, tools.Count);

        Assert.Contains(tools, t => t.Type == "web_search_20250305");
        Assert.Contains(tools, t => t.Type == "web_fetch_20250910");
        Assert.Contains(tools, t => t.Type == "code_execution_20250825");

        Logger.LogTrace("Verified all 3 built-in tools in request");
    }

    [Fact]
    public async Task WebSearch_Streaming_MultiTurn_ShouldNotThrowOnSecondRequest()
    {
        Logger.LogTrace("Starting WebSearch_Streaming_MultiTurn_ShouldNotThrowOnSecondRequest");

        // Turn 1: instruction chain with server tool content
        var turn1Instruction = """
            <|instruction_start|>{"instruction_chain": [
                {"messages": [
                    {"server_tool_use": {"name": "web_search", "id": "srvtoolu_t1_01", "input": {"query": "first query"}}},
                    {"server_tool_result": {"name": "web_search", "tool_use_id": "srvtoolu_t1_01"}},
                    {"text_with_citations": {"text": "Here are the results from the first search.", "citations": [{"type": "web_search_result_location", "url": "https://example.com/1", "title": "Result 1", "cited_text": "first result text"}]}}
                ]}
            ]}<|instruction_end|>
            """;

        var requestCapture = new RequestCapture();
        var httpClient = TestModeHttpClientFactory.CreateAnthropicTestClient(
            LoggerFactory, requestCapture, chunkDelayMs: 0
        );
        var anthropicClient = new AnthropicClient("test-api-key", httpClient: httpClient);
        var agent = new AnthropicAgent("TestAgent", anthropicClient, LoggerFactory.CreateLogger<AnthropicAgent>());

        var options = new GenerateReplyOptions
        {
            ModelId = "claude-sonnet-4-20250514",
            BuiltInTools = new List<object> { new AnthropicWebSearchTool() },
        };

        // Turn 1: send instruction chain and collect response
        var turn1Messages = new IMessage[]
        {
            new TextMessage { Role = Role.User, Text = turn1Instruction },
        };

        var turn1Responses = new List<IMessage>();
        await foreach (var msg in await agent.GenerateReplyStreamingAsync(turn1Messages, options))
        {
            turn1Responses.Add(msg);
            Logger.LogTrace("Turn 1 message: {MessageType}, Role={Role}",
                msg.GetType().Name, msg.Role);
        }

        Logger.LogTrace("Turn 1 complete, got {Count} messages", turn1Responses.Count);
        Assert.NotEmpty(turn1Responses);
        Assert.Contains(turn1Responses, m => m is ToolCallMessage);
        Assert.Contains(turn1Responses, m => m is ToolCallResultMessage);

        // Verify the serialized payload fields are accessible after turn 1
        var toolUse1 = turn1Responses.OfType<ToolCallMessage>().First();
        Assert.Equal(ExecutionTarget.ProviderServer, toolUse1.ExecutionTarget);
        var inputJson = toolUse1.FunctionArgs;
        Logger.LogTrace("Turn 1 ServerToolUse Input accessible: {Input}", inputJson);

        var toolResult1 = turn1Responses.OfType<ToolCallResultMessage>().First();
        Assert.Equal(ExecutionTarget.ProviderServer, toolResult1.ExecutionTarget);
        var resultJson = toolResult1.Result;
        Logger.LogTrace("Turn 1 ServerToolResult Result accessible: {Result}", resultJson);

        // Turn 2: Build history = [original user, turn1 responses, new user message]
        // This is where the bug manifests: AnthropicRequest.FromMessages() tries to serialize
        // the JsonElement fields from turn 1, which would be stale if not cloned
        var turn2Instruction = """
            <|instruction_start|>{"instruction_chain": [
                {"messages": [
                    {"text": "Here is the follow-up answer based on additional context."}
                ]}
            ]}<|instruction_end|>
            """;

        var turn2Messages = new List<IMessage>();
        turn2Messages.AddRange(turn1Messages);
        turn2Messages.AddRange(turn1Responses);
        turn2Messages.Add(new TextMessage { Role = Role.User, Text = turn2Instruction });

        Logger.LogTrace("Turn 2: sending {Count} messages in history", turn2Messages.Count);

        // Turn 2: This call will serialize all previous messages (including server tool
        // messages with JsonElement) into the Anthropic request format.
        // Without .Clone(), this would throw InvalidOperationException.
        var turn2Responses = new List<IMessage>();
        var turn2Exception = await Record.ExceptionAsync(async () =>
        {
            await foreach (var msg in await agent.GenerateReplyStreamingAsync(turn2Messages, options))
            {
                turn2Responses.Add(msg);
                Logger.LogTrace("Turn 2 message: {MessageType}, Role={Role}",
                    msg.GetType().Name, msg.Role);
            }
        });

        Assert.Null(turn2Exception);
        Logger.LogTrace("Turn 2 complete, got {Count} messages (no exception)", turn2Responses.Count);
        Assert.NotEmpty(turn2Responses);

        // Verify the second request was sent successfully
        Assert.Equal(2, requestCapture.RequestCount);
    }

    /// <summary>
    ///     Builds SSE events simulating a successful web_search streaming flow:
    ///     text -> server_tool_use -> web_search_tool_result -> text with citations
    /// </summary>
    private static List<SseEvent> BuildWebSearchSseEvents()
    {
        var events = new List<SseEvent>();

        // message_start
        events.Add(new SseEvent
        {
            Event = "message_start",
            Data = JsonSerializer.Serialize(new
            {
                type = "message_start",
                message = new
                {
                    id = "msg_stream_01",
                    type = "message",
                    role = "assistant",
                    model = "claude-sonnet-4-20250514",
                    content = Array.Empty<object>(),
                    stop_reason = (string?)null,
                    usage = new { input_tokens = 100, output_tokens = 0 },
                },
            }),
        });

        // Content block 0: text "Let me search for that."
        events.Add(new SseEvent
        {
            Event = "content_block_start",
            Data = JsonSerializer.Serialize(new
            {
                type = "content_block_start",
                index = 0,
                content_block = new { type = "text", text = "" },
            }),
        });
        events.Add(new SseEvent
        {
            Event = "content_block_delta",
            Data = JsonSerializer.Serialize(new
            {
                type = "content_block_delta",
                index = 0,
                delta = new { type = "text_delta", text = "Let me search for that." },
            }),
        });
        events.Add(new SseEvent
        {
            Event = "content_block_stop",
            Data = JsonSerializer.Serialize(new { type = "content_block_stop", index = 0 }),
        });

        // Content block 1: server_tool_use (web_search)
        events.Add(new SseEvent
        {
            Event = "content_block_start",
            Data = JsonSerializer.Serialize(new
            {
                type = "content_block_start",
                index = 1,
                content_block = new
                {
                    type = "server_tool_use",
                    id = "srvtoolu_stream_01",
                    name = "web_search",
                    input = new { },
                },
            }),
        });
        events.Add(new SseEvent
        {
            Event = "content_block_delta",
            Data = JsonSerializer.Serialize(new
            {
                type = "content_block_delta",
                index = 1,
                delta = new { type = "input_json_delta", partial_json = "{\"query\":\"latest AI news\"}" },
            }),
        });
        events.Add(new SseEvent
        {
            Event = "content_block_stop",
            Data = JsonSerializer.Serialize(new { type = "content_block_stop", index = 1 }),
        });

        // Content block 2: web_search_tool_result
        events.Add(new SseEvent
        {
            Event = "content_block_start",
            Data = JsonSerializer.Serialize(new
            {
                type = "content_block_start",
                index = 2,
                content_block = new
                {
                    type = "web_search_tool_result",
                    tool_use_id = "srvtoolu_stream_01",
                    content = new[]
                    {
                        new
                        {
                            type = "web_search_result",
                            url = "https://news.example.com/ai",
                            title = "Latest AI News",
                            encrypted_content = "encrypted...",
                            page_age = "2 hours ago",
                        },
                    },
                },
            }),
        });
        events.Add(new SseEvent
        {
            Event = "content_block_stop",
            Data = JsonSerializer.Serialize(new { type = "content_block_stop", index = 2 }),
        });

        // Content block 3: text with citations
        events.Add(new SseEvent
        {
            Event = "content_block_start",
            Data = JsonSerializer.Serialize(new
            {
                type = "content_block_start",
                index = 3,
                content_block = new
                {
                    type = "text",
                    text = "",
                    citations = new[]
                    {
                        new
                        {
                            type = "web_search_result_location",
                            url = "https://news.example.com/ai",
                            title = "Latest AI News",
                            cited_text = "AI developments in 2026",
                        },
                    },
                },
            }),
        });
        events.Add(new SseEvent
        {
            Event = "content_block_delta",
            Data = JsonSerializer.Serialize(new
            {
                type = "content_block_delta",
                index = 3,
                delta = new { type = "text_delta", text = "Based on my search, AI developments in 2026 are significant." },
            }),
        });
        events.Add(new SseEvent
        {
            Event = "content_block_stop",
            Data = JsonSerializer.Serialize(new { type = "content_block_stop", index = 3 }),
        });

        // message_delta and message_stop
        events.Add(new SseEvent
        {
            Event = "message_delta",
            Data = JsonSerializer.Serialize(new
            {
                type = "message_delta",
                delta = new { stop_reason = "end_turn", stop_sequence = (string?)null },
                usage = new { input_tokens = 200, output_tokens = 80 },
            }),
        });
        events.Add(new SseEvent
        {
            Event = "message_stop",
            Data = JsonSerializer.Serialize(new { type = "message_stop" }),
        });

        return events;
    }

    /// <summary>
    ///     Builds SSE events simulating a web_search error response.
    /// </summary>
    private static List<SseEvent> BuildWebSearchErrorSseEvents()
    {
        var events = new List<SseEvent>();

        events.Add(new SseEvent
        {
            Event = "message_start",
            Data = JsonSerializer.Serialize(new
            {
                type = "message_start",
                message = new
                {
                    id = "msg_err_01",
                    type = "message",
                    role = "assistant",
                    model = "claude-sonnet-4-20250514",
                    content = Array.Empty<object>(),
                    stop_reason = (string?)null,
                    usage = new { input_tokens = 50, output_tokens = 0 },
                },
            }),
        });

        // server_tool_use
        events.Add(new SseEvent
        {
            Event = "content_block_start",
            Data = JsonSerializer.Serialize(new
            {
                type = "content_block_start",
                index = 0,
                content_block = new
                {
                    type = "server_tool_use",
                    id = "srvtoolu_err_01",
                    name = "web_search",
                    input = new { },
                },
            }),
        });
        events.Add(new SseEvent
        {
            Event = "content_block_stop",
            Data = JsonSerializer.Serialize(new { type = "content_block_stop", index = 0 }),
        });

        // web_search_tool_result with error
        events.Add(new SseEvent
        {
            Event = "content_block_start",
            Data = JsonSerializer.Serialize(new
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
            }),
        });
        events.Add(new SseEvent
        {
            Event = "content_block_stop",
            Data = JsonSerializer.Serialize(new { type = "content_block_stop", index = 1 }),
        });

        // text response after error
        events.Add(new SseEvent
        {
            Event = "content_block_start",
            Data = JsonSerializer.Serialize(new
            {
                type = "content_block_start",
                index = 2,
                content_block = new { type = "text", text = "" },
            }),
        });
        events.Add(new SseEvent
        {
            Event = "content_block_delta",
            Data = JsonSerializer.Serialize(new
            {
                type = "content_block_delta",
                index = 2,
                delta = new { type = "text_delta", text = "I was unable to search due to rate limits." },
            }),
        });
        events.Add(new SseEvent
        {
            Event = "content_block_stop",
            Data = JsonSerializer.Serialize(new { type = "content_block_stop", index = 2 }),
        });

        events.Add(new SseEvent
        {
            Event = "message_delta",
            Data = JsonSerializer.Serialize(new
            {
                type = "message_delta",
                delta = new { stop_reason = "end_turn", stop_sequence = (string?)null },
                usage = new { input_tokens = 50, output_tokens = 20 },
            }),
        });
        events.Add(new SseEvent
        {
            Event = "message_stop",
            Data = JsonSerializer.Serialize(new { type = "message_stop" }),
        });

        return events;
    }
}
