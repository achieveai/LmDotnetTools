namespace AchieveAi.LmDotnetTools.AnthropicProvider.Tests.Models;

/// <summary>
///     Tests that our Anthropic stream parser correctly extracts all important
///     information from real Anthropic API SSE responses captured from production.
///     Fixture files live under TestData/ and are copied to the output directory at build.
/// </summary>
public class RealAnthropicResponseParsingTests
{
    /// <summary>
    ///     Result of parsing an SSE file through the stream parser.
    ///     Contains both the per-event streamed messages and the joined final messages.
    /// </summary>
    private record ParseResult(
        List<IMessage> StreamedMessages,
        List<IMessage> JoinedMessages);

    /// <summary>
    ///     Parses an SSE file through the AnthropicStreamParser, returning both the
    ///     per-event streamed messages (including updates) and the joined final messages.
    /// </summary>
    private static ParseResult ParseSseFile(string path)
    {
        var parser = new AnthropicStreamParser();
        var streamed = new List<IMessage>();
        var lines = File.ReadAllLines(path);

        string? currentEvent = null;

        foreach (var line in lines)
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                currentEvent = line["event: ".Length..].Trim();
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal) && currentEvent != null)
            {
                var data = line["data: ".Length..].Trim();
                streamed.AddRange(parser.ProcessEvent(currentEvent, data));
                currentEvent = null;
            }
            else if (string.IsNullOrWhiteSpace(line))
            {
                currentEvent = null;
            }
        }

        return new ParseResult(streamed, parser.GetAllMessages());
    }

    private static string GetTestDataPath(string fileName)
    {
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", fileName);
    }

    private static string ToolCallFixture => GetTestDataPath("sample-response-anthropic.sse.txt");
    private static string WebSearchFixture => GetTestDataPath("sample-response-anthropic-websearch.sse.txt");

    // ──────────────────────────────────────────────────────────────────────
    // Tool-call response (thinking + text + 3 parallel tool_use blocks)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void ToolCallResponse_StreamedAndJoined_ContainThinkingContent()
    {
        var result = ParseSseFile(ToolCallFixture);

        // Streamed: should have reasoning updates
        var reasoningUpdates = result.StreamedMessages.OfType<ReasoningUpdateMessage>().ToList();
        Assert.NotEmpty(reasoningUpdates);

        // Joined: should have both plain and encrypted reasoning
        var reasoningMessages = result.JoinedMessages.OfType<ReasoningMessage>().ToList();
        Assert.True(reasoningMessages.Count >= 2);

        var plain = reasoningMessages.First(r => r.Visibility == ReasoningVisibility.Plain);
        Assert.Contains("Alport syndrome", plain.Reasoning);
        Assert.Contains("deafness", plain.Reasoning);
        Assert.Contains("nephritis", plain.Reasoning);
        Assert.Equal(Role.Assistant, plain.Role);

        var encrypted = reasoningMessages.First(r => r.Visibility == ReasoningVisibility.Encrypted);
        Assert.NotEmpty(encrypted.Reasoning);
    }

    [Fact]
    public void ToolCallResponse_StreamedAndJoined_ContainTextContent()
    {
        var result = ParseSseFile(ToolCallFixture);

        // Streamed: should have text update chunks
        Assert.NotEmpty(result.StreamedMessages.OfType<TextUpdateMessage>());

        // Joined: final TextMessage with complete text
        var textMessages = result.JoinedMessages.OfType<TextMessage>().ToList();
        Assert.NotEmpty(textMessages);
        var text = textMessages.First();
        Assert.Contains("deafness", text.Text);
        Assert.Contains("hemorrhagic nephritis", text.Text);
        Assert.Equal(Role.Assistant, text.Role);
    }

    [Fact]
    public void ToolCallResponse_StreamedAndJoined_ContainParallelToolCalls()
    {
        var result = ParseSseFile(ToolCallFixture);

        // Streamed: should have tool call update chunks
        Assert.NotEmpty(result.StreamedMessages.OfType<ToolsCallUpdateMessage>());

        // Joined: 3 separate ToolsCallMessages (one per tool_use content block)
        var toolsCallMessages = result.JoinedMessages.OfType<ToolsCallMessage>().ToList();
        Assert.Equal(3, toolsCallMessages.Count);

        var allToolCalls = toolsCallMessages.SelectMany(m => m.ToolCalls).ToList();

        // All should be llm_query_search with valid args
        foreach (var tc in allToolCalls)
        {
            Assert.Equal("mcp__books__llm_query_search", tc.FunctionName);
            Assert.NotNull(tc.FunctionArgs);
            Assert.NotEqual("{}", tc.FunctionArgs);
        }

        // Distinct IDs
        Assert.Equal(3, allToolCalls.Select(t => t.ToolCallId).ToHashSet().Count);

        // Verify search queries
        var args = allToolCalls.Select(tc => tc.FunctionArgs!).ToList();
        Assert.Contains("Alport", args[0]);
        Assert.Contains("type IV collagen", args[1]);
        Assert.Contains("basement membrane", args[2]);
    }

    [Fact]
    public void ToolCallResponse_StreamedAndJoined_CacheMetricsMatch()
    {
        var result = ParseSseFile(ToolCallFixture);

        var streamedUsage = result.StreamedMessages.OfType<UsageMessage>().First();
        var joinedUsage = result.JoinedMessages.OfType<UsageMessage>().First();

        foreach (var usage in new[] { streamedUsage, joinedUsage })
        {
            Assert.Equal(12055, usage.Usage.TotalCachedTokens);
            Assert.Equal(619, usage.Usage.GetExtraProperty<int>("cache_creation_input_tokens"));
        }
    }

    [Fact]
    public void ToolCallResponse_StreamedAndJoined_UsageValuesMatch()
    {
        var result = ParseSseFile(ToolCallFixture);

        var streamedUsage = result.StreamedMessages.OfType<UsageMessage>().First();
        var joinedUsage = result.JoinedMessages.OfType<UsageMessage>().First();

        foreach (var usage in new[] { streamedUsage, joinedUsage })
        {
            Assert.Equal(732, usage.Usage.CompletionTokens);
            Assert.Equal(9, usage.Usage.PromptTokens);
            Assert.Equal(741, usage.Usage.TotalTokens);
        }
    }

    [Fact]
    public void ToolCallResponse_JoinedMessageOrderIsCorrect()
    {
        var result = ParseSseFile(ToolCallFixture);
        var joined = result.JoinedMessages;

        var firstReasoning = joined.FindIndex(m => m is ReasoningMessage);
        var firstText = joined.FindIndex(m => m is TextMessage);
        var firstToolsCall = joined.FindIndex(m => m is ToolsCallMessage);
        var usageIdx = joined.FindIndex(m => m is UsageMessage);

        Assert.True(firstReasoning >= 0, "Should have a ReasoningMessage");
        Assert.True(firstText >= 0, "Should have a TextMessage");
        Assert.True(firstToolsCall >= 0, "Should have a ToolsCallMessage");
        Assert.True(usageIdx >= 0, "Should have a UsageMessage");

        Assert.True(firstReasoning < firstText, "Reasoning before text");
        Assert.True(firstText < firstToolsCall, "Text before tool calls");
        Assert.True(firstToolsCall < usageIdx, "Tool calls before usage");
    }

    // ──────────────────────────────────────────────────────────────────────
    // Web-search response (thinking + server_tool_use + web_search_tool_result
    //                      + text with citations + end_turn)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void WebSearchResponse_StreamedAndJoined_ContainThinkingContent()
    {
        var result = ParseSseFile(WebSearchFixture);

        // Streamed: reasoning updates
        Assert.NotEmpty(result.StreamedMessages.OfType<ReasoningUpdateMessage>());

        // Joined: at least one plain reasoning
        var plain = result.JoinedMessages.OfType<ReasoningMessage>()
            .Where(r => r.Visibility == ReasoningVisibility.Plain)
            .ToList();
        Assert.NotEmpty(plain);
        Assert.Contains("Alport", plain.First().Reasoning);
    }

    [Fact]
    public void WebSearchResponse_Joined_ExtractsServerToolCall()
    {
        var result = ParseSseFile(WebSearchFixture);

        // The server_tool_use block emits a ToolCallMessage with ExecutionTarget.ProviderServer
        var serverToolCalls = result.JoinedMessages.OfType<ToolCallMessage>()
            .Where(tc => tc.ExecutionTarget == ExecutionTarget.ProviderServer)
            .ToList();
        Assert.NotEmpty(serverToolCalls);

        var webSearchCall = serverToolCalls.First();
        Assert.Equal("web_search", webSearchCall.FunctionName);
        Assert.StartsWith("srvtoolu_", webSearchCall.ToolCallId);

        // The web_search query should be about Alport syndrome
        Assert.NotNull(webSearchCall.FunctionArgs);
        Assert.Contains("Alport", webSearchCall.FunctionArgs);
    }

    [Fact]
    public void WebSearchResponse_Joined_ExtractsServerToolResult()
    {
        var result = ParseSseFile(WebSearchFixture);

        // The web_search_tool_result block emits a ToolCallResultMessage
        var toolResults = result.JoinedMessages.OfType<ToolCallResultMessage>().ToList();
        Assert.NotEmpty(toolResults);

        var webResult = toolResults.First();
        Assert.Equal("web_search", webResult.ToolName);
        Assert.StartsWith("srvtoolu_", webResult.ToolCallId);
    }

    [Fact]
    public void WebSearchResponse_Joined_ExtractsTextWithCitations()
    {
        var result = ParseSseFile(WebSearchFixture);

        // Content blocks with citations[] emit TextWithCitationsMessage
        var citedTexts = result.JoinedMessages.OfType<TextWithCitationsMessage>().ToList();
        Assert.NotEmpty(citedTexts);

        // At least one should have non-empty citations
        var withCitations = citedTexts.Where(t => t.Citations is { Count: > 0 }).ToList();
        Assert.NotEmpty(withCitations);

        // Verify citation structure
        var firstCitation = withCitations.First().Citations!.First();
        Assert.Equal("web_search_result_location", firstCitation.Type);
        Assert.NotNull(firstCitation.Url);
        Assert.NotNull(firstCitation.Title);
        Assert.NotNull(firstCitation.CitedText);
    }

    [Fact]
    public void WebSearchResponse_StreamedAndJoined_CacheMetricsMatch()
    {
        var result = ParseSseFile(WebSearchFixture);

        var streamedUsage = result.StreamedMessages.OfType<UsageMessage>().First();
        var joinedUsage = result.JoinedMessages.OfType<UsageMessage>().First();

        foreach (var usage in new[] { streamedUsage, joinedUsage })
        {
            Assert.Equal(4353, usage.Usage.TotalCachedTokens);
            Assert.Equal(
                11769,
                usage.Usage.GetExtraProperty<int>("cache_creation_input_tokens"));
        }
    }

    [Fact]
    public void WebSearchResponse_StreamedAndJoined_UsageValuesMatch()
    {
        var result = ParseSseFile(WebSearchFixture);

        var streamedUsage = result.StreamedMessages.OfType<UsageMessage>().First();
        var joinedUsage = result.JoinedMessages.OfType<UsageMessage>().First();

        foreach (var usage in new[] { streamedUsage, joinedUsage })
        {
            Assert.Equal(854, usage.Usage.CompletionTokens);
            Assert.Equal(17, usage.Usage.PromptTokens);
            Assert.Equal(871, usage.Usage.TotalTokens);
        }
    }

    [Fact]
    public void WebSearchResponse_Joined_HasTextOutputForEndTurn()
    {
        var result = ParseSseFile(WebSearchFixture);

        // Unlike the tool-call response, web_search completes in one turn (end_turn)
        var textMessages = result.JoinedMessages.OfType<TextMessage>().ToList();
        var citedMessages = result.JoinedMessages.OfType<TextWithCitationsMessage>().ToList();
        Assert.True(
            textMessages.Count + citedMessages.Count > 0,
            "Should have text output for end_turn response");
    }

    [Fact]
    public void WebSearchResponse_JoinedMessageOrderIsCorrect()
    {
        var result = ParseSseFile(WebSearchFixture);
        var joined = result.JoinedMessages;

        // Expected order: thinking -> server_tool_use -> tool_result -> thinking -> text/citations -> usage
        var firstReasoning = joined.FindIndex(m => m is ReasoningMessage);
        var firstServerTool = joined.FindIndex(m =>
            m is ToolCallMessage tc && tc.ExecutionTarget == ExecutionTarget.ProviderServer);
        var firstToolResult = joined.FindIndex(m => m is ToolCallResultMessage);
        var firstTextOrCited = joined.FindIndex(m => m is TextMessage or TextWithCitationsMessage);
        var usageIdx = joined.FindIndex(m => m is UsageMessage);

        Assert.True(firstReasoning >= 0, "Should have a ReasoningMessage");
        Assert.True(firstServerTool >= 0, "Should have a server ToolCallMessage");
        Assert.True(firstToolResult >= 0, "Should have a ToolCallResultMessage");
        Assert.True(firstTextOrCited >= 0, "Should have text output");
        Assert.True(usageIdx >= 0, "Should have a UsageMessage");

        Assert.True(firstReasoning < firstServerTool, "Reasoning before server tool call");
        Assert.True(firstServerTool < firstToolResult, "Server tool call before result");
        Assert.True(firstToolResult < firstTextOrCited, "Tool result before text output");
        Assert.True(firstTextOrCited < usageIdx, "Text before usage");
    }

    [Fact]
    public void WebSearchResponse_Joined_ServerToolCallAndResultShareId()
    {
        var result = ParseSseFile(WebSearchFixture);

        var serverCall = result.JoinedMessages.OfType<ToolCallMessage>()
            .First(tc => tc.ExecutionTarget == ExecutionTarget.ProviderServer);
        var toolResult = result.JoinedMessages.OfType<ToolCallResultMessage>().First();

        // The tool_use_id in the result should match the server_tool_use id
        Assert.Equal(serverCall.ToolCallId, toolResult.ToolCallId);
    }

    [Fact]
    public void WebSearchResponse_Joined_HasExactlyOneServerToolCall()
    {
        var result = ParseSseFile(WebSearchFixture);

        // GetAllMessages() (joined) must contain exactly one ToolCallMessage per server_tool_use block.
        // If duplicates exist, multi-turn conversations will fail with "tool call id is duplicated".
        var serverToolCalls = result.JoinedMessages.OfType<ToolCallMessage>()
            .Where(tc => tc.ExecutionTarget == ExecutionTarget.ProviderServer)
            .ToList();
        Assert.Single(serverToolCalls);

        // No duplicate ToolCallIds across all tool-related messages
        var allToolCallIds = result.JoinedMessages.OfType<ToolCallMessage>()
            .Select(tc => tc.ToolCallId)
            .ToList();
        Assert.Equal(allToolCallIds.Count, allToolCallIds.Distinct().Count());
    }

    [Fact]
    public void WebSearchResponse_Streamed_HasPreviewAndFinalUpdateMessages()
    {
        var result = ParseSseFile(WebSearchFixture);

        // Both content_block_start and content_block_stop emit ToolCallUpdateMessage.
        // The joiner middleware combines them into a single ToolCallMessage.
        var streamedUpdates = result.StreamedMessages.OfType<ToolCallUpdateMessage>()
            .Where(tc => tc.ExecutionTarget == ExecutionTarget.ProviderServer)
            .ToList();
        Assert.Equal(2, streamedUpdates.Count);

        // First (preview at start) has null args — empty "{}" must not leak into the stream
        // because the joiner concatenates FunctionArgs strings and would produce "{}{"query":"..."}"
        Assert.Null(streamedUpdates[0].FunctionArgs);

        // Second (final at stop) has accumulated args with actual query
        Assert.Contains("Alport", streamedUpdates[1].FunctionArgs!);
    }
}
