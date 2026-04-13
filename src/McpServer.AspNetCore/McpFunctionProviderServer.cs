using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AchieveAi.LmDotnetTools.McpServer.AspNetCore;

/// <summary>
/// MCP server that exposes IFunctionProvider instances as MCP tools.
/// Implements IHostedService for integration with AspNetCore host lifecycle.
/// Register as singleton via AddMcpFunctionProviderServer() for injection across all scopes.
/// </summary>
public sealed class McpFunctionProviderServer : IHostedService, IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly CancellationTokenSource _shutdownCts = new();
    private Task? _runTask;
    private bool _disposed;

    /// <summary>
    /// Gets the port the server is listening on.
    /// Returns null if the server hasn't started yet or if the port couldn't be determined.
    /// </summary>
    public int? Port { get; private set; }

    /// <summary>
    /// Gets the base URL of the MCP server (e.g., "http://localhost:5123")
    /// </summary>
    public string? BaseUrl => Port.HasValue ? $"http://localhost:{Port}" : null;

    /// <summary>
    /// Gets the MCP endpoint URL (e.g., "http://localhost:5123/mcp")
    /// </summary>
    public string? McpEndpointUrl => BaseUrl != null ? $"{BaseUrl}/mcp" : null;

    /// <summary>
    /// Creates a new MCP server with the specified WebApplication.
    /// Use AddMcpFunctionProviderServer() extension method for DI registration.
    /// </summary>
    /// <param name="app">The configured WebApplication instance</param>
    public McpFunctionProviderServer(WebApplication app)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
    }

    /// <summary>
    /// Starts the MCP server asynchronously.
    /// The server will run in the background until stopped or disposed.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A task that completes when the server has started</returns>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Link the provided cancellation token with our shutdown token
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);

        // Start the server in the background
        _runTask = _app.RunAsync(_shutdownCts.Token);

        // Wait for the server to start and get the assigned port
        var server = _app.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>();

        // Wait for addresses to be available (with timeout)
        var timeout = TimeSpan.FromSeconds(5);
        var startTime = DateTime.UtcNow;

        while (addresses?.Addresses == null || addresses.Addresses.Count == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (DateTime.UtcNow - startTime > timeout)
            {
                throw new TimeoutException("Server failed to start within the timeout period");
            }

            await Task.Delay(100, cancellationToken);
            addresses = server.Features.Get<IServerAddressesFeature>();
        }

        // Extract the port from the first address
        var address = addresses.Addresses.First();
        if (Uri.TryCreate(address, UriKind.Absolute, out var uri))
        {
            Port = uri.Port;
        }
    }

    /// <summary>
    /// Stops the MCP server gracefully.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_disposed && _runTask != null)
        {
            await _shutdownCts.CancelAsync();
            try
            {
                await _runTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Expected when shutting down
            }
        }
    }

    /// <summary>
    /// Disposes the server asynchronously, stopping it if it's running.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await StopAsync();
        _shutdownCts.Dispose();
        await _app.DisposeAsync();
    }
}
