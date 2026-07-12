using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.Sandbox.Wire;

namespace AchieveAi.LmDotnetTools.Sandbox;

public sealed partial class SandboxClient
{
    /// <summary>
    /// Sends a REST call with the gateway's snake_case JSON body (when <paramref name="body"/> is
    /// non-null), stamping <see cref="AppIdHeader"/>/<see cref="AppKeyHeader"/> (and
    /// <see cref="SessionIdHeader"/> when <paramref name="sessionId"/> is supplied) on the OUTGOING
    /// request only — never on <see cref="HttpClient.DefaultRequestHeaders"/>, which would leak this
    /// credential onto every other concurrent caller sharing a borrowed client.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="SandboxClientOptions.TransportTimeout"/> is enforced via a token LINKED to
    /// <paramref name="ct"/> rather than <see cref="HttpClient.Timeout"/>, so it applies identically
    /// whether the underlying client is owned or borrowed (mutating a borrowed client's
    /// <see cref="HttpClient.Timeout"/> would affect every other concurrent caller of that shared
    /// client, exactly like <see cref="HttpClient.DefaultRequestHeaders"/>).
    /// </para>
    /// <para>
    /// The request URI is always resolved as an ABSOLUTE URI against the validated
    /// <see cref="SandboxClientOptions.ServerAddress"/> (see <see cref="ResolveRequestUri"/>) rather
    /// than left relative for <see cref="Transport"/> to combine with its own
    /// <see cref="HttpClient.BaseAddress"/>. A borrowed <see cref="HttpClient"/>'s
    /// <see cref="HttpClient.BaseAddress"/> can be <c>null</c> (which would make a relative request
    /// throw) or point at an unrelated/mismatched host (which would silently send this credential to
    /// that host instead) — this call site never trusts it either way.
    /// </para>
    /// </remarks>
    private async Task<HttpResponseMessage> SendRestAsync(
        HttpMethod method,
        string relativeUri,
        object? body,
        string? sessionId,
        CancellationToken ct
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var request = new HttpRequestMessage(method, ResolveRequestUri(relativeUri));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: SandboxJson.RestOptions);
        }

        StampAuthHeaders(request);
        if (sessionId is not null)
        {
            _ = request.Headers.TryAddWithoutValidation(SessionIdHeader, sessionId);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_options.TransportTimeout);
        try
        {
            return await Transport.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our own linked token fired, not the caller's — this is the client-side transport
            // deadline elapsing, not caller cancellation (which is re-thrown as-is below).
            throw new SandboxException(
                SandboxErrorKind.TransportTimeout,
                $"Sandbox gateway request to '{relativeUri}' did not complete within the configured "
                    + $"transport timeout ({_options.TransportTimeout})."
            );
        }
        catch (HttpRequestException ex)
        {
            throw new SandboxException(
                SandboxErrorKind.TransportTimeout,
                $"Could not reach the sandbox gateway at '{_options.ServerAddress}'.",
                statusCode: null,
                ex
            );
        }
    }

    /// <summary>
    /// Stamps the per-app bearer headers (ADR 0029) onto <paramref name="request"/>. This is the
    /// ONLY place gateway auth headers are attached — every gated call site routes through
    /// <see cref="SendRestAsync"/> or <see cref="SendMcpToolCallAsync"/>, both of which call this, so
    /// a single seam stamps them consistently. Deliberately never touches
    /// <see cref="HttpClient.DefaultRequestHeaders"/>.
    /// </summary>
    private void StampAuthHeaders(HttpRequestMessage request)
    {
        _ = request.Headers.TryAddWithoutValidation(AppIdHeader, _options.AppId);
        _ = request.Headers.TryAddWithoutValidation(AppKeyHeader, _options.ClientSecret);
    }

    /// <summary>
    /// Resolves <paramref name="relativeUri"/> to an ABSOLUTE <see cref="Uri"/> against the
    /// constructor-validated <see cref="SandboxClientOptions.ServerAddress"/> — never against
    /// <see cref="Transport"/>'s own <see cref="HttpClient.BaseAddress"/>. Every call site in this
    /// class (REST, MCP, and <c>/health</c>) routes through this so a borrowed <see cref="HttpClient"/>
    /// can never redirect a request (and the <see cref="AppIdHeader"/>/<see cref="AppKeyHeader"/>
    /// credentials stamped on it) to a host other than the one this client was constructed for, even
    /// when that borrowed client's <see cref="HttpClient.BaseAddress"/> is <c>null</c> or points
    /// somewhere else entirely. Passing an already-absolute <see cref="Uri"/> on the outgoing
    /// <see cref="HttpRequestMessage"/> makes <see cref="HttpClient"/> ignore its own
    /// <see cref="HttpClient.BaseAddress"/> for that request.
    /// </summary>
    private Uri ResolveRequestUri(string relativeUri) => new(_options.ServerAddress, relativeUri);

    /// <summary>
    /// Classifies a non-success gateway response into a <see cref="SandboxException"/> WITHOUT ever
    /// reading <paramref name="response"/>'s body: an auth-rejection response is the upstream output
    /// most likely to echo submitted credential material, and an empty body (e.g. a bare <c>401</c>)
    /// must classify identically to one carrying an (ignored) error payload — reading the body first
    /// would risk a JSON-parse failure masking the real classification.
    /// </summary>
    /// <remarks>
    /// A <c>3xx</c> is treated as an explicit protocol violation, never followed: this SDK's owned
    /// transport disables auto-redirect, and a borrowed transport is required to do the same (see the
    /// borrowed-client constructor's security precondition). If a <c>3xx</c> is nevertheless observed
    /// here, refusing it — rather than chasing the <c>Location</c> — is the only redirect protection
    /// enforceable at this seam, and it keeps this SDK from ever replaying the <c>X-Sbx-*</c>
    /// credential headers to a redirect target itself.
    /// </remarks>
    private static SandboxException MapErrorResponse(HttpResponseMessage response, string operation)
    {
        var statusCode = (int)response.StatusCode;
        if (statusCode is >= 300 and < 400)
        {
            return new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned redirect status {statusCode} for {operation}; this SDK never "
                    + "follows redirects (following one would replay the X-Sbx-* credential headers to the "
                    + "redirect target). Point ServerAddress directly at the gateway's canonical origin.",
                statusCode
            );
        }

        var kind = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => SandboxErrorKind.Authorization,
            HttpStatusCode.NotFound => SandboxErrorKind.NotFound,
            _ => SandboxErrorKind.Protocol,
        };

        return new SandboxException(kind, $"Sandbox gateway returned {statusCode} for {operation}.", statusCode);
    }

    /// <summary>
    /// Projects each element of a 2xx-response collection through <paramref name="map"/>, rejecting
    /// any <c>null</c> element as <see cref="SandboxErrorKind.Protocol"/> first. System.Text.Json
    /// deserializes a JSON <c>null</c> array element into a <c>null</c> reference even for a
    /// non-nullable wire DTO, and projecting that <c>null</c> (or handing it to a model constructor)
    /// would otherwise throw a raw <see cref="NullReferenceException"/> that escapes this SDK's
    /// <see cref="SandboxException"/> contract. A <c>null</c> <paramref name="source"/> (absent field)
    /// maps to an empty list, distinct from a present-but-null element.
    /// </summary>
    private static List<TOut> SelectNonNullOrThrow<TIn, TOut>(
        IReadOnlyList<TIn?>? source,
        Func<TIn, TOut> map,
        string operation,
        int statusCode
    )
        where TIn : class
    {
        if (source is null)
        {
            return [];
        }

        var result = new List<TOut>(source.Count);
        foreach (var element in source)
        {
            if (element is null)
            {
                throw new SandboxException(
                    SandboxErrorKind.Protocol,
                    $"Sandbox gateway returned a null collection element for {operation}.",
                    statusCode
                );
            }

            result.Add(map(element));
        }

        return result;
    }

    /// <summary>
    /// Probes <c>GET {ServerAddress}/health</c> withOUT any credential header — the gateway's health
    /// endpoint is unauthenticated, and this method must never attach <see cref="AppIdHeader"/>/
    /// <see cref="AppKeyHeader"/> to prove it. Internal rather than public: it is a transport-readiness
    /// probe, not a control-plane operation this SDK's public contract commits to.
    /// </summary>
    internal async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var request = new HttpRequestMessage(HttpMethod.Get, ResolveRequestUri("health"));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_options.TransportTimeout);
        try
        {
            using var response = await Transport.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    /// <summary>
    /// Sends a direct MCP JSON-RPC <c>tools/call</c> request, serialized verbatim via
    /// <see cref="SandboxJson.McpOptions"/> (never the REST snake_case options) and scoped to
    /// <paramref name="sessionId"/> via <see cref="SessionIdHeader"/>. Internal transport primitive:
    /// <see cref="ExecuteAsync"/> builds every command Bash submission on it, and it is also exercised
    /// directly by tests for the wire separation and header stamping it enforces.
    /// </summary>
    internal async Task<JsonElement> SendMcpToolCallAsync(string sessionId, string toolName, object arguments, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(arguments);

        var rpcRequest = new McpToolCallRequestDto(JsonRpcVersion, McpRequestId, "tools/call", new McpToolCallParamsDto(toolName, arguments));
        var json = JsonSerializer.Serialize(rpcRequest, SandboxJson.McpOptions);

        using var request = new HttpRequestMessage(HttpMethod.Post, ResolveRequestUri("mcp"))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        StampAuthHeaders(request);
        _ = request.Headers.TryAddWithoutValidation(SessionIdHeader, sessionId);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_options.TransportTimeout);

        HttpResponseMessage response;
        try
        {
            response = await Transport.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SandboxException(
                SandboxErrorKind.TransportTimeout,
                $"Sandbox MCP call '{toolName}' did not complete within the configured transport timeout ({_options.TransportTimeout})."
            );
        }
        catch (HttpRequestException ex)
        {
            throw new SandboxException(
                SandboxErrorKind.TransportTimeout,
                $"Could not reach the sandbox gateway at '{_options.ServerAddress}' for MCP call '{toolName}'.",
                statusCode: null,
                ex
            );
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw MapErrorResponse(response, $"MCP call '{toolName}'");
            }

            JsonDocument document;
            try
            {
                var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using (stream)
                {
                    document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                }
            }
            catch (JsonException ex)
            {
                throw new SandboxException(
                    SandboxErrorKind.Protocol,
                    $"Sandbox gateway returned a malformed MCP response for '{toolName}'.",
                    (int)response.StatusCode,
                    ex
                );
            }

            using (document)
            {
                var result = ValidateMcpEnvelopeAndExtractResult(document, toolName, (int)response.StatusCode);

                // Clone before the enclosing JsonDocument is disposed: JsonElement values borrow their
                // backing buffer from the document, which becomes invalid once it is disposed.
                return result.Clone();
            }
        }
    }

    /// <summary>
    /// Validates a 2xx MCP reply as a complete JSON-RPC 2.0 response envelope BEFORE touching its
    /// <c>result</c>, and returns the <c>result</c> element on success. Every structural violation is
    /// mapped to <see cref="SandboxErrorKind.Protocol"/> so a malformed 2xx body can never surface as
    /// a raw <see cref="InvalidOperationException"/> (from calling <see cref="JsonElement.TryGetProperty(string, out JsonElement)"/>
    /// on a non-object root) or <see cref="NullReferenceException"/>. The checks, in order:
    /// <list type="bullet">
    /// <item>the root is a JSON object;</item>
    /// <item><c>jsonrpc</c> is the string <c>"2.0"</c>;</item>
    /// <item><c>id</c> is a number equal to the request's <see cref="McpRequestId"/>;</item>
    /// <item>exactly one of <c>result</c> or a non-null <c>error</c> is present (mutual exclusivity);</item>
    /// <item>
    /// when <c>error</c> is present it is a JSON object — which is then surfaced as the failure via
    /// <see cref="ProtocolErrorFromMcpEnvelope"/>, WITHOUT ever copying its gateway-controlled
    /// <c>message</c> (or any other error field) into the resulting exception.
    /// </item>
    /// </list>
    /// </summary>
    private static JsonElement ValidateMcpEnvelopeAndExtractResult(JsonDocument document, string toolName, int statusCode)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw MalformedEnvelope(toolName, statusCode, "the response is not a JSON object");
        }

        if (
            !root.TryGetProperty("jsonrpc", out var versionElement)
            || versionElement.ValueKind != JsonValueKind.String
            || !string.Equals(versionElement.GetString(), JsonRpcVersion, StringComparison.Ordinal)
        )
        {
            throw MalformedEnvelope(toolName, statusCode, $"the 'jsonrpc' member is missing or is not \"{JsonRpcVersion}\"");
        }

        if (!root.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.Number || !idElement.TryGetInt32(out var id) || id != McpRequestId)
        {
            throw MalformedEnvelope(toolName, statusCode, $"the 'id' member is missing or does not match the request id ({McpRequestId})");
        }

        var hasResult = root.TryGetProperty("result", out var resultElement);
        var hasError = root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind != JsonValueKind.Null;

        if (hasResult == hasError)
        {
            throw MalformedEnvelope(
                toolName,
                statusCode,
                hasResult ? "it carries both a 'result' and an 'error' member" : "it carries neither a 'result' nor an 'error' member"
            );
        }

        if (hasError)
        {
            if (errorElement.ValueKind != JsonValueKind.Object)
            {
                throw MalformedEnvelope(toolName, statusCode, "the 'error' member is not a JSON-RPC error object");
            }

            throw ProtocolErrorFromMcpEnvelope(toolName, statusCode, errorElement);
        }

        return resultElement;
    }

    /// <summary>
    /// Builds the <see cref="SandboxException"/> for a JSON-RPC <c>error</c> envelope member. NEVER
    /// copies the gateway-controlled <c>error.message</c> — or any other field of the error object,
    /// or the surrounding response body — into the returned exception's
    /// <see cref="Exception.Message"/> or an inner exception: that text is arbitrary content the
    /// upstream gateway/tool fully controls and may contain secrets (e.g. captured command output or
    /// credential material an upstream tool echoed back), exactly like the response bodies
    /// <see cref="MapErrorResponse"/> never reads for the same reason. The only value ever surfaced is
    /// the JSON-RPC <c>code</c> — a small, gateway-defined integer, not caller-influenced free text —
    /// and only when it genuinely deserializes as a JSON number.
    /// </summary>
    private static SandboxException ProtocolErrorFromMcpEnvelope(string toolName, int statusCode, JsonElement errorElement)
    {
        var codeSuffix =
            errorElement.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.Number && codeElement.TryGetInt32(out var code)
                ? $" (code {code})"
                : string.Empty;

        return new SandboxException(SandboxErrorKind.Protocol, $"Sandbox gateway MCP call '{toolName}' returned a JSON-RPC error{codeSuffix}.", statusCode);
    }

    private static SandboxException MalformedEnvelope(string toolName, int statusCode, string detail) =>
        new(SandboxErrorKind.Protocol, $"Sandbox gateway returned a malformed MCP response envelope for '{toolName}': {detail}.", statusCode);
}
