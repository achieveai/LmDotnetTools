using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AchieveAi.LmDotnetTools.Sandbox.Wire;

namespace AchieveAi.LmDotnetTools.Sandbox;

public sealed partial class SandboxClient
{
    /// <summary>
    /// Defensive upper bound on a single direct-API byte download the SDK buffers whole into memory (a
    /// file body or a command's stdout/stderr artifact). The download is streamed and rejected the instant
    /// it declares (<c>Content-Length</c>) OR actually streams past this, so a huge, chunked, or
    /// lying-<c>Content-Length</c> body can never force an unbounded allocation. Set generously at
    /// 64&#160;MiB — comfortably above the gateway's 8&#160;MiB default operation output cap, so ordinary
    /// command output and normal workspace files are never affected; it only guards against a pathological
    /// or malformed response.
    /// </summary>
    internal const long MaxDirectReadBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Sends a session-scoped direct-API <c>GET</c> whose response body the SDK materializes whole (a file
    /// body or a command stdout/stderr artifact) and returns those bytes, BOUNDED in both size and time.
    /// Unlike <see cref="SendDirectAsync"/> (which buffers the whole body under
    /// <see cref="HttpCompletionOption.ResponseContentRead"/> before the caller can inspect its size), this
    /// completes on <see cref="HttpCompletionOption.ResponseHeadersRead"/> and streams the body under the
    /// SAME per-call transport-timeout CTS: a declared-oversize <c>Content-Length</c> is refused before a
    /// byte is read, and a body with no/under-reported length is capped by a running byte count that throws
    /// the instant it exceeds <see cref="MaxDirectReadBytes"/>. Keeping the streaming read inside the
    /// timeout scope preserves the whole-call time bound the buffered path had. This is the ONLY byte-
    /// download seam; the small-JSON endpoints (operations submit/poll, directory listing, write response)
    /// stay on <see cref="SendDirectAsync"/>, where the cap does not apply.
    /// </summary>
    private async Task<byte[]> DownloadCappedBytesAsync(
        string relativeUri,
        string sessionId,
        string operation,
        string? operationId,
        CancellationToken ct
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var request = new HttpRequestMessage(HttpMethod.Get, ResolveRequestUri(relativeUri));
        StampAuthHeaders(request);
        _ = request.Headers.TryAddWithoutValidation(SessionIdHeader, sessionId);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_options.TransportTimeout);
        try
        {
            using var response = await Transport
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw await MapDirectErrorAsync(response, operation, sessionId).ConfigureAwait(false);
            }

            var declaredLength = response.Content.Headers.ContentLength;
            if (declaredLength is > MaxDirectReadBytes)
            {
                throw new SandboxException(
                    SandboxErrorKind.Protocol,
                    $"Sandbox gateway response for {operation} declared {declaredLength} bytes, exceeding the "
                        + $"{MaxDirectReadBytes}-byte direct-read cap; refusing to buffer it.",
                    (int)response.StatusCode,
                    operationId: operationId
                );
            }

            return await ReadStreamCappedAsync(response, operation, operationId, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our own linked token fired (the transport deadline, including a slow/never-ending body
            // stream), not the caller's — surface it as a client-side transport timeout, preserving the
            // operationId so a timed-out artifact fetch stays recoverable by re-issuing the same command.
            throw new SandboxException(
                SandboxErrorKind.TransportTimeout,
                $"Sandbox gateway request to '{relativeUri}' did not complete within the configured "
                    + $"transport timeout ({_options.TransportTimeout}).",
                operationId: operationId
            );
        }
        catch (HttpRequestException ex)
        {
            throw new SandboxException(
                SandboxErrorKind.TransportTimeout,
                $"Could not reach the sandbox gateway at '{_options.ServerAddress}'.",
                statusCode: null,
                ex,
                operationId
            );
        }
    }

    /// <summary>
    /// Reads <paramref name="response"/>'s body stream into a byte array, throwing
    /// <see cref="SandboxErrorKind.Protocol"/> the instant the running byte count exceeds
    /// <see cref="MaxDirectReadBytes"/> — so a chunked or under-declared body that streams past the cap is
    /// rejected mid-read rather than buffered whole. All reads run under the caller's (transport-timeout)
    /// token, so time stays bounded too.
    /// </summary>
    private static async Task<byte[]> ReadStreamCappedAsync(
        HttpResponseMessage response,
        string operation,
        string? operationId,
        CancellationToken ct
    )
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        // Pre-size only when the gateway declared a sane (already-capped) length; otherwise start empty
        // and let the buffer grow so a missing/over-declared length never drives a huge up-front alloc.
        var declaredLength = response.Content.Headers.ContentLength;
        var initialCapacity = declaredLength is > 0 and <= MaxDirectReadBytes ? (int)declaredLength.Value : 0;
        using var buffer = new MemoryStream(initialCapacity);

        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(chunk.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxDirectReadBytes)
            {
                throw new SandboxException(
                    SandboxErrorKind.Protocol,
                    $"Sandbox gateway response for {operation} exceeded the {MaxDirectReadBytes}-byte "
                        + "direct-read cap while streaming; refusing to buffer it.",
                    (int)response.StatusCode,
                    operationId: operationId
                );
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

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
    /// <see cref="SendRestAsync"/> or <see cref="SendDirectAsync"/>, both of which call this, so
    /// a single seam stamps them consistently. Deliberately never touches
    /// <see cref="HttpClient.DefaultRequestHeaders"/>.
    /// </summary>
    private void StampAuthHeaders(HttpRequestMessage request)
    {
        _ = request.Headers.TryAddWithoutValidation(AppIdHeader, _options.AppId);

        // Keyless dev path (AUTH_ENFORCE=off): an empty secret means no X-Sbx-App-Key header at all,
        // so an empty value never reaches the gateway and a borrowed transport's own auth handler can
        // supply the header instead without colliding with an empty one stamped here.
        if (!string.IsNullOrEmpty(_options.ClientSecret))
        {
            _ = request.Headers.TryAddWithoutValidation(AppKeyHeader, _options.ClientSecret);
        }
    }

    /// <summary>
    /// Resolves <paramref name="relativeUri"/> to an ABSOLUTE <see cref="Uri"/> against the
    /// constructor-validated <see cref="SandboxClientOptions.ServerAddress"/> — never against
    /// <see cref="Transport"/>'s own <see cref="HttpClient.BaseAddress"/>. Every call site in this
    /// class (control-plane REST, the direct file/command APIs, and <c>/health</c>) routes through
    /// this so a borrowed <see cref="HttpClient"/>
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
    /// Sends a session-scoped direct-API (ADR 0031) request that returns a SMALL JSON snapshot and hands
    /// back the RAW <see cref="HttpResponseMessage"/> for the caller to read — the caller owns and disposes
    /// it. Used by the operations submit/poll, directory listing, and file <c>PUT</c> endpoints; the two
    /// byte-DOWNLOAD paths (file read and command artifact) instead use <see cref="DownloadCappedBytesAsync"/>,
    /// which streams under a size cap. Reuses the same auth-header stamping, absolute-URI resolution (never
    /// trusting a borrowed transport's <see cref="HttpClient.BaseAddress"/>), no-auto-redirect posture, and
    /// per-call transport-timeout hardening. Completes on <see cref="HttpCompletionOption.ResponseContentRead"/>
    /// so the configured <see cref="SandboxClientOptions.TransportTimeout"/> bounds the WHOLE call — headers
    /// AND the small body — not just the headers; the response bodies here are tiny gateway-shaped JSON, so
    /// buffering costs nothing and closes the body-stall gap a headers-only completion would leave under the
    /// caller's token alone. <paramref name="content"/>, when supplied, is the request body (JSON for
    /// <c>operations</c>, octet bytes for a file <c>PUT</c>).
    /// </summary>
    private async Task<HttpResponseMessage> SendDirectAsync(
        HttpMethod method,
        string relativeUri,
        HttpContent? content,
        string sessionId,
        CancellationToken ct
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var request = new HttpRequestMessage(method, ResolveRequestUri(relativeUri));
        if (content is not null)
        {
            request.Content = content;
        }

        StampAuthHeaders(request);
        _ = request.Headers.TryAddWithoutValidation(SessionIdHeader, sessionId);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_options.TransportTimeout);
        try
        {
            // ResponseContentRead: SendAsync completes only once the entire body is buffered, so the
            // linked transport-timeout token covers the body, not just the headers. The caller then reads
            // that in-memory buffer synchronously (no further network), so it is safe for this method's
            // `timeoutCts` to be disposed on return. `request` is intentionally not wrapped in `using`:
            // disposing it here could tear down an in-flight request content stream, and its managed
            // content is released by GC (the PUT body is disposed by the caller's own `using`).
            return await Transport
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            request.Dispose();
            throw new SandboxException(
                SandboxErrorKind.TransportTimeout,
                $"Sandbox gateway request to '{relativeUri}' did not complete within the configured "
                    + $"transport timeout ({_options.TransportTimeout})."
            );
        }
        catch (HttpRequestException ex)
        {
            request.Dispose();
            throw new SandboxException(
                SandboxErrorKind.TransportTimeout,
                $"Could not reach the sandbox gateway at '{_options.ServerAddress}'.",
                statusCode: null,
                ex
            );
        }
    }

    /// <summary>
    /// Classifies a non-success direct-API (ADR 0031) response into a <see cref="SandboxException"/>.
    /// For a NON-auth response it reads the gateway's small, stable
    /// <c>{ error, code, error_code, retryable }</c> body and maps the fixed-string <c>error_code</c>
    /// (a closed, gateway-defined vocabulary — safe to surface, like a JSON-RPC <c>code</c>) to a
    /// <see cref="SandboxErrorKind"/>; the human <c>error</c> message is never echoed. A <c>401</c>/
    /// <c>403</c> is classified as <see cref="SandboxErrorKind.Authorization"/> WITHOUT reading the
    /// body (an auth rejection is the response most likely to echo credential material — the same
    /// invariant <see cref="MapErrorResponse"/> holds; the gateway's own <c>403</c> error codes like
    /// <c>mount_read_only</c> are therefore indistinguishable here, which is acceptable because the
    /// write path only ever targets the writable workspace). A <c>3xx</c> is refused, never followed.
    /// The error body is read under a FRESH transport-timeout token, deliberately NOT the caller's: an
    /// error response has already arrived, so a caller cancelling mid-parse must not be able to erase the
    /// gateway's real classification. This method ALWAYS returns a <see cref="SandboxException"/> and never
    /// throws <see cref="OperationCanceledException"/>.
    /// </summary>
    private async Task<SandboxException> MapDirectErrorAsync(HttpResponseMessage response, string operation, string sessionId)
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

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new SandboxException(SandboxErrorKind.Authorization, $"Sandbox gateway returned {statusCode} for {operation}.", statusCode);
        }

        string? errorCode = null;
        try
        {
            // Bound the body read with a FRESH transport-timeout CTS, NOT linked to the caller's token: the
            // error response has already been received, so a caller cancelling now must not turn this into
            // an OperationCanceledException that erases the gateway's real classification (NotFound/Conflict/
            // …). The fresh deadline still prevents an unbounded wait on a slow/streamed error body.
            using var bodyCts = new CancellationTokenSource(_options.TransportTimeout);
            var error = await response.Content.ReadFromJsonAsync<GatewayErrorDto>(SandboxJson.RestOptions, bodyCts.Token).ConfigureAwait(false);
            errorCode = error?.ErrorCode;
        }
        catch (Exception ex) when (ex is JsonException or OperationCanceledException)
        {
            // No usable machine-readable body (malformed, or the bounded read elapsed) — fall back to
            // status-only classification below. Never propagate OCE: an already-received error must always
            // classify to a SandboxException.
        }

        // A definitive "this session is gone" drops the stale sessionId→mountId cache entry so a later
        // call re-resolves the mount (or fails cleanly) instead of replaying a dead mapping. Skipped when
        // the body did not parse (errorCode null) — best-effort, acceptable.
        if (string.Equals(errorCode, "session_not_found", StringComparison.Ordinal))
        {
            EvictWorkspaceMountId(sessionId);
        }

        var kind = MapDirectErrorKind(response.StatusCode, errorCode);
        var codeSuffix = string.IsNullOrEmpty(errorCode) ? string.Empty : $" (error_code {errorCode})";
        return new SandboxException(kind, $"Sandbox gateway returned {statusCode} for {operation}{codeSuffix}.", statusCode)
        {
            ErrorCode = errorCode,
        };
    }

    /// <summary>
    /// Maps a direct-API <c>error_code</c> (preferred) or, absent one, the raw status to a
    /// <see cref="SandboxErrorKind"/>. The operation terminal states <c>timed_out</c>/
    /// <c>output_limit_exceeded</c> are NOT handled here — they arrive as a <c>200</c> status body,
    /// not an HTTP error, and are classified by the command poll loop.
    /// </summary>
    private static SandboxErrorKind MapDirectErrorKind(HttpStatusCode status, string? errorCode) =>
        errorCode switch
        {
            "session_not_found" or "mount_not_found" or "operation_not_found" or "path_not_found" => SandboxErrorKind.NotFound,
            "idempotency_conflict" or "operation_running" or "target_locked" => SandboxErrorKind.Conflict,
            "workspace_required" => SandboxErrorKind.WorkspaceRequired,
            "operation_api_unavailable"
            or "operation_probe_failed"
            or "operation_concurrency_limit"
            or "operation_capacity_exhausted"
            or "too_many_concurrent_requests"
            or "sandbox_busy" => SandboxErrorKind.Unavailable,
            _ => status switch
            {
                HttpStatusCode.NotFound => SandboxErrorKind.NotFound,
                HttpStatusCode.Conflict => SandboxErrorKind.Conflict,
                _ => SandboxErrorKind.Protocol,
            },
        };

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
}
