using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.McpServer.AspNetCore.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.McpServer.AspNetCore;

/// <summary>
/// Disposable wrapper for an AspNetCore MCP server that exposes IFunctionProvider instances as MCP tools.
/// Supports dynamic port allocation and proper cleanup.
/// </summary>
public sealed class McpFunctionProviderServer : IAsyncDisposable
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

    private McpFunctionProviderServer(WebApplication app)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
    }

    /// <summary>
    /// Creates a new MCP server with the specified function providers.
    /// The server will use a dynamically allocated port.
    /// </summary>
    /// <param name="functionProviders">The function providers to register</param>
    /// <param name="configureLogging">Optional action to configure logging</param>
    /// <param name="configureServices">Optional action to configure additional services</param>
    /// <returns>A new MCP server instance</returns>
    public static McpFunctionProviderServer Create(
        IEnumerable<IFunctionProvider> functionProviders,
        Action<ILoggingBuilder>? configureLogging = null,
        Action<IServiceCollection>? configureServices = null)
    {
        ArgumentNullException.ThrowIfNull(functionProviders);

        var builder = WebApplication.CreateBuilder();

        // Configure Kestrel to listen on a dynamic port (0 = OS will assign a free port)
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(System.Net.IPAddress.Loopback, 0); // Port 0 = dynamic allocation
        });

        // Configure logging
        if (configureLogging != null)
        {
            builder.Logging.ClearProviders();
            configureLogging(builder.Logging);
        }
        else
        {
            // Default: console logging with Warning level to reduce noise
            builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Warning);
        }

        // Register function providers
        foreach (var provider in functionProviders)
        {
            builder.Services.AddFunctionProvider(provider);
        }

        // Add MCP server with function provider support
        builder.Services.AddMcpServerFromFunctionProviders();

        // Add CORS for development/testing
        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                policy.AllowAnyOrigin()
                      .AllowAnyMethod()
                      .AllowAnyHeader();
            });
        });

        // Allow additional service configuration
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();

        // Enable CORS
        app.UseCors();

        // Map MCP endpoints
        app.MapMcpFunctionProviders();

        return new McpFunctionProviderServer(app);
    }

    /// <summary>
    /// Starts the MCP server asynchronously.
    /// The server will run in the background until disposed.
    /// </summary>
    /// <returns>A task that completes when the server has started</returns>
    public async Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

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
            if (DateTime.UtcNow - startTime > timeout)
            {
                throw new TimeoutException("Server failed to start within the timeout period");
            }

            await Task.Delay(100);
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
    public async Task StopAsync()
    {
        if (!_disposed && _runTask != null)
        {
            _shutdownCts.Cancel();
            try
            {
                await _runTask;
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
