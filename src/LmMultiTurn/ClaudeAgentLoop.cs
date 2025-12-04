using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Agents;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmMultiTurn;

/// <summary>
/// Multi-turn agent implementation using Claude Agent SDK CLI with MCP tools.
/// Thread-safe for concurrent input via SendAsync.
/// Supports multiple independent output subscribers via SubscribeAsync.
/// </summary>
/// <remarks>
/// This class provides the same interface as MultiTurnAgentLoop but uses ClaudeAgentSdkAgent
/// (which wraps the claude-agent-sdk CLI) instead of a provider agent with middleware stack.
/// Tools are exposed via MCP servers configured externally.
/// </remarks>
public sealed class ClaudeAgentLoop : MultiTurnAgentBase
{
    private readonly ClaudeAgentSdkOptions _claudeOptions;
    private readonly Dictionary<string, McpServerConfig> _mcpServers;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly Func<ClaudeAgentSdkOptions, ILogger?, ClaudeAgentSdkClient>? _clientFactory;
    private readonly SemaphoreSlim _restartLock = new(1, 1);

    private ClaudeAgentSdkAgent? _agent;
    private IClaudeAgentSdkClient? _client;

    /// <summary>
    /// Creates a new ClaudeAgentLoop.
    /// </summary>
    /// <param name="claudeOptions">Options for the ClaudeAgentSdk client</param>
    /// <param name="mcpServers">MCP server configurations for tools</param>
    /// <param name="threadId">Unique identifier for this conversation thread</param>
    /// <param name="systemPrompt">System prompt for the agent (persists across all runs)</param>
    /// <param name="defaultOptions">Default GenerateReplyOptions template</param>
    /// <param name="maxTurnsPerRun">Maximum turns per run (default: 50)</param>
    /// <param name="inputChannelCapacity">Capacity of the input queue (default: 100)</param>
    /// <param name="outputChannelCapacity">Capacity per subscriber output channel (default: 1000)</param>
    /// <param name="store">Optional persistence store for conversation state</param>
    /// <param name="logger">Optional logger</param>
    /// <param name="loggerFactory">Optional logger factory for creating loggers for internal components</param>
    /// <param name="clientFactory">Optional factory for creating ClaudeAgentSdkClient (for testing/mocking)</param>
    public ClaudeAgentLoop(
        ClaudeAgentSdkOptions claudeOptions,
        Dictionary<string, McpServerConfig>? mcpServers,
        string threadId,
        string? systemPrompt = null,
        GenerateReplyOptions? defaultOptions = null,
        int maxTurnsPerRun = 50,
        int inputChannelCapacity = 100,
        int outputChannelCapacity = 1000,
        IConversationStore? store = null,
        ILogger<ClaudeAgentLoop>? logger = null,
        ILoggerFactory? loggerFactory = null,
        Func<ClaudeAgentSdkOptions, ILogger?, ClaudeAgentSdkClient>? clientFactory = null)
        : base(threadId, systemPrompt, defaultOptions, maxTurnsPerRun, inputChannelCapacity, outputChannelCapacity, store, logger)
    {
        ArgumentNullException.ThrowIfNull(claudeOptions);

        _claudeOptions = claudeOptions;
        _mcpServers = mcpServers ?? [];
        _loggerFactory = loggerFactory;
        _clientFactory = clientFactory;
    }

    /// <summary>
    /// ClaudeAgentLoop does not support injection into ongoing runs.
    /// Messages sent during an active run will be queued for the next run.
    /// </summary>
    protected override bool SupportsInjection => false;

    /// <inheritdoc />
    protected override void OnBeforeRun()
    {
        Logger.LogInformation(
            "Initializing ClaudeAgentSdk with {Count} MCP servers",
            _mcpServers.Count);

        foreach (var (name, config) in _mcpServers)
        {
            Logger.LogDebug(
                "MCP Server '{Name}': Type={Type}, Command={Command}, Url={Url}",
                name,
                config.Type,
                config.Command,
                config.Url);
        }

        // Create loggers for internal components
        var clientLogger = _loggerFactory?.CreateLogger<ClaudeAgentSdkClient>();
        var agentLogger = _loggerFactory?.CreateLogger<ClaudeAgentSdkAgent>();

        // Create client using factory if provided, otherwise create directly
        _client = _clientFactory != null
            ? _clientFactory(_claudeOptions, clientLogger)
            : new ClaudeAgentSdkClient(_claudeOptions, clientLogger);

        _agent = new ClaudeAgentSdkAgent(
            name: "ClaudeAgentLoop",
            client: _client,
            options: _claudeOptions,
            logger: agentLogger);
    }

    /// <inheritdoc />
    protected override void OnDispose()
    {
        _agent?.Dispose();
        _restartLock.Dispose();
    }

    /// <summary>
    /// Stop the current run AND the underlying claude-agent-sdk process.
    /// Can restart via SendAsync which will trigger RunAsync.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    public async Task StopProcessAsync(CancellationToken ct = default)
    {
        await _restartLock.WaitAsync(ct);
        try
        {
            Logger.LogInformation("Stopping process and run loop...");

            // First stop the run loop
            await StopAsync();

            // Then shutdown the client process gracefully
            if (_client != null)
            {
                await _client.ShutdownAsync(TimeSpan.FromSeconds(10), ct);
            }

            Logger.LogInformation("Process stopped, ready for restart via SendAsync");
        }
        finally
        {
            _restartLock.Release();
        }
    }

    /// <inheritdoc />
    public override async ValueTask<RunAssignment> SendAsync(
        List<IMessage> messages,
        string? inputId = null,
        string? parentRunId = null,
        CancellationToken ct = default)
    {
        await _restartLock.WaitAsync(ct);
        try
        {
            // Auto-restart if client was stopped but we have a previous request
            if (_client is { IsRunning: false, LastRequest: not null })
            {
                Logger.LogInformation("Client not running, restarting process...");
                await _client.StartAsync(_client.LastRequest, ct);

                // Also restart the run loop if needed
                if (!IsRunning)
                {
                    Logger.LogInformation("Restarting run loop...");
                    _ = RunAsync(ct);
                }
            }

            return await base.SendAsync(messages, inputId, parentRunId, ct);
        }
        finally
        {
            _restartLock.Release();
        }
    }

    /// <inheritdoc />
    protected override async Task<bool> ExecuteAgenticLoopAsync(
        string runId,
        string generationId,
        CancellationToken ct)
    {
        if (_agent == null)
        {
            throw new InvalidOperationException("Agent not initialized");
        }

        // Build options with MCP servers config
        var extraPropertiesBuilder = DefaultOptions.ExtraProperties?.ToBuilder()
            ?? ImmutableDictionary.CreateBuilder<string, object?>();

        // Add MCP servers to extra properties
        if (_mcpServers.Count > 0)
        {
            extraPropertiesBuilder["mcpServers"] = _mcpServers;
        }

        var options = DefaultOptions with
        {
            RunId = runId,
            ThreadId = ThreadId,
            MaxToken = 16192,
            ExtraProperties = extraPropertiesBuilder.ToImmutable(),
        };

        // Build messages list with system prompt prepended (if configured)
        var messagesToSend = GetMessagesWithSystemPrompt();

        // Stream responses from ClaudeAgentSdk
        // Note: The CLI handles tool execution via MCP - we just publish all messages
        var stream = await _agent.GenerateReplyStreamingAsync(messagesToSend, options, ct);

        await foreach (var msg in stream.WithCancellation(ct))
        {
            // Publish message to subscribers
            await PublishToAllAsync(msg, ct);

            // Add to conversation history
            AddToHistory(msg);
        }

        // ClaudeAgentLoop doesn't support forking
        return false;
    }
}
