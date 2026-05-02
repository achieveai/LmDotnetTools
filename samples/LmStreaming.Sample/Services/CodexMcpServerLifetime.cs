using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.McpServer.AspNetCore;

[assembly: InternalsVisibleTo("LmStreaming.Sample.Tests")]

namespace LmStreaming.Sample.Services;

/// <summary>
/// Lazy lifetime wrapper for the codex MCP server. The underlying
/// <see cref="McpFunctionProviderServer"/> is registered as a singleton (without an
/// <see cref="Microsoft.Extensions.Hosting.IHostedService"/> registration), and we only
/// call <c>StartAsync</c> on it the first time a codex agent is created. Subsequent
/// callers await the cached <see cref="Task{T}"/> from the same <see cref="Lazy{T}"/>.
/// </summary>
public sealed class CodexMcpServerLifetime : IAsyncDisposable
{
    private readonly McpFunctionProviderServer? _server;
    private readonly Func<Task<string>> _startupDelegate;
    private readonly ILogger<CodexMcpServerLifetime> _logger;
    private readonly Lazy<Task<string>> _start;

    public CodexMcpServerLifetime(
        McpFunctionProviderServer server,
        ILogger<CodexMcpServerLifetime> logger)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _startupDelegate = StartServerAsync;
        _start = new Lazy<Task<string>>(_startupDelegate, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    // Test-only constructor: supplies a custom startup delegate so tests can verify the
    // lazy start-once / cached-failure semantics without spinning up a real MCP server.
    internal CodexMcpServerLifetime(
        Func<Task<string>> startupDelegate,
        ILogger<CodexMcpServerLifetime> logger)
    {
        _server = null;
        _startupDelegate = startupDelegate ?? throw new ArgumentNullException(nameof(startupDelegate));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _start = new Lazy<Task<string>>(_startupDelegate, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// Ensures the codex MCP server has started and returns its endpoint URL. The first
    /// caller starts the server; subsequent callers await the cached task. If the start
    /// fails, the failure is cached — every caller observes the same exception (recovery
    /// requires an app restart, by design).
    /// </summary>
    public Task<string> EnsureStartedAsync(CancellationToken ct = default)
    {
        _ = ct;
        return _start.Value;
    }

    private async Task<string> StartServerAsync()
    {
        _logger.LogInformation("Starting codex MCP server on demand");
        await _server!.StartAsync().ConfigureAwait(false);
        var endpoint = _server.McpEndpointUrl
            ?? throw new InvalidOperationException("MCP server started without an endpoint URL.");
        _logger.LogInformation("Codex MCP server started. Endpoint: {Endpoint}", endpoint);
        return endpoint;
    }

    public async ValueTask DisposeAsync()
    {
        if (_start.IsValueCreated)
        {
            try
            {
                await _start.Value.ConfigureAwait(false);
            }
            catch
            {
                // Start failed — nothing to dispose beyond what the server already
                // cleaned up internally. Don't propagate during disposal.
            }
        }

        if (_server != null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
        }
    }
}
