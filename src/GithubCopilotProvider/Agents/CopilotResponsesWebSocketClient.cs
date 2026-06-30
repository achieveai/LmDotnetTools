using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Auth;
using AchieveAi.LmDotnetTools.LmCore.Http;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Agents;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.GithubCopilotProvider.Agents;

/// <summary>
///     <see cref="IOpenAiResponsesClient"/> implementation that drives the GitHub Copilot Responses
///     API over a persistent WebSocket (<c>GET /responses</c>, HTTP 101). Each
///     <see cref="StreamResponseAsync"/> call sends one <c>response.create</c> text frame and yields
///     the server's event frames until the turn's terminal lifecycle event.
/// </summary>
/// <remarks>
///     Multi-turn chaining is automatic: the <c>response.completed.response.id</c> of one turn is
///     fed as <c>previous_response_id</c> on the next, matching the Copilot CLI's behaviour. The
///     socket stays open across turns. Server event frames are parsed with the shared
///     <see cref="ResponseEventParser"/>, so the same <see cref="OpenAiResponsesAgent"/> mapping is
///     reused as for the SSE transport.
/// </remarks>
public sealed class CopilotResponsesWebSocketClient : IOpenAiResponsesClient, IAsyncDisposable
{
    private static readonly JsonSerializerOptions s_serializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Uri _endpoint;
    private readonly ICopilotTokenProvider _tokenProvider;
    private readonly CopilotSessionContext _session;
    private readonly CopilotOptions _options;
    private readonly Func<ICopilotResponsesSocket> _socketFactory;
    private readonly ILogger _logger;
    private readonly RetryOptions _retryOptions;
    private readonly SemaphoreSlim _turnGate = new(1, 1);

    private ICopilotResponsesSocket? _socket;
    private string? _previousResponseId;
    private bool _disposed;

    /// <summary>Creates a WebSocket Responses client.</summary>
    /// <param name="endpoint">
    ///     WebSocket endpoint (e.g. <c>wss://api.enterprise.githubcopilot.com/responses</c>).
    /// </param>
    /// <param name="tokenProvider">Bearer-token source for the upgrade request.</param>
    /// <param name="session">Shared client/machine tracking ids.</param>
    /// <param name="options">Copilot header options.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="socketFactory">
    ///     Optional socket factory (override in tests). Defaults to a real <see cref="ClientWebSocket"/>.
    /// </param>
    /// <param name="retryOptions">
    ///     Optional connect-retry configuration (bounds the pre-turn connect-retry loop). Defaults to
    ///     <see cref="RetryOptions.Default"/>.
    /// </param>
    public CopilotResponsesWebSocketClient(
        Uri endpoint,
        ICopilotTokenProvider tokenProvider,
        CopilotSessionContext session,
        CopilotOptions? options = null,
        ILogger? logger = null,
        Func<ICopilotResponsesSocket>? socketFactory = null,
        RetryOptions? retryOptions = null
    )
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _options = options ?? new CopilotOptions();
        _logger = logger ?? NullLogger.Instance;
        _socketFactory = socketFactory ?? (() => new ClientWebSocketResponsesSocket());
        _retryOptions = retryOptions ?? RetryOptions.Default;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ResponseEvent> StreamResponseAsync(
        ResponseCreateRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var socket = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

            var frame = request with
            {
                Type = ResponseEventTypes.ClientResponseCreate,
                Stream = null, // WebSocket frames are inherently streamed; omit the HTTP-only flag.
                Initiator = request.Initiator ?? _options.DefaultInitiator,
                Store = request.Store ?? false,
                PreviousResponseId = request.PreviousResponseId ?? _previousResponseId,
            };

            var json = JsonSerializer.Serialize(frame, s_serializerOptions);
            _logger.LogDebug(
                "WS response.create model={Model} inputItems={Count} previousResponseId={Prev}",
                frame.Model,
                frame.Input.Count,
                frame.PreviousResponseId is null ? "(none)" : "(set)"
            );

            await socket.SendTextAsync(json, cancellationToken).ConfigureAwait(false);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // A null is a genuine peer Close frame. Reaching here means the socket closed BEFORE a
                // terminal response.completed/response.failed event, i.e. the turn was truncated. Surface
                // it as an error instead of silently ending the stream as though the turn had completed
                // (which would also wrongly leave _previousResponseId chained to a partial turn).
                var text =
                    await socket.ReceiveTextAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new IOException(
                        "WebSocket closed before turn completion (no response.completed/response.failed received)."
                    );

                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var ev = ResponseEventParser.Parse(text);

                if (
                    ev is ResponseLifecycleEvent lifecycle
                    && string.Equals(lifecycle.Type, ResponseEventTypes.ResponseCompleted, StringComparison.Ordinal)
                )
                {
                    _previousResponseId = TryGetResponseId(lifecycle.Response) ?? _previousResponseId;
                }

                yield return ev;

                if (ev.Type is ResponseEventTypes.ResponseCompleted or ResponseEventTypes.ResponseFailed)
                {
                    yield break;
                }
            }
        }
        finally
        {
            _ = _turnGate.Release();
        }
    }

    private async Task<ICopilotResponsesSocket> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_socket is { IsConnected: true })
        {
            return _socket;
        }

        // A previously-stored socket that is no longer connected (e.g. after a faulted/truncated turn)
        // must be disposed before we replace it, otherwise its underlying connection leaks until
        // finalization. Null it out first so a failed reconnect never leaves a half-open socket stored.
        if (_socket is not null)
        {
            var stale = _socket;
            _socket = null;
            await DisposeSocketSafelyAsync(stale).ConfigureAwait(false);
        }

        // Pre-turn connect-retry: this runs ONLY before the first response.create frame is sent, so a
        // retry is safe (idempotent). Each attempt refreshes the token + headers (fresh x-interaction-id)
        // and creates a NEW socket; a failed socket is disposed locally and _socket is assigned ONLY
        // after a successful connect, so a half-open socket is never observed by a turn.
        var attempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var token = await _tokenProvider.GetTokenAsync(cancellationToken).ConfigureAwait(false);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = $"Bearer {token}",
            };
            foreach (var header in CopilotRequestHeaders.Build(_options, _session, Guid.NewGuid().ToString()))
            {
                headers[header.Key] = header.Value;
            }

            var socket = _socketFactory();
            try
            {
                await socket.ConnectAsync(_endpoint, headers, cancellationToken).ConfigureAwait(false);
                _socket = socket;
                return socket;
            }
            catch (Exception ex) when (attempt < _retryOptions.MaxRetries && IsRetryableConnect(ex))
            {
                attempt++;
                var delay = _retryOptions.CalculateDelay(attempt);
                _logger.LogWarning(
                    "WebSocket connect failed (attempt {Attempt}/{MaxRetries}), retrying in {Delay}ms: {Error}",
                    attempt,
                    _retryOptions.MaxRetries + 1,
                    delay.TotalMilliseconds,
                    ex.Message
                );

                await DisposeSocketSafelyAsync(socket).ConfigureAwait(false);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Non-retryable, retries exhausted, or cancelled: dispose the failed socket and surface.
                await DisposeSocketSafelyAsync(socket).ConfigureAwait(false);
                throw;
            }
        }
    }

    /// <summary>
    ///     Classifies a connect/upgrade failure as transient (retryable). Retries when a
    ///     <see cref="WebSocketException"/> wraps a transient <see cref="SocketException"/>
    ///     (connection refused / timed out / host unreachable / DNS hiccup) or an inner
    ///     <see cref="HttpRequestException"/> whose status is a retryable 5xx/429. Auth/permanent
    ///     failures (401/403/400/404, DNS name-resolution failure) are NOT retried.
    /// </summary>
    private static bool IsRetryableConnect(Exception exception)
    {
        if (exception is not WebSocketException wsEx)
        {
            return false;
        }

        return wsEx.InnerException switch
        {
            // Transient transport-layer failures. TryAgain (WSATRY_AGAIN) is the non-authoritative
            // "DNS hiccup, retry" case; HostUnreachable/NetworkUnreachable are transient routing
            // failures. HostNotFound (WSAHOST_NOT_FOUND, authoritative "no such host") is NOT here —
            // it is a permanent name-resolution failure (see the doc comment above).
            SocketException socketEx => socketEx.SocketErrorCode
                is SocketError.ConnectionRefused
                    or SocketError.TimedOut
                    or SocketError.HostUnreachable
                    or SocketError.NetworkUnreachable
                    or SocketError.TryAgain,
            HttpRequestException { StatusCode: { } status } => HttpRetryHelper.IsRetryableStatusCode(status),
            _ => false,
        };
    }

    private static async Task DisposeSocketSafelyAsync(ICopilotResponsesSocket socket)
    {
        try
        {
            await socket.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // Best-effort cleanup of a failed socket — never mask the original connect failure.
        }
    }

    private static string? TryGetResponseId(JsonElement response)
    {
        if (
            response.ValueKind == JsonValueKind.Object
            && response.TryGetProperty("id", out var idEl)
            && idEl.ValueKind == JsonValueKind.String
        )
        {
            return idEl.GetString();
        }

        return null;
    }

    /// <summary>Asynchronously closes the socket and releases the turn gate. Preferred over <see cref="Dispose"/>.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_socket is not null)
            {
                await _socket.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _turnGate.Dispose();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            // Bounded best-effort close — never block the disposing thread indefinitely on the
            // socket's network round-trip. The turn gate is always released in the finally.
            _ = _socket?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is AggregateException or WebSocketException or OperationCanceledException)
        {
            // Best-effort: the close raced, timed out, or the socket was already faulted.
        }
        finally
        {
            _turnGate.Dispose();
        }
    }
}

/// <summary>
///     Minimal text-framed WebSocket abstraction used by <see cref="CopilotResponsesWebSocketClient"/>,
///     allowing an in-memory fake to be substituted in tests.
/// </summary>
public interface ICopilotResponsesSocket : IAsyncDisposable
{
    /// <summary>True once the socket is open.</summary>
    bool IsConnected { get; }

    /// <summary>Opens the connection, applying <paramref name="headers"/> to the upgrade request.</summary>
    Task ConnectAsync(Uri endpoint, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken);

    /// <summary>Sends a single UTF-8 text frame.</summary>
    Task SendTextAsync(string text, CancellationToken cancellationToken);

    /// <summary>
    ///     Receives the next complete text message (reassembling fragments). Returns null ONLY on a
    ///     graceful peer Close frame; an abnormal/abrupt close surfaces as a thrown
    ///     <see cref="WebSocketException"/> rather than null, so the caller can distinguish a clean
    ///     close from a fault.
    /// </summary>
    Task<string?> ReceiveTextAsync(CancellationToken cancellationToken);
}

/// <summary>
///     Default <see cref="ICopilotResponsesSocket"/> backed by a real <see cref="ClientWebSocket"/>.
/// </summary>
public sealed class ClientWebSocketResponsesSocket : ICopilotResponsesSocket
{
    private readonly ClientWebSocket _socket = new();

    /// <inheritdoc />
    public bool IsConnected => _socket.State == WebSocketState.Open;

    /// <inheritdoc />
    public async Task ConnectAsync(
        Uri endpoint,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(headers);
        foreach (var header in headers)
        {
            _socket.Options.SetRequestHeader(header.Key, header.Value);
        }

        await _socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task SendTextAsync(string text, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<string?> ReceiveTextAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();

        while (true)
        {
            // A WebSocketException (abnormal/abrupt close, protocol error) is a FAULT, not a graceful
            // close — let it propagate so the caller can surface it instead of silently truncating the
            // turn. A null is reserved for a genuine peer Close frame below.
            var result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            message.Write(buffer, 0, result.Count);

            if (result.EndOfMessage)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(message.ToArray());
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket
                    .CloseAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (WebSocketException)
        {
            // Best-effort close.
        }
        finally
        {
            _socket.Dispose();
        }
    }
}
