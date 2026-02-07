using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmTestUtils.TestMode;

/// <summary>
///     HTTP message handler for test mode that simulates SSE streaming responses.
///     Processes instruction chains and generates mock LLM responses for testing.
/// </summary>
public sealed class TestSseMessageHandler : HttpMessageHandler
{
    // Default configuration values with clear intent
    private const int DefaultWordsPerChunk = 10; // Number of words to send per SSE chunk
    private const int DefaultChunkDelayMs = 500; // Delay between chunks to simulate streaming
    private readonly IInstructionChainParser _chainParser;
    private readonly IConversationAnalyzer _conversationAnalyzer;

    private readonly ILogger<TestSseMessageHandler> _logger;

    /// <summary>
    ///     Initializes a new instance for test mode with default services.
    ///     Used for backward compatibility when DI is not available.
    /// </summary>
    public TestSseMessageHandler()
        : this(LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<TestSseMessageHandler>()) { }

    /// <summary>
    ///     Initializes a new instance with dependency injection.
    /// </summary>
    public TestSseMessageHandler(
        ILogger<TestSseMessageHandler> logger,
        IInstructionChainParser? chainParser = null,
        IConversationAnalyzer? conversationAnalyzer = null
    )
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Use provided services or create defaults
        _chainParser =
            chainParser
            ?? new InstructionChainParser(
                LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<InstructionChainParser>()
            );

        _conversationAnalyzer =
            conversationAnalyzer
            ?? new ConversationAnalyzer(
                LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<ConversationAnalyzer>(),
                _chainParser
            );
    }

    public int WordsPerChunk { get; set; } = DefaultWordsPerChunk;
    public int ChunkDelayMs { get; set; } = DefaultChunkDelayMs;

    /// <summary>
    ///     Processes HTTP requests to simulate LLM chat completions with SSE streaming.
    /// </summary>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace("SendAsync called - Method: {Method}, URI: {Uri}", request.Method, request.RequestUri);
        if (request.Method != HttpMethod.Post || request.RequestUri == null)
        {
            _logger.LogTrace("Not POST or no URI, returning 404");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        if (!request.RequestUri.AbsolutePath.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogTrace("Path doesn't match /v1/chat/completions: {Path}", request.RequestUri.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        _logger.LogTrace("Processing chat completions request");

        var body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("Missing request body"),
            };
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (Exception ex)
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent($"Invalid JSON: {ex.Message}"),
            };
        }

        using (doc)
        {
            var root = doc.RootElement;

            var stream =
                root.TryGetProperty("stream", out var streamProp) && streamProp.ValueKind == JsonValueKind.True;

            var model =
                root.TryGetProperty("model", out var modelProp) && modelProp.ValueKind == JsonValueKind.String
                    ? modelProp.GetString()
                    : null;

            // Analyze full conversation for instruction chains
            var (instruction, responseCount) = _conversationAnalyzer.AnalyzeConversation(root);

            InstructionPlan? planToExecute = instruction;
            if (instruction != null)
            {
                // Resolve any dynamic message placeholders (system_prompt_echo, tools_list, request metadata)
                ResolveDynamicMessages(instruction, root, request);

                // Execute the instruction at the calculated index
                _logger.LogInformation("Executing instruction {Index}: {Id}", responseCount + 1, instruction.IdMessage);
            }
            else
            {
                // Check if this is chain exhaustion vs no chain found
                if (responseCount > 0)
                {
                    // Chain was found but exhausted - generate completion message
                    _logger.LogInformation(
                        "Chain exhausted after {Count} executions, generating completion message",
                        responseCount
                    );

                    var completion = new InstructionPlan(
                        "completion",
                        null,
                        [InstructionMessage.ForText(5)] // "Task completed successfully"
                    );
                    planToExecute = completion;
                }
                else
                {
                    // No chain found - fall back to existing single instruction logic for backward compatibility
                    var latest = _conversationAnalyzer.ExtractLatestUserMessage(root) ?? string.Empty;
                    var (plan, fallbackMessage) = TryParseInstructionPlan(latest);

                    if (plan is not null)
                    {
                        // Resolve any dynamic message placeholders (system_prompt_echo, tools_list, request metadata)
                        ResolveDynamicMessages(plan, root, request);

                        _logger.LogInformation("Using single instruction mode (backward compatibility)");
                        planToExecute = plan;
                    }
                    else
                    {
                        // Generate a simple fallback instruction from user text.
                        planToExecute = new InstructionPlan(
                            "fallback",
                            fallbackMessage.Contains("\nReason:", StringComparison.Ordinal) ? 20 : null,
                            [InstructionMessage.ForExplicitText(fallbackMessage)]
                        );
                    }
                }
            }

            HttpContent content = stream
                ? new SseStreamHttpContent(planToExecute!, model, WordsPerChunk, ChunkDelayMs)
                : CreateNonStreamingResponse(planToExecute!, model ?? "test-model");

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            response.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            response.Headers.ConnectionClose = false;
            return response;
        }
    }

    /// <summary>
    ///     Attempts to parse an instruction plan from user message for backward compatibility.
    /// </summary>
    private (InstructionPlan? plan, string fallback) TryParseInstructionPlan(string userMessage)
    {
        _logger.LogTrace("Parsing user message for single instruction");

        // Try to extract instruction chain (which may contain a single instruction)
        var plans = _chainParser.ExtractInstructionChain(userMessage);

        if (plans != null && plans.Length > 0)
        {
            // Return the first instruction for backward compatibility
            return (plans[0], userMessage);
        }

        _logger.LogTrace("No instruction found, using fallback");
        return (null, userMessage);
    }

    /// <summary>
    ///     Resolves any dynamic message placeholders in the instruction plan using request context.
    /// </summary>
    private static void ResolveDynamicMessages(
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
                message.ExplicitText = ExtractSystemPrompt(requestRoot);
            }
            else if (message.ExplicitText == "__TOOLS_LIST__")
            {
                message.ExplicitText = ExtractToolsList(requestRoot);
            }
            else if (message.ExplicitText == "__REQUEST_URL__")
            {
                message.ExplicitText = request.RequestUri?.ToString() ?? "No request URL";
            }
            else if (message.ExplicitText == "__REQUEST_HEADERS__")
            {
                message.ExplicitText = ExtractRequestHeaders(request);
            }
            else if (message.ExplicitText != null && message.ExplicitText.StartsWith("__REQUEST_PARAMS__"))
            {
                var fieldFilter = message.ExplicitText.Contains(':')
                    ? message.ExplicitText.Split(':', 2)[1].Split(',')
                    : null;
                message.ExplicitText = ExtractRequestParams(requestRoot, fieldFilter);
            }
        }
    }

    /// <summary>
    ///     Extracts the system prompt from the request messages array.
    /// </summary>
    private static string ExtractSystemPrompt(JsonElement root)
    {
        if (!root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
        {
            return "No system prompt configured";
        }

        foreach (var msg in messages.EnumerateArray())
        {
            if (msg.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!msg.TryGetProperty("role", out var role) || role.GetString() != "system")
            {
                continue;
            }

            if (msg.TryGetProperty("content", out var content))
            {
                if (content.ValueKind == JsonValueKind.String)
                {
                    return content.GetString() ?? "No system prompt configured";
                }

                // Handle array content format (e.g., [{ "type": "text", "text": "..." }])
                if (content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in content.EnumerateArray())
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
        }

        return "No system prompt configured";
    }

    /// <summary>
    ///     Extracts tool names from the request tools array.
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

            // OpenAI format: { "type": "function", "function": { "name": "..." } }
            if (tool.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
            {
                if (fn.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                {
                    var toolName = name.GetString();
                    if (!string.IsNullOrEmpty(toolName))
                    {
                        toolNames.Add(toolName);
                    }
                }
            }
        }

        return toolNames.Count == 0 ? "No tools available" : string.Join(", ", toolNames);
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

    private StringContent CreateNonStreamingResponse(InstructionPlan plan, string model)
    {
        var messageId = $"chatcmpl_{Guid.NewGuid():N}";
        var contentParts = new List<string>();
        var toolCalls = new List<object>();
        string? reasoningText = null;

        if (plan.ReasoningLength is int reasoningLength && reasoningLength > 0)
        {
            reasoningText = string.Join(" ", GenerateLoremIpsumWords(reasoningLength));
        }

        foreach (var message in plan.Messages)
        {
            if (!string.IsNullOrWhiteSpace(message.ExplicitText))
            {
                contentParts.Add(message.ExplicitText);
                continue;
            }

            if (message.TextLength is int textLength && textLength > 0)
            {
                contentParts.Add(string.Join(" ", GenerateLoremIpsumWords(textLength)));
                continue;
            }

            if (message.ToolCalls == null)
            {
                continue;
            }

            foreach (var toolCall in message.ToolCalls)
            {
                toolCalls.Add(
                    new
                    {
                        id = $"call_{Guid.NewGuid():N}",
                        type = "function",
                        function = new
                        {
                            name = toolCall.Name,
                            arguments = toolCall.ArgsJson,
                        },
                    }
                );
            }
        }

        if (contentParts.Count == 0 && toolCalls.Count == 0)
        {
            contentParts.Add("Response generated from instruction chain.");
        }

        var response = new
        {
            id = messageId,
            @object = "chat.completion",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new
                    {
                        role = "assistant",
                        content = string.Join("\n\n", contentParts),
                        reasoning = reasoningText,
                        tool_calls = toolCalls.Count > 0 ? toolCalls : null,
                    },
                    finish_reason = "stop",
                },
            },
            usage = new
            {
                prompt_tokens = 100,
                completion_tokens = 50,
                total_tokens = 150,
            },
        };

        var json = JsonSerializer.Serialize(response);
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    private static IEnumerable<string> GenerateLoremIpsumWords(int wordCount)
    {
        var lorem = (
            "lorem ipsum dolor sit amet consectetur adipiscing elit sed do eiusmod tempor incididunt ut labore et dolore "
            + "magna aliqua ut enim ad minim veniam quis nostrud exercitation ullamco laboris nisi ut aliquip ex ea "
            + "commodo consequat duis aute irure dolor in reprehenderit in voluptate velit esse cillum dolore eu fugiat "
            + "nulla pariatur excepteur sint occaecat cupidatat non proident sunt in culpa qui officia deserunt mollit "
            + "anim id est laborum"
        ).Split(' ');

        for (var i = 0; i < wordCount; i++)
        {
            yield return lorem[i % lorem.Length];
        }
    }
}
