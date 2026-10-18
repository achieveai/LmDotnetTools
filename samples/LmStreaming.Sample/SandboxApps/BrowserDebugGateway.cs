using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.Sandbox;
using ModelContextProtocol;

namespace LmStreaming.Sample.SandboxApps;

/// <summary>The credential-scoped gateway boundary; target and browser ownership stay in the host.</summary>
public interface IBrowserDebugGateway
{
    Task<bool> SupportsBrowserAsync(CancellationToken ct);
    Task<JsonElement> CallAsync(
        string browserId,
        string toolkit,
        string? toolName,
        JsonElement? arguments,
        CancellationToken ct
    );

    /// <summary>Destroys the browser; idempotent, so closing an already-closed browser succeeds.</summary>
    Task CloseAsync(string browserId, CancellationToken ct);
}

/// <summary>
/// Drives leased browsers through the SDK's MCP relay (<see cref="SandboxClient.ConnectBrowserMcpAsync"/>), one MCP
/// session per browser and toolkit, reused across calls. A result keeps the MCP shape the provider reads
/// (<c>content</c>, <c>isError</c>, <c>structuredContent</c>) and gains <c>_meta</c> with the browser id and the
/// instance the relay reported, so a replaced browser is caught as before. A JSON-RPC error from the browser's MCP
/// server is a tool error the agent can fix; any other failure is a <see cref="SandboxException"/>.
/// </summary>
public sealed class BrowserDebugGateway(SandboxClient client) : IBrowserDebugGateway, IAsyncDisposable
{
    private readonly ConcurrentDictionary<
        (string BrowserId, BrowserMcpServer Server),
        Task<BrowserMcpSession>
    > _sessions = new();

    public Task<bool> SupportsBrowserAsync(CancellationToken ct) => client.SupportsBrowserAsync(ct);

    public async Task<JsonElement> CallAsync(
        string browserId,
        string toolkit,
        string? toolName,
        JsonElement? arguments,
        CancellationToken ct
    )
    {
        var server = toolkit switch
        {
            "playwright" => BrowserMcpServer.Playwright,
            "devtools" => BrowserMcpServer.DevTools,
            _ => throw new ArgumentException("Toolkit must be playwright or devtools.", nameof(toolkit)),
        };
        var session = await SessionAsync(browserId, server, ct);
        JsonObject result;
        try
        {
            result = toolName is null
                ? Text(
                    JsonSerializer.Serialize(
                        (await session.Client.ListToolsAsync(cancellationToken: ct)).Select(t => new
                        {
                            name = t.Name,
                            description = t.Description,
                        })
                    ),
                    isError: false
                )
                : JsonSerializer
                    .SerializeToNode(
                        await session.Client.CallToolAsync(
                            toolName,
                            arguments?.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value),
                            cancellationToken: ct
                        ),
                        McpJsonUtilities.DefaultOptions
                    )!
                    .AsObject();
        }
        catch (McpProtocolException ex) when (!session.IsBrowserReplaced)
        {
            result = Text(ex.Message, isError: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await DropAsync(browserId, server);
            if (session.IsBrowserReplaced)
            {
                // The replacement does not know this MCP session; report the new instance so the provider
                // invalidates the handle instead of reporting an outage.
                return WithMeta(Text("The browser was replaced.", isError: true), browserId, session);
            }
            throw AsSandboxException(ex);
        }

        if (session.IsBrowserReplaced)
        {
            await DropAsync(browserId, server);
        }
        return WithMeta(result, browserId, session);
    }

    /// <summary>Ends this browser's MCP sessions, then destroys the browser (idempotent).</summary>
    public async Task CloseAsync(string browserId, CancellationToken ct)
    {
        foreach (var server in Enum.GetValues<BrowserMcpServer>())
        {
            await DropAsync(browserId, server);
        }
        await client.CloseBrowserAsync(browserId, ct);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var key in _sessions.Keys.ToArray())
        {
            await DropAsync(key.BrowserId, key.Server);
        }
        client.Dispose();
    }

    private async Task<BrowserMcpSession> SessionAsync(string browserId, BrowserMcpServer server, CancellationToken ct)
    {
        var key = (browserId, server);
        var connect = _sessions.GetOrAdd(key, _ => client.ConnectBrowserMcpAsync(browserId, server, ct));
        try
        {
            return await connect;
        }
        catch
        {
            // A failed or cancelled connect is not cached: the next call connects again.
            _ = _sessions.TryRemove(
                new KeyValuePair<(string, BrowserMcpServer), Task<BrowserMcpSession>>(key, connect)
            );
            throw;
        }
    }

    private async Task DropAsync(string browserId, BrowserMcpServer server)
    {
        if (!_sessions.TryRemove((browserId, server), out var connect))
        {
            return;
        }
        try
        {
            await (await connect).DisposeAsync();
        }
        catch (Exception)
        {
            // The session never connected or is already broken; there is nothing left to end.
        }
    }

    private static JsonObject Text(string text, bool isError) =>
        new()
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = isError,
        };

    private static JsonElement WithMeta(JsonObject result, string browserId, BrowserMcpSession session)
    {
        var meta = result["_meta"] as JsonObject ?? [];
        meta["sbx/browser_id"] = browserId;
        meta["sbx/browser_instance"] = session.LatestBrowserInstance;
        result["_meta"] = meta;
        return JsonSerializer.SerializeToElement(result);
    }

    private static SandboxException AsSandboxException(Exception ex)
    {
        for (var inner = ex; inner is not null; inner = inner.InnerException)
        {
            if (inner is SandboxException typed)
            {
                return typed;
            }
        }
        return ex switch
        {
            OperationCanceledException => new SandboxException(
                SandboxErrorKind.TransportTimeout,
                "The browser relay call timed out.",
                statusCode: null,
                ex
            ),
            McpException or JsonException => new SandboxException(
                SandboxErrorKind.Protocol,
                "The browser relay returned an invalid MCP response.",
                statusCode: null,
                ex
            ),
            _ => new SandboxException(
                SandboxErrorKind.Unavailable,
                "The browser relay is unreachable.",
                statusCode: null,
                ex
            ),
        };
    }
}
