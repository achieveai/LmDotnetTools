using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AchieveAi.LmDotnetTools.GithubCopilotProvider.Http;

/// <summary>
///     Translates GitHub Copilot's <c>/chat/completions</c> dialect (the only endpoint Gemini models
///     accept) to and from the OpenRouter-style shape the stock <c>OpenClient</c> already speaks, so no
///     provider outside this assembly needs to know about Copilot's quirks.
/// </summary>
/// <remarks>
///     <para>Response (streamed events and non-streamed bodies):</para>
///     <list type="bullet">
///         <item>
///             A zero-token <c>usage</c> object rides on every streamed chunk. <c>OpenClient</c> drops any
///             chunk carrying zero usage, which would discard every text delta, so it is removed.
///         </item>
///         <item><c>content: null</c> becomes <c>""</c> (<c>OpenClient</c> throws on a null content).</item>
///         <item><c>reasoning_text</c> (readable thinking summary) becomes <c>reasoning</c>.</item>
///         <item>
///             <c>reasoning_opaque</c> (Gemini's thought signature) becomes an encrypted
///             <c>reasoning_details</c> entry. Copilot sends it on the tool-call chunk, and
///             <c>OpenClient</c> ignores reasoning that shares a chunk with tool calls, so the reasoning
///             is split into its own event ahead of the tool calls.
///         </item>
///     </list>
///     <para>
///         Request: an assistant message's encrypted <c>reasoning_details</c> is sent back as
///         <c>reasoning_opaque</c>, and plain <c>reasoning</c> as <c>reasoning_text</c>.
///     </para>
///     <para>Anything not recognised passes through untouched; only <c>/chat/completions</c> is touched.</para>
/// </remarks>
public sealed class CopilotChatCompletionsDialectHandler : DelegatingHandler
{
    private const string ChatCompletionsPath = "/chat/completions";
    private const string EncryptedReasoningType = "reasoning.encrypted";

    /// <summary>Creates the handler around <paramref name="innerHandler"/> (default transport when null).</summary>
    public CopilotChatCompletionsDialectHandler(HttpMessageHandler? innerHandler = null)
        : base(innerHandler ?? new HttpClientHandler()) { }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.RequestUri?.AbsolutePath.EndsWith(ChatCompletionsPath, StringComparison.Ordinal) != true)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (request.Content is not null)
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var rewritten = RewriteRequestBody(body);
            if (!ReferenceEquals(rewritten, body))
            {
                var mediaType = request.Content.Headers.ContentType?.MediaType ?? "application/json";
                request.Content = new StringContent(rewritten, Encoding.UTF8, mediaType);
            }
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return response;
        }

        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (string.Equals(contentType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            var inner = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            response.Content = WithHeadersOf(
                response.Content,
                new StreamContent(new SseLineRewriteStream(inner, RewriteSseLine))
            );
        }
        else if (string.Equals(contentType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            response.Content = WithHeadersOf(
                response.Content,
                new StringContent(RewriteResponseBody(json), Encoding.UTF8)
            );
        }

        return response;
    }

    /// <summary>Rewrites one outbound request body; returns the same instance when nothing changed.</summary>
    internal static string RewriteRequestBody(string body)
    {
        if (TryParseObject(body) is not { } root || root["messages"] is not JsonArray messages)
        {
            return body;
        }

        var changed = false;
        foreach (var message in messages.OfType<JsonObject>())
        {
            if (message["role"]?.GetValue<string>() != "assistant")
            {
                continue;
            }

            if (message["reasoning_details"] is JsonArray details)
            {
                _ = message.Remove("reasoning_details");
                changed = true;
                foreach (var detail in details.OfType<JsonObject>())
                {
                    var data = detail["data"]?.GetValue<string>() ?? detail["summary"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(data))
                    {
                        continue;
                    }

                    var key =
                        detail["type"]?.GetValue<string>() == EncryptedReasoningType
                            ? "reasoning_opaque"
                            : "reasoning_text";
                    message[key] ??= data;
                }
            }

            if (message["reasoning"] is JsonValue reasoning)
            {
                _ = message.Remove("reasoning");
                message["reasoning_text"] ??= reasoning.GetValue<string>();
                changed = true;
            }
        }

        return changed ? root.ToJsonString() : body;
    }

    /// <summary>Rewrites a non-streamed response body (<c>choices[].message</c>).</summary>
    internal static string RewriteResponseBody(string json)
    {
        if (TryParseObject(json) is not { } root || root["choices"] is not JsonArray choices)
        {
            return json;
        }

        foreach (var choice in choices.OfType<JsonObject>())
        {
            if (choice["message"] is JsonObject message)
            {
                NormalizeMessage(message);
            }
        }

        return root.ToJsonString();
    }

    /// <summary>
    ///     Rewrites one SSE line. Non-<c>data:</c> lines, <c>[DONE]</c> and unparsable payloads pass
    ///     through. A chunk may expand into two events, separated by the blank line that ends an event.
    /// </summary>
    internal static string RewriteSseLine(string line)
    {
        if (!line.StartsWith("data:", StringComparison.Ordinal) || TryParseObject(line[5..].Trim()) is not { } chunk)
        {
            return line;
        }

        return string.Join("\n\n", RewriteChunk(chunk).Select(c => "data: " + c.ToJsonString()));
    }

    private static List<JsonObject> RewriteChunk(JsonObject chunk)
    {
        if (chunk["usage"] is JsonObject usage && IsZero(usage, "prompt_tokens") && IsZero(usage, "completion_tokens"))
        {
            _ = chunk.Remove("usage");
        }

        if (chunk["choices"] is not JsonArray choices || choices.Count == 0 || choices[0] is not JsonObject choice)
        {
            return [chunk];
        }

        if (choice["delta"] is not JsonObject delta)
        {
            return [chunk];
        }

        var hasToolCalls = delta["tool_calls"] is JsonArray { Count: > 0 };
        NormalizeMessage(delta);

        var reasoning = delta["reasoning"];
        var details = delta["reasoning_details"];
        if (!hasToolCalls || (reasoning is null && details is null))
        {
            return [chunk];
        }

        // Reasoning on a tool-call chunk would be dropped downstream: give it its own event first.
        _ = delta.Remove("reasoning");
        _ = delta.Remove("reasoning_details");
        var reasoningDelta = new JsonObject { ["role"] = "assistant", ["content"] = "" };
        if (reasoning is not null)
        {
            reasoningDelta["reasoning"] = reasoning;
        }

        if (details is not null)
        {
            reasoningDelta["reasoning_details"] = details;
        }

        var reasoningChunk = new JsonObject();
        foreach (var (key, value) in chunk)
        {
            if (key is not ("choices" or "usage"))
            {
                reasoningChunk[key] = value?.DeepClone();
            }
        }

        reasoningChunk["choices"] = new JsonArray(
            new JsonObject { ["index"] = choice["index"]?.DeepClone() ?? 0, ["delta"] = reasoningDelta }
        );
        return [reasoningChunk, chunk];
    }

    private static void NormalizeMessage(JsonObject message)
    {
        if (message.TryGetPropertyValue("content", out var content) && content is null)
        {
            message["content"] = "";
        }

        if (message["reasoning_text"] is JsonValue text)
        {
            _ = message.Remove("reasoning_text");
            var value = text.GetValue<string>();
            if (!string.IsNullOrEmpty(value))
            {
                message["reasoning"] ??= value;
            }
        }

        if (message["reasoning_opaque"] is JsonValue opaque)
        {
            _ = message.Remove("reasoning_opaque");
            var value = opaque.GetValue<string>();
            if (!string.IsNullOrEmpty(value))
            {
                message["reasoning_details"] = new JsonArray(
                    new JsonObject { ["type"] = EncryptedReasoningType, ["data"] = value }
                );
            }
        }
    }

    private static bool IsZero(JsonObject usage, string key) =>
        usage[key] is JsonValue v && v.TryGetValue<long>(out var n) && n == 0;

    private static JsonObject? TryParseObject(string text)
    {
        if (string.IsNullOrEmpty(text) || text[0] != '{')
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static HttpContent WithHeadersOf(HttpContent original, HttpContent replacement)
    {
        foreach (var header in original.Headers)
        {
            if (!string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                _ = replacement.Headers.Remove(header.Key);
                _ = replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return replacement;
    }

    /// <summary>
    ///     Read-only stream that pulls one line at a time from the inner stream and yields the
    ///     rewritten line, so events are forwarded as they arrive rather than after the stream ends.
    /// </summary>
    private sealed class SseLineRewriteStream(Stream inner, Func<string, string> rewrite) : Stream
    {
        private readonly StreamReader _reader = new(inner, Encoding.UTF8);
        private byte[] _pending = [];
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            while (_offset >= _pending.Length)
            {
                var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    return 0;
                }

                _pending = Encoding.UTF8.GetBytes(rewrite(line) + "\n");
                _offset = 0;
            }

            var count = Math.Min(buffer.Length, _pending.Length - _offset);
            _pending.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        ) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _reader.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
