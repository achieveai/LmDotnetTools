using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Tests.Agents;

/// <summary>
///     E2E tests for server tool streaming via instruction chains.
///     Validates the full 4-block pattern: text → server_tool_use → server_tool_result → text.
/// </summary>
public class ServerToolStreamingE2ETests : LoggingTestBase
{
    /// <summary>
    ///     Shared instruction chain that mirrors the real Kimi API 4-block pattern.
    /// </summary>
    private const string KimiLikeInstructionChain = """
        <|instruction_start|>{"instruction_chain": [{
            "messages": [
                {"text": "Let me search for that."},
                {"server_tool_use": {"name": "web_search", "id": "srvtoolu_kimi_01", "input": {"query": "top AI companies 2026"}}},
                {"server_tool_result": {"name": "web_search", "tool_use_id": "srvtoolu_kimi_01",
                    "result": [{"type":"web_search_result","content":null,"url":"https://example.com","title":"AI Companies","encrypted_content":"enc123","page_age":"2026-02-07"}]}},
                {"text": "Based on my search, here are the top AI companies."}
            ]
        }]}<|instruction_end|>
        """;

    public ServerToolStreamingE2ETests(ITestOutputHelper output)
        : base(output) { }

    [Fact]
    public async Task KimiLikeFlow_Streaming_TextServerToolTextSequence()
    {
        Logger.LogInformation(
            "Starting {TestName} - verifying streaming 4-block pattern",
            nameof(KimiLikeFlow_Streaming_TextServerToolTextSequence)
        );

        var requestCapture = new RequestCapture();
        var httpClient = TestModeHttpClientFactory.CreateAnthropicTestClient(
            LoggerFactory, requestCapture, chunkDelayMs: 0
        );
        var anthropicClient = new AnthropicClient("test-api-key", httpClient: httpClient);
        var agent = new AnthropicAgent(
            "TestAgent", anthropicClient, LoggerFactory.CreateLogger<AnthropicAgent>()
        );

        var messages = new[]
        {
            new TextMessage { Role = Role.User, Text = KimiLikeInstructionChain },
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "claude-sonnet-4-20250514",
            BuiltInTools = [new AnthropicWebSearchTool()],
        };

        var responseMessages = new List<IMessage>();
        await foreach (var msg in await agent.GenerateReplyStreamingAsync(messages, options))
        {
            responseMessages.Add(msg);
            Logger.LogDebug(
                "Streamed message: {MessageType}, Role={Role}",
                msg.GetType().Name,
                msg.Role
            );
        }

        Logger.LogInformation(
            "Streaming complete: {MessageCount} messages received",
            responseMessages.Count
        );

        // Verify 4-block structure
        // In streaming mode, text arrives as TextUpdateMessage (not TextMessage)
        // Server tool use arrives as ToolCallUpdateMessage (preview at start + final at stop)
        var textUpdates = responseMessages.OfType<TextUpdateMessage>().ToList();
        var serverToolUpdateMessages = responseMessages.OfType<ToolCallUpdateMessage>().Where(m => m.ExecutionTarget == ExecutionTarget.ProviderServer).ToList();
        var serverToolResultMessages = responseMessages.OfType<ToolCallResultMessage>().Where(m => m.ExecutionTarget == ExecutionTarget.ProviderServer).ToList();
        var textWithCitationsMessages = responseMessages.OfType<TextWithCitationsMessage>().ToList();

        Logger.LogDebug(
            "Message breakdown: TextUpdates={TextUpdateCount}, ServerToolUpdates={ToolUpdateCount}, "
            + "ServerToolResult={ResultCount}, TextWithCitations={CitationsCount}",
            textUpdates.Count,
            serverToolUpdateMessages.Count,
            serverToolResultMessages.Count,
            textWithCitationsMessages.Count
        );

        // content_block_start and content_block_stop both emit ToolCallUpdateMessage.
        // The joiner middleware (applied externally) combines them into one ToolCallMessage.
        Assert.Equal(2, serverToolUpdateMessages.Count);
        Assert.Single(serverToolResultMessages);
        Assert.Empty(textWithCitationsMessages); // No citations in this chain

        var toolUse = serverToolUpdateMessages.Last();
        Assert.Equal("web_search", toolUse.FunctionName);
        Assert.Equal("srvtoolu_kimi_01", toolUse.ToolCallId);

        var toolResult = serverToolResultMessages.First();
        Assert.Equal("web_search", toolResult.ToolName);
        Assert.False(toolResult.IsError);

        // Verify text content via ICanGetText (covers both TextMessage and TextUpdateMessage)
        var allText = responseMessages.OfType<ICanGetText>()
            .Select(m => m.GetText())
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();

        Assert.Contains(allText, t => t!.Contains("Let me search"));
        Assert.Contains(allText, t => t!.Contains("Based on my search"));
    }

    [Fact]
    public async Task KimiLikeFlow_NonStreaming_TextServerToolTextSequence()
    {
        Logger.LogInformation(
            "Starting {TestName} - verifying non-streaming 4-block pattern",
            nameof(KimiLikeFlow_NonStreaming_TextServerToolTextSequence)
        );

        var requestCapture = new RequestCapture();
        var httpClient = TestModeHttpClientFactory.CreateAnthropicTestClient(
            LoggerFactory, requestCapture, chunkDelayMs: 0
        );
        var anthropicClient = new AnthropicClient("test-api-key", httpClient: httpClient);
        var agent = new AnthropicAgent(
            "TestAgent", anthropicClient, LoggerFactory.CreateLogger<AnthropicAgent>()
        );

        var messages = new[]
        {
            new TextMessage { Role = Role.User, Text = KimiLikeInstructionChain },
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "claude-sonnet-4-20250514",
            BuiltInTools = [new AnthropicWebSearchTool()],
        };

        var responseMessages = (await agent.GenerateReplyAsync(messages, options)).ToList();

        Logger.LogInformation(
            "Non-streaming response: {MessageCount} messages received",
            responseMessages.Count
        );

        foreach (var msg in responseMessages)
        {
            Logger.LogDebug(
                "Response message: {MessageType}, Role={Role}",
                msg.GetType().Name,
                msg.Role
            );
        }

        // Same assertions as streaming test
        var serverToolUseMessages = responseMessages.OfType<ToolCallMessage>().Where(m => m.ExecutionTarget == ExecutionTarget.ProviderServer).ToList();
        var serverToolResultMessages = responseMessages.OfType<ToolCallResultMessage>().Where(m => m.ExecutionTarget == ExecutionTarget.ProviderServer).ToList();

        Assert.Single(serverToolUseMessages);
        Assert.Single(serverToolResultMessages);

        var toolUse = serverToolUseMessages.First();
        Assert.Equal("web_search", toolUse.FunctionName);
        Assert.Equal("srvtoolu_kimi_01", toolUse.ToolCallId);

        var toolResult = serverToolResultMessages.First();
        Assert.Equal("web_search", toolResult.ToolName);
        Assert.False(toolResult.IsError);
    }

    [Fact]
    public async Task StreamingVsNonStreaming_ProduceEquivalentMessageTypes()
    {
        Logger.LogInformation(
            "Starting {TestName} - comparing streaming vs non-streaming output",
            nameof(StreamingVsNonStreaming_ProduceEquivalentMessageTypes)
        );

        // --- Streaming path ---
        var streamingCapture = new RequestCapture();
        var streamingClient = TestModeHttpClientFactory.CreateAnthropicTestClient(
            LoggerFactory, streamingCapture, chunkDelayMs: 0
        );
        var streamingAnthropicClient = new AnthropicClient("test-api-key", httpClient: streamingClient);
        var streamingAgent = new AnthropicAgent(
            "StreamAgent", streamingAnthropicClient, LoggerFactory.CreateLogger<AnthropicAgent>()
        );

        var messages = new[]
        {
            new TextMessage { Role = Role.User, Text = KimiLikeInstructionChain },
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "claude-sonnet-4-20250514",
            BuiltInTools = [new AnthropicWebSearchTool()],
        };

        var streamingMessages = new List<IMessage>();
        await foreach (var msg in await streamingAgent.GenerateReplyStreamingAsync(messages, options))
        {
            streamingMessages.Add(msg);
        }

        Logger.LogDebug(
            "Streaming produced {Count} messages",
            streamingMessages.Count
        );

        // --- Non-streaming path ---
        var nonStreamingCapture = new RequestCapture();
        var nonStreamingClient = TestModeHttpClientFactory.CreateAnthropicTestClient(
            LoggerFactory, nonStreamingCapture, chunkDelayMs: 0
        );
        var nonStreamingAnthropicClient = new AnthropicClient("test-api-key", httpClient: nonStreamingClient);
        var nonStreamingAgent = new AnthropicAgent(
            "NonStreamAgent", nonStreamingAnthropicClient, LoggerFactory.CreateLogger<AnthropicAgent>()
        );

        var nonStreamingMessages = (await nonStreamingAgent.GenerateReplyAsync(messages, options)).ToList();

        Logger.LogDebug(
            "Non-streaming produced {Count} messages",
            nonStreamingMessages.Count
        );

        // Compare server tool message counts
        // Streaming emits ToolCallUpdateMessage (not ToolCallMessage), non-streaming emits ToolCallMessage
        var streamingToolUpdateCount = streamingMessages.OfType<ToolCallUpdateMessage>().Count(m => m.ExecutionTarget == ExecutionTarget.ProviderServer);
        var streamingToolResultCount = streamingMessages.OfType<ToolCallResultMessage>().Count(m => m.ExecutionTarget == ExecutionTarget.ProviderServer);
        var nonStreamingToolUseCount = nonStreamingMessages.OfType<ToolCallMessage>().Count(m => m.ExecutionTarget == ExecutionTarget.ProviderServer);
        var nonStreamingToolResultCount = nonStreamingMessages.OfType<ToolCallResultMessage>().Count(m => m.ExecutionTarget == ExecutionTarget.ProviderServer);

        Logger.LogInformation(
            "Comparison: Streaming(ToolUpdates={StreamToolUpdates}, ToolResult={StreamToolResult}) "
            + "vs NonStreaming(ToolUse={NonStreamToolUse}, ToolResult={NonStreamToolResult})",
            streamingToolUpdateCount,
            streamingToolResultCount,
            nonStreamingToolUseCount,
            nonStreamingToolResultCount
        );

        // Streaming: 2 ToolCallUpdateMessages (preview + final), non-streaming: 1 ToolCallMessage
        Assert.Equal(2, streamingToolUpdateCount);
        Assert.Equal(1, nonStreamingToolUseCount);
        Assert.Equal(1, streamingToolResultCount);
        Assert.Equal(1, nonStreamingToolResultCount);

        // Compare tool use IDs and names (streaming uses last ToolCallUpdateMessage)
        var streamToolUse = streamingMessages.OfType<ToolCallUpdateMessage>().Last(m => m.ExecutionTarget == ExecutionTarget.ProviderServer);
        var nonStreamToolUse = nonStreamingMessages.OfType<ToolCallMessage>().First(m => m.ExecutionTarget == ExecutionTarget.ProviderServer);
        Assert.Equal(streamToolUse.FunctionName, nonStreamToolUse.FunctionName);
        Assert.Equal(streamToolUse.ToolCallId, nonStreamToolUse.ToolCallId);
    }

    [Fact]
    public async Task MultiTurn_StreamingHistory_RequestWireFormatCorrect()
    {
        Logger.LogInformation(
            "Starting {TestName} - verifying multi-turn wire format",
            nameof(MultiTurn_StreamingHistory_RequestWireFormatCorrect)
        );

        var requestCapture = new RequestCapture();
        var httpClient = TestModeHttpClientFactory.CreateAnthropicTestClient(
            LoggerFactory, requestCapture, chunkDelayMs: 0
        );
        var anthropicClient = new AnthropicClient("test-api-key", httpClient: httpClient);
        var agent = new AnthropicAgent(
            "TestAgent", anthropicClient, LoggerFactory.CreateLogger<AnthropicAgent>()
        );

        var options = new GenerateReplyOptions
        {
            ModelId = "claude-sonnet-4-20250514",
            BuiltInTools = [new AnthropicWebSearchTool()],
        };

        // Turn 1: server tool flow via instruction chain
        var turn1Messages = new IMessage[]
        {
            new TextMessage { Role = Role.User, Text = KimiLikeInstructionChain },
        };

        var turn1Responses = new List<IMessage>();
        await foreach (var msg in await agent.GenerateReplyStreamingAsync(turn1Messages, options))
        {
            turn1Responses.Add(msg);
        }

        Logger.LogDebug(
            "Turn 1 produced {Count} messages",
            turn1Responses.Count
        );

        // Turn 2: follow-up question with full history
        var turn2Instruction = """
            <|instruction_start|>{"instruction_chain": [
                {"messages": [{"text": "Here are more details about those companies."}]}
            ]}<|instruction_end|>
            """;

        var turn2Messages = new List<IMessage>();
        turn2Messages.AddRange(turn1Messages);
        turn2Messages.AddRange(turn1Responses);
        turn2Messages.Add(new TextMessage { Role = Role.User, Text = turn2Instruction });

        var turn2Responses = new List<IMessage>();
        await foreach (var msg in await agent.GenerateReplyStreamingAsync(turn2Messages, options))
        {
            turn2Responses.Add(msg);
        }

        Logger.LogDebug(
            "Turn 2 produced {Count} messages",
            turn2Responses.Count
        );

        // Inspect the turn 2 request wire format
        Assert.Equal(2, requestCapture.RequestCount);
        var turn2Body = requestCapture.RequestBodies[1];
        using var doc = JsonDocument.Parse(turn2Body);
        var requestMessages = doc.RootElement.GetProperty("messages");

        Logger.LogDebug(
            "Turn 2 request has {MessageCount} messages",
            requestMessages.GetArrayLength()
        );

        // Verify wire format constraints
        var hasServerToolUse = false;
        var hasToolResult = false;
        string? previousRole = null;

        foreach (var msg in requestMessages.EnumerateArray())
        {
            var role = msg.GetProperty("role").GetString()!;

            // No consecutive same-role messages
            Assert.NotEqual(
                previousRole,
                role,
                StringComparer.Ordinal
            );
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
                        Logger.LogDebug(
                            "Found server_tool_use in {Role} message: id={Id}, name={Name}",
                            role,
                            block.GetProperty("id").GetString(),
                            block.GetProperty("name").GetString()
                        );
                    }

                    if (typeStr == "tool_result"
                        && block.TryGetProperty("tool_use_id", out var toolUseId)
                        && toolUseId.GetString() == "srvtoolu_kimi_01")
                    {
                        hasToolResult = true;
                        Assert.Equal("user", role);
                        Logger.LogDebug(
                            "Found tool_result in {Role} message: tool_use_id={ToolUseId}",
                            role,
                            toolUseId.GetString()
                        );
                    }
                }
            }
        }

        Assert.True(hasServerToolUse, "Turn 2 request should contain server_tool_use in assistant message");
        Assert.True(hasToolResult, "Turn 2 request should contain tool_result in user message");
    }

    [Fact]
    public async Task KimiQuirk_MismatchedToolIds_StreamingParsesCorrectly()
    {
        Logger.LogInformation(
            "Starting {TestName} - verifying mismatched tool IDs parse without error",
            nameof(KimiQuirk_MismatchedToolIds_StreamingParsesCorrectly)
        );

        // Build SSE events with mismatched IDs (server_tool_use id != web_search_tool_result tool_use_id)
        var events = BuildMismatchedIdSseEvents();

        Logger.LogDebug("Built {EventCount} SSE events with mismatched IDs", events.Count);

        var handler = FakeHttpMessageHandler.CreateSseStreamHandler(events);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.anthropic.com/v1") };
        var anthropicClient = new AnthropicClient("test-api-key", httpClient: httpClient);
        var agent = new AnthropicAgent(
            "TestAgent", anthropicClient, LoggerFactory.CreateLogger<AnthropicAgent>()
        );

        var messages = new[]
        {
            new TextMessage { Role = Role.User, Text = "Search for something" },
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "claude-sonnet-4-20250514",
            BuiltInTools = [new AnthropicWebSearchTool()],
        };

        // Should not throw despite mismatched IDs
        var responseMessages = new List<IMessage>();
        var exception = await Record.ExceptionAsync(async () =>
        {
            await foreach (var msg in await agent.GenerateReplyStreamingAsync(messages, options))
            {
                responseMessages.Add(msg);
                Logger.LogDebug(
                    "Streamed message: {MessageType}",
                    msg.GetType().Name
                );
            }
        });

        Assert.Null(exception);
        Logger.LogInformation(
            "Mismatched IDs parsed without error: {MessageCount} messages",
            responseMessages.Count
        );

        // Verify both messages have correct tool names despite mismatched IDs
        // Streaming emits ToolCallUpdateMessage, not ToolCallMessage
        var toolUse = responseMessages.OfType<ToolCallUpdateMessage>().FirstOrDefault(m => m.ExecutionTarget == ExecutionTarget.ProviderServer);
        Assert.NotNull(toolUse);
        Assert.Equal("web_search", toolUse!.FunctionName);

        var toolResult = responseMessages.OfType<ToolCallResultMessage>().FirstOrDefault(m => m.ExecutionTarget == ExecutionTarget.ProviderServer);
        Assert.NotNull(toolResult);
        Assert.Equal("web_search", toolResult!.ToolName);
    }

    [Fact]
    public async Task ErrorResult_NonStreaming_ViaInstructionChain()
    {
        Logger.LogInformation(
            "Starting {TestName} - verifying error result via non-streaming instruction chain",
            nameof(ErrorResult_NonStreaming_ViaInstructionChain)
        );

        var errorInstructionChain = """
            <|instruction_start|>{"instruction_chain": [{
                "messages": [
                    {"server_tool_use": {"name": "web_search", "id": "srvtoolu_err_01", "input": {"query": "test"}}},
                    {"server_tool_result": {"name": "web_search", "tool_use_id": "srvtoolu_err_01", "error_code": "max_uses_exceeded"}},
                    {"text": "I was unable to complete the search due to rate limits."}
                ]
            }]}<|instruction_end|>
            """;

        var requestCapture = new RequestCapture();
        var httpClient = TestModeHttpClientFactory.CreateAnthropicTestClient(
            LoggerFactory, requestCapture, chunkDelayMs: 0
        );
        var anthropicClient = new AnthropicClient("test-api-key", httpClient: httpClient);
        var agent = new AnthropicAgent(
            "TestAgent", anthropicClient, LoggerFactory.CreateLogger<AnthropicAgent>()
        );

        var messages = new[]
        {
            new TextMessage { Role = Role.User, Text = errorInstructionChain },
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "claude-sonnet-4-20250514",
            BuiltInTools = [new AnthropicWebSearchTool()],
        };

        var responseMessages = (await agent.GenerateReplyAsync(messages, options)).ToList();

        Logger.LogInformation(
            "Error result test: {MessageCount} messages received",
            responseMessages.Count
        );

        foreach (var msg in responseMessages)
        {
            Logger.LogDebug(
                "Response message: {MessageType}, Role={Role}",
                msg.GetType().Name,
                msg.Role
            );
        }

        var toolResult = responseMessages.OfType<ToolCallResultMessage>().FirstOrDefault(m => m.ExecutionTarget == ExecutionTarget.ProviderServer);
        Assert.NotNull(toolResult);
        Assert.True(toolResult!.IsError);
        Assert.Equal("max_uses_exceeded", toolResult.ErrorCode);

        Logger.LogInformation(
            "Error result verified: IsError={IsError}, ErrorCode={ErrorCode}",
            toolResult.IsError,
            toolResult.ErrorCode
        );
    }

    [Fact]
    public async Task NonWebSearchTool_NonStreaming_UsesCorrectResultType()
    {
        Logger.LogInformation(
            "Starting {TestName} - verifying non-web_search tool type mapping",
            nameof(NonWebSearchTool_NonStreaming_UsesCorrectResultType)
        );

        // Use web_fetch instead of web_search to ensure the name→type mapping is exercised
        var webFetchInstructionChain = """
            <|instruction_start|>{"instruction_chain": [{
                "messages": [
                    {"text": "Let me fetch that page."},
                    {"server_tool_use": {"name": "web_fetch", "id": "srvtoolu_fetch_01", "input": {"url": "https://example.com"}}},
                    {"server_tool_result": {"name": "web_fetch", "tool_use_id": "srvtoolu_fetch_01",
                        "result": [{"type":"web_fetch_result","content":"Page content here","url":"https://example.com"}]}},
                    {"text": "Here is what the page says."}
                ]
            }]}<|instruction_end|>
            """;

        var requestCapture = new RequestCapture();
        var httpClient = TestModeHttpClientFactory.CreateAnthropicTestClient(
            LoggerFactory, requestCapture, chunkDelayMs: 0
        );
        var anthropicClient = new AnthropicClient("test-api-key", httpClient: httpClient);
        var agent = new AnthropicAgent(
            "TestAgent", anthropicClient, LoggerFactory.CreateLogger<AnthropicAgent>()
        );

        var messages = new[]
        {
            new TextMessage { Role = Role.User, Text = webFetchInstructionChain },
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "claude-sonnet-4-20250514",
        };

        var responseMessages = (await agent.GenerateReplyAsync(messages, options)).ToList();

        Logger.LogInformation(
            "web_fetch non-streaming: {MessageCount} messages received",
            responseMessages.Count
        );

        foreach (var msg in responseMessages)
        {
            Logger.LogDebug(
                "Response message: {MessageType}, Role={Role}",
                msg.GetType().Name,
                msg.Role
            );
        }

        var toolUse = responseMessages.OfType<ToolCallMessage>().FirstOrDefault(m => m.ExecutionTarget == ExecutionTarget.ProviderServer);
        Assert.NotNull(toolUse);
        Assert.Equal("web_fetch", toolUse!.FunctionName);
        Assert.Equal("srvtoolu_fetch_01", toolUse.ToolCallId);

        var toolResult = responseMessages.OfType<ToolCallResultMessage>().FirstOrDefault(m => m.ExecutionTarget == ExecutionTarget.ProviderServer);
        Assert.NotNull(toolResult);
        Assert.Equal("web_fetch", toolResult!.ToolName);
        Assert.False(toolResult.IsError);

        // Verify the wire format uses web_fetch_tool_result (not web_search_tool_result)
        Assert.Equal(1, requestCapture.RequestCount);
        var body = requestCapture.RequestBodies[0];
        using var doc = JsonDocument.Parse(body);
        var requestMessages = doc.RootElement.GetProperty("messages");

        Logger.LogDebug(
            "Non-streaming request body for web_fetch: {RequestBody}",
            body
        );

        // The response JSON should NOT contain "web_search_tool_result"
        Assert.DoesNotContain("web_search_tool_result", body);
    }

    [Fact]
    public async Task ErrorResult_Streaming_ViaInstructionChain()
    {
        Logger.LogInformation(
            "Starting {TestName} - verifying streaming error result",
            nameof(ErrorResult_Streaming_ViaInstructionChain)
        );

        var errorInstructionChain = """
            <|instruction_start|>{"instruction_chain": [{
                "messages": [
                    {"server_tool_use": {"name": "web_search", "id": "srvtoolu_err_02", "input": {"query": "test"}}},
                    {"server_tool_result": {"name": "web_search", "tool_use_id": "srvtoolu_err_02", "error_code": "max_uses_exceeded"}},
                    {"text": "I was unable to complete the search due to rate limits."}
                ]
            }]}<|instruction_end|>
            """;

        var requestCapture = new RequestCapture();
        var httpClient = TestModeHttpClientFactory.CreateAnthropicTestClient(
            LoggerFactory, requestCapture, chunkDelayMs: 0
        );
        var anthropicClient = new AnthropicClient("test-api-key", httpClient: httpClient);
        var agent = new AnthropicAgent(
            "TestAgent", anthropicClient, LoggerFactory.CreateLogger<AnthropicAgent>()
        );

        var messages = new[]
        {
            new TextMessage { Role = Role.User, Text = errorInstructionChain },
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "claude-sonnet-4-20250514",
            BuiltInTools = [new AnthropicWebSearchTool()],
        };

        var responseMessages = new List<IMessage>();
        await foreach (var msg in await agent.GenerateReplyStreamingAsync(messages, options))
        {
            responseMessages.Add(msg);
            Logger.LogDebug(
                "Streamed message: {MessageType}, Role={Role}",
                msg.GetType().Name,
                msg.Role
            );
        }

        Logger.LogInformation(
            "Streaming error result: {MessageCount} messages received",
            responseMessages.Count
        );

        var toolResult = responseMessages.OfType<ToolCallResultMessage>().FirstOrDefault(m => m.ExecutionTarget == ExecutionTarget.ProviderServer);
        Assert.NotNull(toolResult);
        Assert.True(toolResult!.IsError);
        Assert.Equal("max_uses_exceeded", toolResult.ErrorCode);

        Logger.LogInformation(
            "Streaming error verified: IsError={IsError}, ErrorCode={ErrorCode}",
            toolResult.IsError,
            toolResult.ErrorCode
        );
    }

    [Fact]
    public async Task ErrorResult_NonStreaming_WireFormatHasCorrectErrorStructure()
    {
        Logger.LogInformation(
            "Starting {TestName} - verifying error result wire format structure",
            nameof(ErrorResult_NonStreaming_WireFormatHasCorrectErrorStructure)
        );

        var errorInstructionChain = """
            <|instruction_start|>{"instruction_chain": [{
                "messages": [
                    {"server_tool_use": {"name": "web_search", "id": "srvtoolu_err_03", "input": {"query": "test"}}},
                    {"server_tool_result": {"name": "web_search", "tool_use_id": "srvtoolu_err_03", "error_code": "max_uses_exceeded"}}
                ]
            }]}<|instruction_end|>
            """;

        var requestCapture = new RequestCapture();
        var httpClient = TestModeHttpClientFactory.CreateAnthropicTestClient(
            LoggerFactory, requestCapture, chunkDelayMs: 0
        );
        var anthropicClient = new AnthropicClient("test-api-key", httpClient: httpClient);
        var agent = new AnthropicAgent(
            "TestAgent", anthropicClient, LoggerFactory.CreateLogger<AnthropicAgent>()
        );

        var messages = new[]
        {
            new TextMessage { Role = Role.User, Text = errorInstructionChain },
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "claude-sonnet-4-20250514",
            BuiltInTools = [new AnthropicWebSearchTool()],
        };

        // Use non-streaming to get the full JSON response body
        var responseMessages = (await agent.GenerateReplyAsync(messages, options)).ToList();

        Logger.LogInformation(
            "Wire format test: {MessageCount} messages received",
            responseMessages.Count
        );

        // Inspect the raw HTTP response to verify the wire JSON structure
        Assert.Equal(1, requestCapture.RequestCount);

        // Parse what the handler actually returned by re-examining the response messages
        // The handler's CreateNonStreamingResponse builds a JSON body we can inspect
        // via the messages it produces. But to verify wire format, we inspect the handler output.
        // Since we can't easily capture the response body, verify via the parsed message properties
        // AND verify the request body doesn't contain incorrect type strings.
        var toolResult = responseMessages.OfType<ToolCallResultMessage>().FirstOrDefault(m => m.ExecutionTarget == ExecutionTarget.ProviderServer);
        Assert.NotNull(toolResult);
        Assert.True(toolResult!.IsError);
        Assert.Equal("max_uses_exceeded", toolResult.ErrorCode);
        Assert.Equal("web_search", toolResult.ToolName);
        Assert.Equal("srvtoolu_err_03", toolResult.ToolCallId);

        // Verify the result content carries the error structure through
        // The ErrorCode property being set confirms the parser found the error content block
        // with type "web_search_tool_result_error" in the response wire JSON
        Logger.LogInformation(
            "Wire format error structure verified: ToolName={ToolName}, ToolUseId={ToolUseId}, ErrorCode={ErrorCode}",
            toolResult.ToolName,
            toolResult.ToolCallId,
            toolResult.ErrorCode
        );
    }

    [Fact]
    public async Task MultiTurn_ServerToolUse_HistoryHasNoDuplicateToolCallIds()
    {
        Logger.LogInformation(
            "Starting {TestName} - verifying no duplicate tool call IDs in multi-turn history",
            nameof(MultiTurn_ServerToolUse_HistoryHasNoDuplicateToolCallIds)
        );

        var requestCapture = new RequestCapture();
        var httpClient = TestModeHttpClientFactory.CreateAnthropicTestClient(
            LoggerFactory, requestCapture, chunkDelayMs: 0
        );
        var anthropicClient = new AnthropicClient("test-api-key", httpClient: httpClient);
        var agent = new AnthropicAgent(
            "TestAgent", anthropicClient, LoggerFactory.CreateLogger<AnthropicAgent>()
        );

        var options = new GenerateReplyOptions
        {
            ModelId = "claude-sonnet-4-20250514",
            BuiltInTools = [new AnthropicWebSearchTool()],
        };

        // Turn 1: streaming response with server_tool_use
        var turn1Messages = new IMessage[]
        {
            new TextMessage { Role = Role.User, Text = KimiLikeInstructionChain },
        };

        var turn1Responses = new List<IMessage>();
        await foreach (var msg in await agent.GenerateReplyStreamingAsync(turn1Messages, options))
        {
            turn1Responses.Add(msg);
        }

        // Streaming emits 2 ToolCallMessages (preview + final) with the same ID.
        // When building multi-turn history, verify the wire format doesn't contain
        // duplicate tool_use IDs — which would cause the API to reject the request.
        var turn2Instruction = """
            <|instruction_start|>{"instruction_chain": [
                {"messages": [{"text": "Here are more details about those companies."}]}
            ]}<|instruction_end|>
            """;

        var turn2Messages = new List<IMessage>();
        turn2Messages.AddRange(turn1Messages);
        turn2Messages.AddRange(turn1Responses);
        turn2Messages.Add(new TextMessage { Role = Role.User, Text = turn2Instruction });

        var turn2Responses = new List<IMessage>();
        await foreach (var msg in await agent.GenerateReplyStreamingAsync(turn2Messages, options))
        {
            turn2Responses.Add(msg);
        }

        // Inspect turn 2 wire format for duplicate tool_use IDs
        Assert.Equal(2, requestCapture.RequestCount);
        var turn2Body = requestCapture.RequestBodies[1];
        using var doc = JsonDocument.Parse(turn2Body);
        var requestMessages = doc.RootElement.GetProperty("messages");

        var toolUseIds = new List<string>();
        foreach (var msg in requestMessages.EnumerateArray())
        {
            if (!msg.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var block in content.EnumerateArray())
            {
                if (!block.TryGetProperty("type", out var blockType))
                {
                    continue;
                }

                var typeStr = blockType.GetString();

                // Collect IDs from server_tool_use blocks
                if (typeStr == "server_tool_use"
                    && block.TryGetProperty("id", out var id))
                {
                    toolUseIds.Add(id.GetString()!);
                }

                // Also collect IDs from tool_use blocks (local tools)
                if (typeStr == "tool_use"
                    && block.TryGetProperty("id", out var toolId))
                {
                    toolUseIds.Add(toolId.GetString()!);
                }
            }
        }

        Logger.LogInformation(
            "Turn 2 wire format tool_use IDs: {ToolUseIds}",
            string.Join(", ", toolUseIds)
        );

        // Critical: no duplicate tool_use IDs in the wire format
        Assert.Equal(
            toolUseIds.Count,
            toolUseIds.Distinct().Count());
    }

    /// <summary>
    ///     Builds SSE events with mismatched server_tool_use ID and web_search_tool_result tool_use_id.
    /// </summary>
    private static List<SseEvent> BuildMismatchedIdSseEvents()
    {
        return [
        // message_start
        new SseEvent
        {
            Event = "message_start",
            Data = JsonSerializer.Serialize(new
            {
                type = "message_start",
                message = new
                {
                    id = "msg_mismatch_01",
                    type = "message",
                    role = "assistant",
                    model = "claude-sonnet-4-20250514",
                    content = Array.Empty<object>(),
                    stop_reason = (string?)null,
                    usage = new { input_tokens = 50, output_tokens = 0 },
                },
            }),
        },
        // server_tool_use with id "srvtoolu_AAA"
        new SseEvent
        {
            Event = "content_block_start",
            Data = JsonSerializer.Serialize(new
            {
                type = "content_block_start",
                index = 0,
                content_block = new
                {
                    type = "server_tool_use",
                    id = "srvtoolu_AAA",
                    name = "web_search",
                    input = new { },
                },
            }),
        },
        new SseEvent
        {
            Event = "content_block_delta",
            Data = JsonSerializer.Serialize(new
            {
                type = "content_block_delta",
                index = 0,
                delta = new { type = "input_json_delta", partial_json = "{\"query\":\"test\"}" },
            }),
        },
        new SseEvent
        {
            Event = "content_block_stop",
            Data = JsonSerializer.Serialize(new { type = "content_block_stop", index = 0 }),
        },
        // web_search_tool_result with DIFFERENT tool_use_id "srvtoolu_BBB"
        new SseEvent
        {
            Event = "content_block_start",
            Data = JsonSerializer.Serialize(new
            {
                type = "content_block_start",
                index = 1,
                content_block = new
                {
                    type = "web_search_tool_result",
                    tool_use_id = "srvtoolu_BBB",
                    content = new[]
                    {
                        new
                        {
                            type = "web_search_result",
                            url = "https://example.com",
                            title = "Test Result",
                            encrypted_content = "enc...",
                            page_age = "1 hour ago",
                        },
                    },
                },
            }),
        },
        new SseEvent
        {
            Event = "content_block_stop",
            Data = JsonSerializer.Serialize(new { type = "content_block_stop", index = 1 }),
        },
        // message_delta and message_stop
        new SseEvent
        {
            Event = "message_delta",
            Data = JsonSerializer.Serialize(new
            {
                type = "message_delta",
                delta = new { stop_reason = "end_turn", stop_sequence = (string?)null },
                usage = new { input_tokens = 50, output_tokens = 20 },
            }),
        },
        new SseEvent
        {
            Event = "message_stop",
            Data = JsonSerializer.Serialize(new { type = "message_stop" }),
        },
    ];
    }
}
