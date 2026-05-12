using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmTestUtils.TestMode;

/// <summary>
/// Scripted SSE responder for end-to-end tests. Composes per-role queues of
/// <see cref="InstructionPlan"/> and dispatches incoming HTTP requests to the appropriate queue
/// based on caller-supplied predicates over the request payload.
/// </summary>
/// <remarks>
/// <para>
/// OpenAI and Anthropic chat-style requests are classified by request shape (system prompt,
/// tool list) and pop the next plan from the matching role's queue. OpenAI Responses requests
/// use embedded instruction-chain tags instead, matching the Codex mock transport.
/// </para>
/// <para>
/// The responder produces <see cref="HttpMessageHandler"/> instances for both OpenAI and
/// Anthropic wire formats. Both reuse the existing <see cref="SseStreamHttpContent"/> and
/// <see cref="AnthropicSseStreamHttpContent"/> emitters — no SSE framing is duplicated here.
/// </para>
/// </remarks>
public sealed class ScriptedSseResponder
{
    private readonly IReadOnlyList<ScriptedRole> _roles;
    private readonly ILogger _logger;

    internal ScriptedSseResponder(IReadOnlyList<ScriptedRole> roles, ILogger? logger)
    {
        _roles = roles;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Starts a new scripted responder definition.</summary>
    public static ScriptedSseBuilder New()
    {
        return new();
    }

    /// <summary>Wraps this responder in an OpenAI-flavored <see cref="HttpMessageHandler"/>.</summary>
    public HttpMessageHandler AsOpenAiHandler(ILogger? logger = null)
    {
        return new ScriptedHandler(this, ScriptedWireFormat.OpenAi, logger ?? _logger);
    }

    /// <summary>Wraps this responder in an Anthropic-flavored <see cref="HttpMessageHandler"/>.</summary>
    public HttpMessageHandler AsAnthropicHandler(ILogger? logger = null)
    {
        return new ScriptedHandler(this, ScriptedWireFormat.Anthropic, logger ?? _logger);
    }

    /// <summary>
    ///     Wraps this responder in an OpenAI Responses API <see cref="HttpMessageHandler"/>
    ///     that handles <c>POST /v1/responses</c> SSE streaming.
    /// </summary>
    public HttpMessageHandler AsOpenAiResponsesHandler(ILogger? logger = null)
    {
        return new ScriptedHandler(this, ScriptedWireFormat.OpenAiResponses, logger ?? _logger);
    }

    /// <summary>
    ///     Emits a single response.create exchange to <paramref name="socket"/>: matches the
    ///     supplied <paramref name="ctx"/> against the role table, picks the next plan, and
    ///     writes each <see cref="ResponseEvent"/> as a single text frame in wire order.
    /// </summary>
    /// <remarks>
    ///     The host is responsible for the surrounding read loop — this method handles a single
    ///     turn (one <c>response.create</c> → one event-stream burst). The frame contents are
    ///     identical to what <see cref="OpenAiResponsesEventStreamWriter"/> produces, so
    ///     WebSocket and HTTP+SSE deliver byte-equal events at the JSON layer.
    /// </remarks>
    public async Task EmitResponseEventsAsync(
        WebSocket socket,
        ScriptedRequestContext ctx,
        string? model = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(ctx);

        var plan =
            ctx.Wire == ScriptedWireFormat.OpenAiResponses
                ? OpenAiResponsesInstructionPlanResolver.TryResolvePlan(ctx.RequestBody, logger: _logger)
                    ?? TakeNextPlan(ctx)
                    ?? new InstructionPlan("scripted-fallback", null, [InstructionMessage.ForExplicitText("ok")])
                : TakeNextPlan(ctx)
                    ?? new InstructionPlan("scripted-fallback", null, [InstructionMessage.ForExplicitText("ok")]);

        var events = OpenAiResponsesEventStreamWriter.Write(plan, model);
        foreach (var ev in events)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var json = ResponseEventParser.ToJsonObject(ev).ToJsonString();
            var bytes = Encoding.UTF8.GetBytes(json);
            await socket
                .SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
    }

    internal InstructionPlan? TakeNextPlan(ScriptedRequestContext ctx)
    {
        // Dispatch is predicate-driven rather than queue-locked: role predicates MUST be
        // non-overlapping so two concurrent requests can't both match the same role and race
        // on its queue. Every shipped scenario keys on distinct system-prompt markers, which
        // satisfies this invariant.
        foreach (var role in _roles)
        {
            if (role.Matches(ctx))
            {
                var plan = role.TryDequeue();
                _logger.LogDebug("Matched role {Role}; plan={Plan}", role.Key, plan?.IdMessage ?? "<exhausted>");
                return plan;
            }
        }

        _logger.LogWarning("No role matched the incoming request");
        return null;
    }

    /// <summary>Snapshot of remaining plan counts per role — useful for assertions.</summary>
    public IReadOnlyDictionary<string, int> RemainingTurns => _roles.ToDictionary(r => r.Key, r => r.Remaining);
}

/// <summary>Fluent builder for <see cref="ScriptedSseResponder"/>.</summary>
public sealed class ScriptedSseBuilder
{
    private readonly List<ScriptedRole> _roles = [];
    private ILogger? _logger;

    internal ScriptedSseBuilder() { }

    /// <summary>Attach a logger for dispatch diagnostics.</summary>
    public ScriptedSseBuilder WithLogger(ILogger logger)
    {
        _logger = logger;
        return this;
    }

    /// <summary>
    /// Begin defining a role. The <paramref name="matches"/> predicate is evaluated against each
    /// incoming request; the first matching role claims the request. Roles are evaluated in
    /// declaration order, so place the most specific predicate first.
    /// </summary>
    public ScriptedRoleBuilder ForRole(string key, Func<ScriptedRequestContext, bool> matches)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(matches);

        var role = new ScriptedRole(key, matches);
        _roles.Add(role);
        return new ScriptedRoleBuilder(this, role);
    }

    /// <summary>Finalize the builder into a responder.</summary>
    public ScriptedSseResponder Build()
    {
        return new(_roles, _logger);
    }
}

/// <summary>Per-role turn builder.</summary>
public sealed class ScriptedRoleBuilder
{
    private readonly ScriptedSseBuilder _parent;
    private readonly ScriptedRole _role;

    internal ScriptedRoleBuilder(ScriptedSseBuilder parent, ScriptedRole role)
    {
        _parent = parent;
        _role = role;
    }

    /// <summary>Append a turn whose <see cref="InstructionPlan"/> is composed by <paramref name="configure"/>.</summary>
    public ScriptedRoleBuilder Turn(Action<InstructionPlanBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new InstructionPlanBuilder(_role.Key, _role.TurnCount);
        configure(builder);
        _role.Enqueue(builder.Build());
        return this;
    }

    /// <summary>Begin defining another role.</summary>
    public ScriptedRoleBuilder ForRole(string key, Func<ScriptedRequestContext, bool> matches)
    {
        return _parent.ForRole(key, matches);
    }

    /// <summary>Finalize the builder.</summary>
    public ScriptedSseResponder Build()
    {
        return _parent.Build();
    }
}

/// <summary>Fluent <see cref="InstructionPlan"/> composer used inside <see cref="ScriptedRoleBuilder.Turn"/>.</summary>
public sealed class InstructionPlanBuilder
{
    private readonly List<InstructionMessage> _messages = [];
    private readonly string _idBase;
    private int? _reasoningLength;
    private int _cacheCreation;
    private int _cacheRead;

    internal InstructionPlanBuilder(string roleKey, int turnIndex)
    {
        _idBase = $"{roleKey}#{turnIndex}";
    }

    /// <summary>Append an explicit text block.</summary>
    public InstructionPlanBuilder Text(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        _messages.Add(InstructionMessage.ForExplicitText(content));
        return this;
    }

    /// <summary>Append a random-length text block (measured in words).</summary>
    public InstructionPlanBuilder TextLen(int wordCount)
    {
        if (wordCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(wordCount), "Must be positive");
        }

        _messages.Add(InstructionMessage.ForText(wordCount));
        return this;
    }

    /// <summary>Set extended-thinking reasoning length (tokens) on the plan.</summary>
    public InstructionPlanBuilder Thinking(int lengthTokens)
    {
        if (lengthTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lengthTokens), "Must be positive");
        }

        _reasoningLength = lengthTokens;
        return this;
    }

    /// <summary>Append a tool call with JSON-serialized arguments.</summary>
    public InstructionPlanBuilder ToolCall(string name, object args)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(args);

        var argsJson = JsonSerializer.Serialize(args);
        _messages.Add(InstructionMessage.ForToolCalls([new InstructionToolCall(name, argsJson)]));
        return this;
    }

    /// <summary>Append multiple tool calls (useful for parallel fan-out turns).</summary>
    public InstructionPlanBuilder ToolCalls(params (string Name, object Args)[] calls)
    {
        ArgumentNullException.ThrowIfNull(calls);

        var instructionCalls = calls
            .Select(c => new InstructionToolCall(c.Name, JsonSerializer.Serialize(c.Args)))
            .ToList();
        _messages.Add(InstructionMessage.ForToolCalls(instructionCalls));
        return this;
    }

    /// <summary>Surface cache-usage metrics on the <c>message_start</c> usage event (Anthropic only).</summary>
    public InstructionPlanBuilder CacheMetrics(int cacheCreationInputTokens, int cacheReadInputTokens)
    {
        if (cacheCreationInputTokens < 0 || cacheReadInputTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cacheCreationInputTokens), "Must be non-negative");
        }

        _cacheCreation = cacheCreationInputTokens;
        _cacheRead = cacheReadInputTokens;
        return this;
    }

    internal InstructionPlan Build()
    {
        return new(_idBase, _reasoningLength, _messages)
        {
            CacheCreationInputTokens = _cacheCreation,
            CacheReadInputTokens = _cacheRead,
        };
    }
}

/// <summary>Metadata about an incoming request for role dispatch.</summary>
public sealed class ScriptedRequestContext
{
    /// <summary>OpenAI-format or Anthropic-format.</summary>
    public ScriptedWireFormat Wire { get; init; }

    /// <summary>Raw parsed request body.</summary>
    public required JsonElement RequestBody { get; init; }

    /// <summary>Extracted system prompt (may be empty).</summary>
    public required string SystemPrompt { get; init; }

    /// <summary>Tool names advertised in the request (may be empty).</summary>
    public required IReadOnlyList<string> Tools { get; init; }

    /// <summary>Text of the most recent user message (may be null).</summary>
    public string? LatestUserMessage { get; init; }

    /// <summary>True when the request includes at least one tool_result content block.</summary>
    public bool HasToolResult { get; init; }

    /// <summary>Convenience: true if the request advertises a tool named <paramref name="name"/>.</summary>
    public bool HasTool(string name)
    {
        return Tools.Any(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Convenience: true if the system prompt contains <paramref name="substring"/>.</summary>
    public bool SystemPromptContains(string substring)
    {
        return !string.IsNullOrEmpty(substring) && SystemPrompt.Contains(substring, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Wire format discriminator.</summary>
public enum ScriptedWireFormat
{
    /// <summary>OpenAI chat/completions SSE.</summary>
    OpenAi,

    /// <summary>Anthropic messages SSE.</summary>
    Anthropic,

    /// <summary>OpenAI Responses API event stream (HTTP+SSE on <c>/v1/responses</c>).</summary>
    OpenAiResponses,
}

internal sealed class ScriptedRole
{
    private readonly ConcurrentQueue<InstructionPlan> _turns = new();
    private readonly Func<ScriptedRequestContext, bool> _matcher;
    private int _turnCount;

    public ScriptedRole(string key, Func<ScriptedRequestContext, bool> matcher)
    {
        Key = key;
        _matcher = matcher;
    }

    public string Key { get; }
    public int TurnCount => _turnCount;
    public int Remaining => _turns.Count;

    public bool Matches(ScriptedRequestContext ctx)
    {
        return _matcher(ctx);
    }

    public void Enqueue(InstructionPlan plan)
    {
        _turns.Enqueue(plan);
        _ = Interlocked.Increment(ref _turnCount);
    }

    public InstructionPlan? TryDequeue()
    {
        return _turns.TryDequeue(out var plan) ? plan : null;
    }
}

internal sealed class ScriptedHandler : HttpMessageHandler
{
    // Faster chunking than the default interactive handlers so E2E tests don't drag.
    private const int DefaultWordsPerChunk = 10;
    private const int DefaultChunkDelayMs = 5;

    private readonly ScriptedSseResponder _responder;
    private readonly ScriptedWireFormat _wire;
    private readonly ILogger _logger;

    public ScriptedHandler(ScriptedSseResponder responder, ScriptedWireFormat wire, ILogger logger)
    {
        _responder = responder;
        _wire = wire;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        if (request.Method != HttpMethod.Post || request.RequestUri == null)
        {
            _logger.LogWarning(
                "Rejecting non-POST/no-URI request: method={Method} uri={Uri}",
                request.Method,
                request.RequestUri
            );
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        var expectedSuffix = _wire switch
        {
            ScriptedWireFormat.OpenAi => "/v1/chat/completions",
            ScriptedWireFormat.OpenAiResponses => "/v1/responses",
            _ => "/messages",
        };
        if (!request.RequestUri.AbsolutePath.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Path {Path} does not end with expected suffix {Suffix} (wire={Wire})",
                request.RequestUri.AbsolutePath,
                expectedSuffix,
                _wire
            );
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
            _logger.LogWarning(
                ex,
                "Failed to parse scripted SSE request body as JSON (wire={Wire}): {ErrorMessage}",
                _wire,
                ex.Message
            );
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent($"Invalid JSON: {ex.Message}"),
            };
        }

        using (doc)
        {
            var root = doc.RootElement;
            var ctx = BuildContext(root);

            var plan =
                _wire == ScriptedWireFormat.OpenAiResponses
                    ? OpenAiResponsesInstructionPlanResolver.TryResolvePlan(root, logger: _logger)
                        ?? _responder.TakeNextPlan(ctx)
                    : _responder.TakeNextPlan(ctx);
            if (plan is null)
            {
                // Fallback: simple "ok" text so the run can complete without blowing up.
                _logger.LogWarning(
                    "No role matched or queue exhausted; emitting fallback text. Wire={Wire} SystemPrompt={SystemPromptPreview}",
                    _wire,
                    Preview(ctx.SystemPrompt)
                );
                plan = new InstructionPlan("scripted-fallback", null, [InstructionMessage.ForExplicitText("ok")]);
            }

            var stream =
                root.TryGetProperty("stream", out var streamProp) && streamProp.ValueKind == JsonValueKind.True;

            var model =
                root.TryGetProperty("model", out var modelProp) && modelProp.ValueKind == JsonValueKind.String
                    ? modelProp.GetString()
                    : null;

            if (!stream)
            {
                // Scripted tests assume streaming. Non-streaming callers are an integration
                // misconfiguration; fail loudly.
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("ScriptedSseResponder requires stream=true"),
                };
            }

            HttpContent content = _wire switch
            {
                ScriptedWireFormat.OpenAi => new SseStreamHttpContent(
                    plan,
                    model,
                    DefaultWordsPerChunk,
                    DefaultChunkDelayMs
                ),
                ScriptedWireFormat.OpenAiResponses => OpenAiResponsesSseStreamHttpContent.FromPlan(
                    plan,
                    model,
                    DefaultWordsPerChunk,
                    DefaultChunkDelayMs
                ),
                _ => new AnthropicSseStreamHttpContent(plan, model, DefaultWordsPerChunk, DefaultChunkDelayMs),
            };

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            response.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            response.Headers.ConnectionClose = false;
            return response;
        }
    }

    private ScriptedRequestContext BuildContext(JsonElement root)
    {
        var (systemPrompt, latestUser) = _wire switch
        {
            ScriptedWireFormat.OpenAi => ExtractOpenAi(root),
            ScriptedWireFormat.OpenAiResponses => ExtractOpenAiResponses(root),
            _ => ExtractAnthropic(root),
        };

        var tools = ExtractToolNames(root);

        // Note: the caller guarantees `root` stays valid for the duration of this dispatch
        // (BuildContext + TakeNextPlan run synchronously inside ScriptedHandler's `using (doc)`
        // block), so we don't need to Clone() here — avoiding the clone keeps the JsonDocument
        // pool from leaking a buffer per request.
        return new ScriptedRequestContext
        {
            Wire = _wire,
            RequestBody = root,
            SystemPrompt = systemPrompt,
            Tools = tools,
            LatestUserMessage = latestUser,
            HasToolResult =
                ContainsContentBlockType(root, "tool_result") || ContainsInputItemType(root, "function_call_output"),
        };
    }

    private static (string systemPrompt, string? latestUser) ExtractOpenAi(JsonElement root)
    {
        var systemPrompt = new StringBuilder();
        string? latest = null;

        if (root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
        {
            foreach (var msg in messages.EnumerateArray())
            {
                if (!msg.TryGetProperty("role", out var roleProp) || roleProp.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var role = roleProp.GetString();
                var text = ReadContent(msg);

                if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase))
                {
                    if (systemPrompt.Length > 0)
                    {
                        _ = systemPrompt.Append('\n');
                    }

                    _ = systemPrompt.Append(text);
                }
                else if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
                {
                    latest = text;
                }
            }
        }

        return (systemPrompt.ToString(), latest);
    }

    private static (string systemPrompt, string? latestUser) ExtractOpenAiResponses(JsonElement root)
    {
        // System prompt lives in `instructions` (string) on the Responses request.
        var systemPrompt =
            root.TryGetProperty("instructions", out var instr) && instr.ValueKind == JsonValueKind.String
                ? instr.GetString() ?? string.Empty
                : string.Empty;

        var latest = ResponsesInputReader.ExtractLatestUserText(root, concatenateAll: true);
        return (systemPrompt, latest);
    }

    private static (string systemPrompt, string? latestUser) ExtractAnthropic(JsonElement root)
    {
        var systemPrompt = string.Empty;
        if (root.TryGetProperty("system", out var sysProp))
        {
            systemPrompt = sysProp.ValueKind switch
            {
                JsonValueKind.String => sysProp.GetString() ?? string.Empty,
                JsonValueKind.Array => string.Join(
                    '\n',
                    sysProp
                        .EnumerateArray()
                        .Where(e =>
                            e.ValueKind == JsonValueKind.Object
                            && e.TryGetProperty("text", out var t)
                            && t.ValueKind == JsonValueKind.String
                        )
                        .Select(e => e.GetProperty("text").GetString() ?? string.Empty)
                ),
                _ => string.Empty,
            };
        }

        string? latest = null;
        if (root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
        {
            foreach (var msg in messages.EnumerateArray())
            {
                if (
                    !msg.TryGetProperty("role", out var roleProp)
                    || roleProp.ValueKind != JsonValueKind.String
                    || !string.Equals(roleProp.GetString(), "user", StringComparison.OrdinalIgnoreCase)
                )
                {
                    continue;
                }

                latest = ReadContent(msg);
            }
        }

        return (systemPrompt, latest);
    }

    private static string ReadContent(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content))
        {
            return string.Empty;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        if (content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var item in content.EnumerateArray())
            {
                if (
                    item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("type", out var type)
                    && type.ValueKind == JsonValueKind.String
                    && type.GetString() == "text"
                    && item.TryGetProperty("text", out var textProp)
                    && textProp.ValueKind == JsonValueKind.String
                )
                {
                    if (sb.Length > 0)
                    {
                        _ = sb.Append('\n');
                    }

                    _ = sb.Append(textProp.GetString());
                }
            }

            return sb.ToString();
        }

        return string.Empty;
    }

    private static IReadOnlyList<string> ExtractToolNames(JsonElement root)
    {
        if (!root.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var names = new List<string>();
        foreach (var tool in tools.EnumerateArray())
        {
            if (tool.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            // OpenAI chat-completions shape: { type: "function", function: { name: "..." } }
            if (
                tool.TryGetProperty("function", out var fn)
                && fn.ValueKind == JsonValueKind.Object
                && fn.TryGetProperty("name", out var fnName)
                && fnName.ValueKind == JsonValueKind.String
            )
            {
                names.Add(fnName.GetString()!);
                continue;
            }

            // Responses API + Anthropic shapes both expose `name` at the tool root:
            //   Responses: { type: "function", name: "...", parameters: {...} }
            //   Anthropic: { name: "...", description: "...", input_schema: {...} }
            if (tool.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String)
            {
                names.Add(nm.GetString()!);
            }
        }

        return names;
    }

    private static bool ContainsContentBlockType(JsonElement root, string contentType)
    {
        if (!root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var message in messages.EnumerateArray())
        {
            if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in content.EnumerateArray())
            {
                if (
                    item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("type", out var type)
                    && type.ValueKind == JsonValueKind.String
                    && string.Equals(type.GetString(), contentType, StringComparison.OrdinalIgnoreCase)
                )
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ContainsInputItemType(JsonElement root, string itemType)
    {
        if (!root.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in input.EnumerateArray())
        {
            if (
                item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && string.Equals(type.GetString(), itemType, StringComparison.OrdinalIgnoreCase)
            )
            {
                return true;
            }
        }

        return false;
    }

    private static string Preview(string s)
    {
        return string.IsNullOrEmpty(s) ? "<empty>" : (s.Length > 80 ? s[..80] + "…" : s);
    }
}
