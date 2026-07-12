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
    /// <see cref="SandboxClientOptions.TransportTimeout"/> is enforced via a token LINKED to
    /// <paramref name="ct"/> rather than <see cref="HttpClient.Timeout"/>, so it applies identically
    /// whether the underlying client is owned or borrowed (mutating a borrowed client's
    /// <see cref="HttpClient.Timeout"/> would affect every other concurrent caller of that shared
    /// client, exactly like <see cref="HttpClient.DefaultRequestHeaders"/>).
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

        using var request = new HttpRequestMessage(method, relativeUri);
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
    /// Classifies a non-success gateway response into a <see cref="SandboxException"/> WITHOUT ever
    /// reading <paramref name="response"/>'s body: an auth-rejection response is the upstream output
    /// most likely to echo submitted credential material, and an empty body (e.g. a bare <c>401</c>)
    /// must classify identically to one carrying an (ignored) error payload — reading the body first
    /// would risk a JSON-parse failure masking the real classification.
    /// </summary>
    private static SandboxException MapErrorResponse(HttpResponseMessage response, string operation)
    {
        var kind = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => SandboxErrorKind.Authorization,
            HttpStatusCode.NotFound => SandboxErrorKind.NotFound,
            _ => SandboxErrorKind.Protocol,
        };

        return new SandboxException(kind, $"Sandbox gateway returned {(int)response.StatusCode} for {operation}.", (int)response.StatusCode);
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

        using var request = new HttpRequestMessage(HttpMethod.Get, "health");
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
    /// no typed command/file operation in this release calls it yet, but the wire separation and
    /// header stamping it exercises are part of this SDK's authenticated-transport contract and are
    /// exercised directly by tests.
    /// </summary>
    internal async Task<JsonElement> SendMcpToolCallAsync(string sessionId, string toolName, object arguments, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(arguments);

        var rpcRequest = new McpToolCallRequestDto("2.0", 1, "tools/call", new McpToolCallParamsDto(toolName, arguments));
        var json = JsonSerializer.Serialize(rpcRequest, SandboxJson.McpOptions);

        using var request = new HttpRequestMessage(HttpMethod.Post, "mcp") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
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
                if (document.RootElement.TryGetProperty("error", out var errorElement) && errorElement.ValueKind != JsonValueKind.Null)
                {
                    var message = errorElement.TryGetProperty("message", out var messageElement) ? messageElement.GetString() : null;
                    throw new SandboxException(
                        SandboxErrorKind.Protocol,
                        $"Sandbox gateway MCP call '{toolName}' returned an error: {message ?? "(no message)"}.",
                        (int)response.StatusCode
                    );
                }

                if (!document.RootElement.TryGetProperty("result", out var resultElement))
                {
                    throw new SandboxException(
                        SandboxErrorKind.Protocol,
                        $"Sandbox gateway MCP call '{toolName}' returned no result.",
                        (int)response.StatusCode
                    );
                }

                // Clone before the enclosing JsonDocument is disposed: JsonElement values borrow their
                // backing buffer from the document, which becomes invalid once it is disposed.
                return resultElement.Clone();
            }
        }
    }
}
