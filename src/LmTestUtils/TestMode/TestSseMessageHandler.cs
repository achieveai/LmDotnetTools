using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmTestUtils.TestMode;

/// <summary>
/// HTTP message handler for test mode that simulates SSE streaming responses.
/// Processes instruction chains and generates mock LLM responses for testing.
/// </summary>
public sealed class TestSseMessageHandler : HttpMessageHandler
{
    // Default configuration values with clear intent
    private const int DefaultWordsPerChunk = 10; // Number of words to send per SSE chunk
    private const int DefaultChunkDelayMs = 500; // Delay between chunks to simulate streaming

    private readonly ILogger<TestSseMessageHandler> _logger;
    private readonly IInstructionChainParser _chainParser;
    private readonly IConversationAnalyzer _conversationAnalyzer;

    public int WordsPerChunk { get; set; } = DefaultWordsPerChunk;
    public int ChunkDelayMs { get; set; } = DefaultChunkDelayMs;

    /// <summary>
    /// Initializes a new instance for test mode with default services.
    /// Used for backward compatibility when DI is not available.
    /// </summary>
    public TestSseMessageHandler()
        : this(
            LoggerFactory
                .Create(builder => builder.AddConsole())
                .CreateLogger<TestSseMessageHandler>(),
            null,
            null
        )
    { }

    /// <summary>
    /// Initializes a new instance with dependency injection.
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
                LoggerFactory
                    .Create(builder => builder.AddConsole())
                    .CreateLogger<InstructionChainParser>()
            );

        _conversationAnalyzer =
            conversationAnalyzer
            ?? new ConversationAnalyzer(
                LoggerFactory
                    .Create(builder => builder.AddConsole())
                    .CreateLogger<ConversationAnalyzer>(),
                _chainParser
            );
    }

    /// <summary>
    /// Processes HTTP requests to simulate LLM chat completions with SSE streaming.
    /// </summary>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(
            "SendAsync called - Method: {Method}, URI: {Uri}",
            request.Method,
            request.RequestUri
        );
        if (request.Method != HttpMethod.Post || request.RequestUri == null)
        {
            _logger.LogTrace("Not POST or no URI, returning 404");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        if (
            !request.RequestUri.AbsolutePath.EndsWith(
                "/v1/chat/completions",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            _logger.LogTrace(
                "Path doesn't match /v1/chat/completions: {Path}",
                request.RequestUri.AbsolutePath
            );
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
        _logger.LogTrace("Processing chat completions request");

        var body =
            request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
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
                root.TryGetProperty("stream", out var streamProp)
                && streamProp.ValueKind == JsonValueKind.True;

            if (!stream)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            var model =
                root.TryGetProperty("model", out var modelProp)
                && modelProp.ValueKind == JsonValueKind.String
                    ? modelProp.GetString()
                    : null;

            // Analyze full conversation for instruction chains
            var (instruction, responseCount) = _conversationAnalyzer.AnalyzeConversation(root);

            HttpContent content;
            if (instruction != null)
            {
                // Execute the instruction at the calculated index
                _logger.LogInformation(
                    "Executing instruction {Index}: {Id}",
                    responseCount + 1,
                    instruction.IdMessage
                );

                content = new SseStreamHttpContent(
                    instructionPlan: instruction,
                    model: model,
                    wordsPerChunk: WordsPerChunk,
                    chunkDelayMs: ChunkDelayMs
                );
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
                        new List<InstructionMessage> { InstructionMessage.ForText(5) } // "Task completed successfully"
                    );

                    content = new SseStreamHttpContent(
                        instructionPlan: completion,
                        model: model,
                        wordsPerChunk: WordsPerChunk,
                        chunkDelayMs: ChunkDelayMs
                    );
                }
                else
                {
                    // No chain found - fall back to existing single instruction logic for backward compatibility
                    var latest =
                        _conversationAnalyzer.ExtractLatestUserMessage(root) ?? string.Empty;
                    var (plan, fallbackMessage) = TryParseInstructionPlan(latest);

                    if (plan is not null)
                    {
                        _logger.LogInformation(
                            "Using single instruction mode (backward compatibility)"
                        );
                        content = new SseStreamHttpContent(
                            instructionPlan: plan,
                            model: model,
                            wordsPerChunk: WordsPerChunk,
                            chunkDelayMs: ChunkDelayMs
                        );
                    }
                    else
                    {
                        // Generate simple response based on user message
                        var reasoningFirst = fallbackMessage.Contains(
                            "\nReason:",
                            StringComparison.Ordinal
                        );
                        content = new SseStreamHttpContent(
                            userMessage: fallbackMessage,
                            model: model,
                            reasoningFirst: reasoningFirst,
                            wordsPerChunk: WordsPerChunk,
                            chunkDelayMs: ChunkDelayMs
                        );
                    }
                }
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            response.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            response.Headers.ConnectionClose = false;
            return response;
        }
    }

    /// <summary>
    /// Attempts to parse an instruction plan from user message for backward compatibility.
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
}
