using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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
/// Unlike <see cref="TestSseMessageHandler"/> which extracts instruction chains embedded in user
/// messages, <see cref="ScriptedSseResponder"/> classifies each request by its shape (system
/// prompt, tool list) and pops the next plan from the matching role's queue. This lets parent
/// and sub-agent scripts stay fully independent — useful for exercising the
/// parent → <c>SubAgentManager</c> → sub-agent fan-out through <c>MultiTurnAgentPool</c>.
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
    public static ScriptedSseBuilder New() => new();

    /// <summary>Wraps this responder in an OpenAI-flavored <see cref="HttpMessageHandler"/>.</summary>
    public HttpMessageHandler AsOpenAiHandler(ILogger? logger = null) =>
        new ScriptedHandler(this, ScriptedWireFormat.OpenAi, logger ?? _logger);

    /// <summary>Wraps this responder in an Anthropic-flavored <see cref="HttpMessageHandler"/>.</summary>
    public HttpMessageHandler AsAnthropicHandler(ILogger? logger = null) =>
        new ScriptedHandler(this, ScriptedWireFormat.Anthropic, logger ?? _logger);

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
                _logger.LogDebug(
                    "Matched role {Role}; plan={Plan}",
                    role.Key,
                    plan?.IdMessage ?? "<exhausted>");
                return plan;
            }
        }

        _logger.LogWarning("No role matched the incoming request");
        return null;
    }

    /// <summary>Snapshot of remaining plan counts per role — useful for assertions.</summary>
    public IReadOnlyDictionary<string, int> RemainingTurns =>
        _roles.ToDictionary(r => r.Key, r => r.Remaining);
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
    public ScriptedSseResponder Build() => new(_roles, _logger);
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
    public ScriptedRoleBuilder ForRole(string key, Func<ScriptedRequestContext, bool> matches) =>
        _parent.ForRole(key, matches);

    /// <summary>Finalize the builder.</summary>
    public ScriptedSseResponder Build() => _parent.Build();
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

    internal InstructionPlan Build() =>
        new(_idBase, _reasoningLength, _messages)
        {
            CacheCreationInputTokens = _cacheCreation,
            CacheReadInputTokens = _cacheRead,
        };
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

    /// <summary>Convenience: true if the request advertises a tool named <paramref name="name"/>.</summary>
    public bool HasTool(string name) =>
        Tools.Any(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Convenience: true if the system prompt contains <paramref name="substring"/>.</summary>
    public bool SystemPromptContains(string substring) =>
        !string.IsNullOrEmpty(substring)
            && SystemPrompt.Contains(substring, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Wire format discriminator.</summary>
public enum ScriptedWireFormat
{
    /// <summary>OpenAI chat/completions SSE.</summary>
    OpenAi,

    /// <summary>Anthropic messages SSE.</summary>
    Anthropic,
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

    public bool Matches(ScriptedRequestContext ctx) => _matcher(ctx);

    public void Enqueue(InstructionPlan plan)
    {
        _turns.Enqueue(plan);
        Interlocked.Increment(ref _turnCount);
    }

    public InstructionPlan? TryDequeue() =>
        _turns.TryDequeue(out var plan) ? plan : null;
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
        CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Post || request.RequestUri == null)
        {
            _logger.LogWarning(
                "Rejecting non-POST/no-URI request: method={Method} uri={Uri}",
                request.Method,
                request.RequestUri);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        var expectedSuffix = _wire == ScriptedWireFormat.OpenAi ? "/v1/chat/completions" : "/messages";
        if (!request.RequestUri.AbsolutePath.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Path {Path} does not end with expected suffix {Suffix} (wire={Wire})",
                request.RequestUri.AbsolutePath,
                expectedSuffix,
                _wire);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        var body = request.Content == null
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
                ex.Message);
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent($"Invalid JSON: {ex.Message}"),
            };
        }

        using (doc)
        {
            var root = doc.RootElement;
            var ctx = BuildContext(root);

            var plan = _responder.TakeNextPlan(ctx);
            if (plan is null)
            {
                // Fallback: simple "ok" text so the run can complete without blowing up.
                _logger.LogWarning(
                    "No role matched or queue exhausted; emitting fallback text. Wire={Wire} SystemPrompt={SystemPromptPreview}",
                    _wire,
                    Preview(ctx.SystemPrompt));
                plan = new InstructionPlan(
                    "scripted-fallback",
                    null,
                    [InstructionMessage.ForExplicitText("ok")]);
            }

            var stream = root.TryGetProperty("stream", out var streamProp)
                && streamProp.ValueKind == JsonValueKind.True;

            var model = root.TryGetProperty("model", out var modelProp)
                && modelProp.ValueKind == JsonValueKind.String
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

            HttpContent content = _wire == ScriptedWireFormat.OpenAi
                ? new SseStreamHttpContent(plan, model, DefaultWordsPerChunk, DefaultChunkDelayMs)
                : new AnthropicSseStreamHttpContent(plan, model, DefaultWordsPerChunk, DefaultChunkDelayMs);

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            response.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            response.Headers.ConnectionClose = false;
            return response;
        }
    }

    private ScriptedRequestContext BuildContext(JsonElement root)
    {
        var (systemPrompt, latestUser) = _wire == ScriptedWireFormat.OpenAi
            ? ExtractOpenAi(root)
            : ExtractAnthropic(root);

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
                if (!msg.TryGetProperty("role", out var roleProp)
                    || roleProp.ValueKind != JsonValueKind.String)
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
                    sysProp.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.Object
                            && e.TryGetProperty("text", out var t)
                            && t.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetProperty("text").GetString() ?? string.Empty)),
                _ => string.Empty,
            };
        }

        string? latest = null;
        if (root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
        {
            foreach (var msg in messages.EnumerateArray())
            {
                if (!msg.TryGetProperty("role", out var roleProp)
                    || roleProp.ValueKind != JsonValueKind.String
                    || !string.Equals(roleProp.GetString(), "user", StringComparison.OrdinalIgnoreCase))
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
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("type", out var type)
                    && type.ValueKind == JsonValueKind.String
                    && type.GetString() == "text"
                    && item.TryGetProperty("text", out var textProp)
                    && textProp.ValueKind == JsonValueKind.String)
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

            // OpenAI shape: { type: "function", function: { name: "..." } }
            if (tool.TryGetProperty("function", out var fn)
                && fn.ValueKind == JsonValueKind.Object
                && fn.TryGetProperty("name", out var fnName)
                && fnName.ValueKind == JsonValueKind.String)
            {
                names.Add(fnName.GetString()!);
                continue;
            }

            // Anthropic shape: { name: "...", description: "...", input_schema: {...} }
            if (tool.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String)
            {
                names.Add(nm.GetString()!);
            }
        }

        return names;
    }

    private static string Preview(string s) =>
        string.IsNullOrEmpty(s) ? "<empty>" : (s.Length > 80 ? s[..80] + "…" : s);
}
