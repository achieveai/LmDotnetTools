using ModelContextProtocol.Client;

namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>The MCP servers bundled in every leased browser.</summary>
public enum BrowserMcpServer
{
    /// <summary>Playwright MCP (<c>…/mcp/playwright</c>).</summary>
    Playwright,

    /// <summary>Chrome DevTools MCP (<c>…/mcp/devtools</c>).</summary>
    DevTools,
}

/// <summary>
/// An MCP client connected to a leased browser's MCP server through the gateway relay
/// (<see cref="SandboxClient.ConnectBrowserMcpAsync"/>). Disposing it ends the MCP session; the browser
/// itself stays bound until idle or <see cref="SandboxClient.CloseBrowserAsync"/>.
/// </summary>
public sealed class BrowserMcpSession : IAsyncDisposable
{
    private readonly SandboxClient.BrowserRelayHandler _relay;

    internal BrowserMcpSession(
        string browserId,
        BrowserMcpServer server,
        McpClient client,
        SandboxClient.BrowserRelayHandler relay
    )
    {
        BrowserId = browserId;
        Server = server;
        Client = client;
        _relay = relay;
    }

    /// <summary>The browser id this session is bound to.</summary>
    public string BrowserId { get; }

    /// <summary>Which bundled server this session talks to.</summary>
    public BrowserMcpServer Server { get; }

    /// <summary>The MCP client: list and call tools with it.</summary>
    public McpClient Client { get; }

    /// <summary>The instance that served the MCP <c>initialize</c> (<see cref="SandboxClient.BrowserInstanceHeader"/>).</summary>
    public string? BrowserInstance => _relay.BrowserInstance;

    /// <summary>The instance that served the most recent relay response.</summary>
    public string? LatestBrowserInstance => _relay.LatestBrowserInstance;

    /// <summary>
    /// True once a relay response came from a different instance than <see cref="BrowserInstance"/>: the
    /// browser was replaced, so this MCP session and all page state are gone. Connect again.
    /// </summary>
    public bool IsBrowserReplaced =>
        BrowserInstance is { } first && LatestBrowserInstance is { } latest && first != latest;

    /// <inheritdoc />
    public ValueTask DisposeAsync() => Client.DisposeAsync();
}
