using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>
/// Chrome's <c>/json/version</c> as served through the gateway relay
/// (<see cref="SandboxClient.GetBrowserVersionAsync"/>).
/// </summary>
/// <param name="BrowserInstance">The serving instance (<see cref="SandboxClient.BrowserInstanceHeader"/>), when sent.</param>
/// <param name="Browser">Chrome's product string, for example <c>Chrome/153.0.0.0</c>.</param>
/// <param name="ProtocolVersion">The CDP protocol version.</param>
/// <param name="DebuggerPath">
/// The browser-level target below <c>…/cdp</c> (for example <c>/devtools/browser/&lt;id&gt;</c>), for
/// <see cref="SandboxClient.ConnectBrowserCdpAsync"/>.
/// </param>
/// <param name="Raw">The whole response object.</param>
public sealed record BrowserCdpVersion(
    string? BrowserInstance,
    string? Browser,
    string? ProtocolVersion,
    string? DebuggerPath,
    JsonElement Raw
);

/// <summary>
/// A CDP WebSocket opened through the gateway relay by <see cref="SandboxClient.ConnectBrowserCdpAsync"/>.
/// <see cref="Socket"/> is the raw socket for any CDP client; <see cref="SendTextAsync"/> and
/// <see cref="ReceiveTextAsync"/> are a minimal message-level layer that turns the gateway's
/// "browser restarted" close into a typed signal.
/// </summary>
public sealed class BrowserCdpConnection : IDisposable
{
    /// <summary>The close status the gateway sends when the browser behind a socket is destroyed.</summary>
    public const WebSocketCloseStatus RestartCloseStatus = WebSocketCloseStatus.InternalServerError;

    /// <summary>The close reason paired with <see cref="RestartCloseStatus"/>.</summary>
    public const string RestartCloseReason = "browser_restarted";

    internal BrowserCdpConnection(string browserId, string? browserInstance, WebSocket socket)
    {
        BrowserId = browserId;
        BrowserInstance = browserInstance;
        Socket = socket;
    }

    /// <summary>The browser id this socket is bound to.</summary>
    public string BrowserId { get; }

    /// <summary>The instance that accepted the upgrade (<see cref="SandboxClient.BrowserInstanceHeader"/>).</summary>
    public string? BrowserInstance { get; }

    /// <summary>The underlying socket.</summary>
    public WebSocket Socket { get; }

    /// <summary>
    /// True when <paramref name="socket"/> was closed because its browser was destroyed (close code
    /// <c>1011</c>, reason <c>browser_restarted</c>). Reconnect: the next request binds a fresh browser.
    /// </summary>
    public static bool IsBrowserRestart(WebSocket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        return socket.CloseStatus == RestartCloseStatus && socket.CloseStatusDescription == RestartCloseReason;
    }

    /// <summary>Sends one text message (a CDP command as JSON).</summary>
    public Task SendTextAsync(string message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, ct);
    }

    /// <summary>
    /// Receives the next whole message as text. Returns <c>null</c> when the peer closed the socket
    /// normally (for example Chrome closing a target's socket).
    /// </summary>
    /// <exception cref="SandboxException">
    /// <see cref="SandboxErrorKind.BrowserRestarted"/> (<c>ErrorCode</c> <c>browser_restarted</c>, retryable)
    /// when the gateway closed the socket because the browser was destroyed; <see cref="SandboxErrorKind.Protocol"/>
    /// for a message over the relay's 256 MiB cap.
    /// </exception>
    public async Task<string?> ReceiveTextAsync(CancellationToken ct = default)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using var message = new MemoryStream();
            while (true)
            {
                var result = await Socket.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (IsBrowserRestart(Socket))
                    {
                        throw new SandboxException(
                            SandboxErrorKind.BrowserRestarted,
                            $"Browser '{BrowserId}' was restarted; its CDP socket closed. Reconnect to get a fresh browser."
                        )
                        {
                            ErrorCode = RestartCloseReason,
                            Retryable = true,
                        };
                    }

                    return null;
                }

                if (message.Length + result.Count > SandboxClient.MaxCdpMessageBytes)
                {
                    throw new SandboxException(
                        SandboxErrorKind.Protocol,
                        $"A CDP message exceeded the {SandboxClient.MaxCdpMessageBytes}-byte relay cap."
                    );
                }

                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Sends a normal close. Does not wait for the peer's close frame: the gateway relay ends the
    /// connection on a client close without echoing one.
    /// </summary>
    public Task CloseAsync(CancellationToken ct = default) =>
        Socket.State is WebSocketState.Open or WebSocketState.CloseReceived
            ? Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct)
            : Task.CompletedTask;

    /// <inheritdoc />
    public void Dispose() => Socket.Dispose();
}
