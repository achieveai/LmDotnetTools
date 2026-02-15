using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmTestUtils.TestMode;

/// <summary>
///     HTTP message handler for test mode that simulates Anthropic API SSE streaming responses.
///     Processes instruction chains and generates mock Claude responses for testing.
/// </summary>
/// <remarks>
///     This is the Anthropic equivalent of <see cref="TestSseMessageHandler" />.
///     It intercepts POST requests to paths ending with /messages and generates Anthropic-format SSE responses.
/// </remarks>
public sealed class AnthropicTestSseMessageHandler : HttpMessageHandler
{
    private const int DefaultWordsPerChunk = 10;
    private const int DefaultChunkDelayMs = 500;
    private readonly IInstructionChainParser _chainParser;
    private readonly IConversationAnalyzer _conversationAnalyzer;
    private readonly ILogger<AnthropicTestSseMessageHandler> _logger;

    /// <summary>
    ///     Initializes a new instance for test mode with default services.
    /// </summary>
    public AnthropicTestSseMessageHandler()
        : this(
            LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<AnthropicTestSseMessageHandler>()
        ) { }

    /// <summary>
    ///     Initializes a new instance with dependency injection.
    /// </summary>
    public AnthropicTestSseMessageHandler(
        ILogger<AnthropicTestSseMessageHandler> logger,
        IInstructionChainParser? chainParser = null,
        IConversationAnalyzer? conversationAnalyzer = null
    )
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var fallbackFactory = LoggerFactory.Create(builder => builder.AddConsole());

        _chainParser =
            chainParser
            ?? new InstructionChainParser(
                fallbackFactory.CreateLogger<InstructionChainParser>()
            );

        _conversationAnalyzer =
            conversationAnalyzer
            ?? new ConversationAnalyzer(
                fallbackFactory.CreateLogger<ConversationAnalyzer>(),
                _chainParser
            );

        _logger.LogDebug(
            "AnthropicTestSseMessageHandler initialized with WordsPerChunk={WordsPerChunk}, ChunkDelayMs={ChunkDelayMs}",
            WordsPerChunk,
            ChunkDelayMs
        );
    }

    /// <summary>
    ///     Number of words to send per SSE chunk.
    /// </summary>
    public int WordsPerChunk { get; set; } = DefaultWordsPerChunk;

    /// <summary>
    ///     Delay between chunks in milliseconds to simulate streaming.
    /// </summary>
    public int ChunkDelayMs { get; set; } = DefaultChunkDelayMs;

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(
            "AnthropicTestSseMessageHandler.SendAsync called - Method: {Method}, URI: {Uri}",
            request.Method,
            request.RequestUri
        );

        if (request.Method != HttpMethod.Post || request.RequestUri == null)
        {
            _logger.LogTrace("Not POST or no URI, returning 404");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        // Check for Anthropic API endpoint (any path ending with /messages)
        if (!request.RequestUri.AbsolutePath.EndsWith("/messages", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogTrace(
                "Path doesn't match /messages: {Path}",
                request.RequestUri.AbsolutePath
            );
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        _logger.LogDebug("Processing Anthropic messages request to {Path}", request.RequestUri.AbsolutePath);

        var body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(body))
        {
            _logger.LogWarning("Empty request body received");
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("Missing request body"),
            };
        }

        _logger.LogTrace("Request body length: {Length} characters", body.Length);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse request JSON");
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent($"Invalid JSON: {ex.Message}"),
            };
        }

        using (doc)
        {
            var root = doc.RootElement;

            // Check for streaming mode
            var stream =
                root.TryGetProperty("stream", out var streamProp) && streamProp.ValueKind == JsonValueKind.True;

            // Extract model
            var model =
                root.TryGetProperty("model", out var modelProp) && modelProp.ValueKind == JsonValueKind.String
                    ? modelProp.GetString()
                    : "claude-sonnet-4-5-20250929";

            _logger.LogDebug("Processing {Mode} request for model: {Model}", stream ? "streaming" : "non-streaming", model);

            // Analyze conversation for instruction chains
            var (instruction, responseCount) = _conversationAnalyzer.AnalyzeConversation(root);

            _logger.LogInformation(
                "Conversation analysis complete - Instruction found: {HasInstruction}, ResponseCount: {ResponseCount}",
                instruction != null,
                responseCount
            );

            // Get the instruction plan to execute
            InstructionPlan? planToExecute = instruction;

            if (planToExecute == null)
            {
                // Check if chain was exhausted vs no chain found
                if (responseCount > 0)
                {
                    _logger.LogInformation(
                        "Instruction chain exhausted after {Count} executions, generating completion message",
                        responseCount
                    );

                    planToExecute = new InstructionPlan("completion", null, [InstructionMessage.ForText(5)]);
                }
                else
                {
                    // No chain found - fall back to single instruction for backward compatibility
                    var latest = _conversationAnalyzer.ExtractLatestUserMessage(root) ?? string.Empty;
                    _logger.LogDebug("No instruction chain found, attempting single instruction parse from: {MessagePreview}",
                        latest.Length > 100 ? latest[..100] + "..." : latest);

                    var (plan, _) = TryParseInstructionPlan(latest);

                    if (plan != null)
                    {
                        planToExecute = plan;
                        _logger.LogInformation("Using single instruction mode (backward compatibility)");
                    }
                    else
                    {
                        _logger.LogDebug("No instructions found, generating simple text response");
                        planToExecute = new InstructionPlan(
                            "fallback",
                            latest.Contains("\nReason:", StringComparison.Ordinal) ? 20 : null,
                            [InstructionMessage.ForText(20)]);
                    }
                }
            }

            // Resolve any dynamic message placeholders (system_prompt_echo, tools_list, request metadata)
            ResolveDynamicMessages(planToExecute, root, request);

            _logger.LogInformation(
                "Executing instruction: IdMessage={IdMessage}, ReasoningLength={ReasoningLength}, MessageCount={MessageCount}",
                planToExecute.IdMessage,
                planToExecute.ReasoningLength,
                planToExecute.Messages.Count
            );

            HttpContent content;

            if (stream)
            {
                // Streaming response
                content = new AnthropicSseStreamHttpContent(planToExecute, model, WordsPerChunk, ChunkDelayMs);
                _logger.LogDebug("Returning SSE streaming response");
            }
            else
            {
                // Non-streaming response - generate JSON
                content = CreateNonStreamingResponse(planToExecute, model!);
                _logger.LogDebug("Returning non-streaming JSON response");
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            response.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            response.Headers.ConnectionClose = false;

            return response;
        }
    }

    private (InstructionPlan? plan, string fallback) TryParseInstructionPlan(string userMessage)
    {
        _logger.LogTrace("Parsing user message for single instruction");

        var plans = _chainParser.ExtractInstructionChain(userMessage);

        if (plans != null && plans.Length > 0)
        {
            _logger.LogDebug("Found single instruction in user message");
            return (plans[0], userMessage);
        }

        _logger.LogTrace("No instruction found in message, using fallback");
        return (null, userMessage);
    }

    /// <summary>
    ///     Resolves any dynamic message placeholders in the instruction plan using request context.
    /// </summary>
    private void ResolveDynamicMessages(
        InstructionPlan plan,
        JsonElement requestRoot,
        HttpRequestMessage request
    )
    {
        for (var i = 0; i < plan.Messages.Count; i++)
        {
            var message = plan.Messages[i];

            if (message.ExplicitText == "__SYSTEM_PROMPT__")
            {
                var systemPrompt = ExtractSystemPrompt(requestRoot);
                _logger.LogDebug(
                    "Resolved __SYSTEM_PROMPT__ placeholder to: {Preview}",
                    systemPrompt.Length > 50 ? systemPrompt[..50] + "..." : systemPrompt
                );
                message.ExplicitText = systemPrompt;
            }
            else if (message.ExplicitText == "__TOOLS_LIST__")
            {
                var toolsList = ExtractToolsList(requestRoot);
                _logger.LogDebug("Resolved __TOOLS_LIST__ placeholder to: {ToolsList}", toolsList);
                message.ExplicitText = toolsList;
            }
            else if (message.ExplicitText == "__REQUEST_URL__")
            {
                message.ExplicitText = request.RequestUri?.ToString() ?? "No request URL";
                _logger.LogDebug("Resolved __REQUEST_URL__ placeholder to: {Url}", message.ExplicitText);
            }
            else if (message.ExplicitText == "__REQUEST_HEADERS__")
            {
                message.ExplicitText = ExtractRequestHeaders(request);
                _logger.LogDebug("Resolved __REQUEST_HEADERS__ placeholder");
            }
            else if (message.ExplicitText != null && message.ExplicitText.StartsWith("__REQUEST_PARAMS__"))
            {
                var fieldFilter = message.ExplicitText.Contains(':')
                    ? message.ExplicitText.Split(':', 2)[1].Split(',')
                    : null;
                message.ExplicitText = ExtractRequestParams(requestRoot, fieldFilter);
                _logger.LogDebug("Resolved __REQUEST_PARAMS__ placeholder");
            }
        }
    }

    /// <summary>
    ///     Extracts request headers as a newline-separated string.
    /// </summary>
    private static string ExtractRequestHeaders(HttpRequestMessage request)
    {
        var headers = new List<string>();
        foreach (var header in request.Headers)
        {
            headers.Add($"{header.Key}: {string.Join(", ", header.Value)}");
        }

        if (request.Content?.Headers != null)
        {
            foreach (var header in request.Content.Headers)
            {
                headers.Add($"{header.Key}: {string.Join(", ", header.Value)}");
            }
        }

        return headers.Count > 0 ? string.Join("\n", headers) : "No headers";
    }

    /// <summary>
    ///     Extracts request body parameters, optionally filtered to specific fields.
    /// </summary>
    private static string ExtractRequestParams(JsonElement root, string[]? fields)
    {
        if (fields == null || fields.Length == 0)
        {
            return root.GetRawText();
        }

        var result = new Dictionary<string, string>();
        foreach (var field in fields)
        {
            if (root.TryGetProperty(field, out var value))
            {
                result[field] = value.ToString();
            }
        }

        return result.Count > 0 ? JsonSerializer.Serialize(result) : "No matching params";
    }

    /// <summary>
    ///     Extracts the system prompt from the Anthropic request.
    ///     Anthropic uses a "system" property at the root level.
    /// </summary>
    private static string ExtractSystemPrompt(JsonElement root)
    {
        // Anthropic format: { "system": "..." } at root level
        if (root.TryGetProperty("system", out var system))
        {
            if (system.ValueKind == JsonValueKind.String)
            {
                return system.GetString() ?? "No system prompt configured";
            }

            // Handle array format for system
            if (system.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in system.EnumerateArray())
                {
                    if (item.TryGetProperty("type", out var type)
                        && type.GetString() == "text"
                        && item.TryGetProperty("text", out var text))
                    {
                        return text.GetString() ?? "No system prompt configured";
                    }
                }
            }
        }

        return "No system prompt configured";
    }

    /// <summary>
    ///     Creates a non-streaming JSON response based on the instruction plan.
    /// </summary>
    private StringContent CreateNonStreamingResponse(InstructionPlan plan, string model)
    {
        var messageId = $"msg_{Guid.NewGuid():N}";
        var content = new List<object>();

        foreach (var message in plan.Messages)
        {
            if (message.TextLength is int len && len > 0)
            {
                var text = GenerateLoremIpsum(len);
                content.Add(new { type = "text", text });
            }
            else if (!string.IsNullOrEmpty(message.ExplicitText))
            {
                content.Add(new { type = "text", text = message.ExplicitText });
            }
            else if (message.ToolCalls != null)
            {
                foreach (var toolCall in message.ToolCalls)
                {
                    var inputObj = string.IsNullOrEmpty(toolCall.ArgsJson)
                        ? new { }
                        : JsonSerializer.Deserialize<object>(toolCall.ArgsJson);

                    content.Add(new
                    {
                        type = "tool_use",
                        id = $"toolu_{Guid.NewGuid():N}",
                        name = toolCall.Name,
                        input = inputObj,
                    });
                }
            }
            else if (message.ServerToolUse is { } stu)
            {
                content.Add(new
                {
                    type = "server_tool_use",
                    id = stu.Id ?? $"srvtoolu_{Guid.NewGuid():N}",
                    name = stu.Name,
                    input = stu.Input.HasValue
                        ? JsonSerializer.Deserialize<object>(stu.Input.Value.GetRawText())
                        : new object(),
                });
            }
            else if (message.ServerToolResult is { } str)
            {
                content.Add(new
                {
                    type = "web_search_tool_result",
                    tool_use_id = str.ToolUseId ?? $"srvtoolu_{Guid.NewGuid():N}",
                    content = str.Result.HasValue
                        ? JsonSerializer.Deserialize<object>(str.Result.Value.GetRawText())
                        : new object(),
                });
            }
            else if (message.TextWithCitations is { } twc)
            {
                var text = twc.Text ?? GenerateLoremIpsum(twc.Length ?? 20);
                var citations = twc.Citations?.Select(c => new
                {
                    type = c.Type,
                    url = c.Url,
                    title = c.Title,
                    cited_text = c.CitedText,
                    start_char_index = 0,
                    end_char_index = text.Length,
                }).ToList<object>() ?? [];

                content.Add(new
                {
                    type = "text",
                    text,
                    citations,
                });
            }
        }

        // Default to a simple text response if no content was generated
        if (content.Count == 0)
        {
            content.Add(new { type = "text", text = "Response generated from instruction chain." });
        }

        var response = new
        {
            type = "message",
            id = messageId,
            role = "assistant",
            content,
            model,
            stop_reason = "end_turn",
            stop_sequence = (string?)null,
            usage = new { input_tokens = 100, output_tokens = 50 },
        };

        var json = JsonSerializer.Serialize(response);
        _logger.LogTrace("Generated non-streaming response: {Response}", json);

        return new StringContent(json, System.Text.Encoding.UTF8, "application/json");
    }

    /// <summary>
    ///     Generates lorem ipsum text of approximately the specified word count.
    /// </summary>
    private static string GenerateLoremIpsum(int wordCount)
    {
        var lorem = (
            "lorem ipsum dolor sit amet consectetur adipiscing elit sed do eiusmod tempor "
            + "incididunt ut labore et dolore magna aliqua ut enim ad minim veniam quis nostrud "
            + "exercitation ullamco laboris nisi ut aliquip ex ea commodo consequat duis aute irure "
            + "dolor in reprehenderit in voluptate velit esse cillum dolore eu fugiat nulla pariatur "
            + "excepteur sint occaecat cupidatat non proident sunt in culpa qui officia deserunt mollit anim id est laborum"
        ).Split(' ');

        var words = new List<string>(wordCount);
        for (var i = 0; i < wordCount; i++)
        {
            words.Add(lorem[i % lorem.Length]);
        }

        return string.Join(' ', words);
    }

    /// <summary>
    ///     Creates an instruction plan that simulates a web_search server tool flow:
    ///     server_tool_use → server_tool_result → text_with_citations.
    /// </summary>
    private static InstructionPlan CreateWebSearchPlan(string userMessage)
    {
        var query = string.IsNullOrWhiteSpace(userMessage) ? "general knowledge" : userMessage;
        var toolUseId = $"srvtoolu_{Guid.NewGuid():N}";

        // 1. Server tool use: web_search with user's query
        var serverToolUse = new InstructionServerToolUse
        {
            Id = toolUseId,
            Name = "web_search",
            Input = JsonDocument.Parse(JsonSerializer.Serialize(new { query })).RootElement,
        };

        // 2. Server tool result: mock search results
        var searchResultJson = JsonSerializer.Serialize(new
        {
            type = "web_search_result",
            search_results = new[]
            {
                new
                {
                    title = "Understanding " + (query.Length > 40 ? query[..40] : query),
                    url = "https://example.com/article-1",
                    encrypted_content = Convert.ToBase64String(
                        System.Text.Encoding.UTF8.GetBytes("mock-encrypted-content-1")),
                    page_age = "2 days ago",
                },
                new
                {
                    title = "Research on " + (query.Length > 40 ? query[..40] : query),
                    url = "https://example.org/research-2",
                    encrypted_content = Convert.ToBase64String(
                        System.Text.Encoding.UTF8.GetBytes("mock-encrypted-content-2")),
                    page_age = "1 week ago",
                },
            },
        });

        var serverToolResult = new InstructionServerToolResult
        {
            ToolUseId = toolUseId,
            Name = "web_search",
            Result = JsonDocument.Parse(searchResultJson).RootElement,
        };

        // 3. Text with citations referencing the search results
        var citedText =
            $"Based on recent search results, here is information about your query. " +
            $"The first source provides a comprehensive overview of the topic with detailed analysis. " +
            $"Additional research from a second source confirms these findings and adds further context " +
            $"with supporting evidence and examples.";

        var textWithCitations = new InstructionTextWithCitations
        {
            Text = citedText,
            Citations =
            [
                new InstructionCitation
                {
                    Type = "web_search_result_location",
                    Url = "https://example.com/article-1",
                    Title = "Understanding " + (query.Length > 40 ? query[..40] : query),
                    CitedText = "comprehensive overview of the topic with detailed analysis",
                },
                new InstructionCitation
                {
                    Type = "web_search_result_location",
                    Url = "https://example.org/research-2",
                    Title = "Research on " + (query.Length > 40 ? query[..40] : query),
                    CitedText = "confirms these findings and adds further context",
                },
            ],
        };

        return new InstructionPlan(
            "auto-web-search",
            null,
            [
                InstructionMessage.ForServerToolUse(serverToolUse),
                InstructionMessage.ForServerToolResult(serverToolResult),
                InstructionMessage.ForTextWithCitations(textWithCitations),
            ]
        );
    }

    /// <summary>
    ///     Detects built-in (server-side) tools in the request's tools array.
    ///     Built-in tools have a "type" property (e.g., "web_search_20250305") rather than "name" + "input_schema".
    /// </summary>
    /// <returns>List of normalized tool names (e.g., "web_search", "code_execution").</returns>
    private static List<string> DetectBuiltInTools(JsonElement root)
    {
        var builtInTools = new List<string>();

        if (!root.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
        {
            return builtInTools;
        }

        foreach (var tool in tools.EnumerateArray())
        {
            if (tool.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!tool.TryGetProperty("type", out var typeProp) || typeProp.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var typeStr = typeProp.GetString();
            if (string.IsNullOrEmpty(typeStr))
            {
                continue;
            }

            // Built-in tools have type like "web_search_20250305", "code_execution_20250522", etc.
            // Function tools have type "custom" or just have name+input_schema.
            if (typeStr.StartsWith("web_search_", StringComparison.Ordinal))
            {
                builtInTools.Add("web_search");
            }
            else if (typeStr.StartsWith("web_fetch_", StringComparison.Ordinal))
            {
                builtInTools.Add("web_fetch");
            }
            else if (typeStr.StartsWith("code_execution_", StringComparison.Ordinal))
            {
                builtInTools.Add("code_execution");
            }
        }

        return builtInTools;
    }

    /// <summary>
    ///     Extracts tool names from the Anthropic request tools array.
    /// </summary>
    private static string ExtractToolsList(JsonElement root)
    {
        if (!root.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
        {
            return "No tools available";
        }

        var toolNames = new List<string>();

        foreach (var tool in tools.EnumerateArray())
        {
            if (tool.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            // Anthropic format: { "name": "...", "description": "...", "input_schema": {...} }
            if (tool.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
            {
                var toolName = name.GetString();
                if (!string.IsNullOrEmpty(toolName))
                {
                    toolNames.Add(toolName);
                }
            }
        }

        return toolNames.Count == 0 ? "No tools available" : string.Join(", ", toolNames);
    }
}
