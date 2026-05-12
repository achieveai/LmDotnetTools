using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmTestUtils.TestMode;

/// <summary>
///     HTTP message handler for test mode that simulates the OpenAI Responses API
///     <c>/v1/responses</c> SSE event stream. Parallel to <see cref="AnthropicTestSseMessageHandler"/>
///     and <see cref="TestSseMessageHandler"/>, but speaks the Responses event grammar.
/// </summary>
/// <remarks>
///     <para>
///     Drives test scenarios via the same <see cref="InstructionChainParser"/> contract used by the
///     other mock handlers — instruction chains are embedded in the latest <c>input</c> item's
///     <c>input_text</c> content. When no chain is present, the handler emits a small lorem
///     ipsum response so unrelated callers don't break.
///     </para>
/// </remarks>
public sealed class OpenAiResponsesTestSseMessageHandler : HttpMessageHandler
{
    private const int DefaultWordsPerChunk = 5;
    private const int DefaultChunkDelayMs = 50;

    private readonly IInstructionChainParser _chainParser;
    private readonly ILogger<OpenAiResponsesTestSseMessageHandler> _logger;

    public OpenAiResponsesTestSseMessageHandler()
        : this(NullLogger<OpenAiResponsesTestSseMessageHandler>.Instance) { }

    public OpenAiResponsesTestSseMessageHandler(
        ILogger<OpenAiResponsesTestSseMessageHandler> logger,
        IInstructionChainParser? chainParser = null
    )
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _chainParser = chainParser ?? new InstructionChainParser(NullLogger<InstructionChainParser>.Instance);
    }

    public int WordsPerChunk { get; set; } = DefaultWordsPerChunk;

    public int ChunkDelayMs { get; set; } = DefaultChunkDelayMs;

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        if (request.Method != HttpMethod.Post || request.RequestUri == null)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        if (!request.RequestUri.AbsolutePath.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        var body =
            request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

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
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse /v1/responses request body as JSON");
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent($"Invalid JSON: {ex.Message}"),
            };
        }

        using (doc)
        {
            var root = doc.RootElement;

            // The Responses API requires stream=true for any client that expects events.
            // Tests sometimes omit it; default to streaming so the handler is consistent
            // with the rest of the mock suite.
            var stream =
                !root.TryGetProperty("stream", out var streamProp)
                || streamProp.ValueKind == JsonValueKind.Undefined
                || streamProp.ValueKind == JsonValueKind.True;

            if (!stream)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("OpenAiResponsesTestSseMessageHandler requires stream=true"),
                };
            }

            var model =
                root.TryGetProperty("model", out var modelProp) && modelProp.ValueKind == JsonValueKind.String
                    ? modelProp.GetString()
                    : "gpt-mock-responses";

            var plan = ResolvePlan(root);

            var events = OpenAiResponsesEventStreamWriter.Write(plan, model, WordsPerChunk);
            var content = new OpenAiResponsesSseStreamHttpContent(events, ChunkDelayMs);

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            response.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            response.Headers.ConnectionClose = false;
            return response;
        }
    }

    /// <summary>
    ///     Resolves the <see cref="InstructionPlan"/> to render. Inspects the request's
    ///     <c>input</c> array for an instruction chain on the latest user item. Falls back to a
    ///     short lorem-ipsum reply when the conversation contains no instructions.
    /// </summary>
    private InstructionPlan ResolvePlan(JsonElement root)
    {
        return OpenAiResponsesInstructionPlanResolver.ResolvePlan(root, _chainParser, _logger);
    }
}
