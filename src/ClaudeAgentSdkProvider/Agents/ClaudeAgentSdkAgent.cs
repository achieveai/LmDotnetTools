using System.Text.Json;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Agents;

/// <summary>
///     Agent implementation using claude-agent-sdk CLI
///     Wraps long-lived Node.js process for continuous agent interaction
/// </summary>
public class ClaudeAgentSdkAgent : IStreamingAgent, IDisposable
{
    private readonly IClaudeAgentSdkClient _client;
    private readonly ILogger<ClaudeAgentSdkAgent>? _logger;
    private readonly ClaudeAgentSdkOptions _options;
    private bool _disposed;

    /// <summary>
    ///     Tracks the session ID across process restarts (for OneShot mode)
    /// </summary>
    private string? _lastSessionId;

    public ClaudeAgentSdkAgent(
        string name,
        IClaudeAgentSdkClient client,
        ClaudeAgentSdkOptions options,
        ILogger<ClaudeAgentSdkAgent>? logger = null
    )
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    public string Name { get; }

    public void Dispose()
    {
        if (!_disposed)
        {
            _client?.Dispose();
            _disposed = true;
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Generate reply in streaming mode
    /// </summary>
    public async Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(messages);

        // Ensure client is started
        if (!_client.IsRunning)
        {
            await StartClientAsync(messages, options, cancellationToken);
        }

        // Stream responses from client
        return _client.SendMessagesAsync(messages, cancellationToken);
    }

    /// <summary>
    ///     Generate reply in non-streaming mode (aggregates streaming responses)
    /// </summary>
    public async Task<IEnumerable<IMessage>> GenerateReplyAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var resultMessages = new List<IMessage>();
        var streamTask = await GenerateReplyStreamingAsync(messages, options, cancellationToken);

        await foreach (var message in streamTask.WithCancellation(cancellationToken))
        {
            resultMessages.Add(message);
        }

        return resultMessages;
    }

    /// <summary>
    ///     Start the underlying CLI client with configured options
    /// </summary>
    private async Task StartClientAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options,
        CancellationToken cancellationToken
    )
    {
        // Capture sessionId from previous run (for OneShot mode session continuity)
        // If the client had a session, preserve it for the next process
        if (_client.CurrentSession?.SessionId != null)
        {
            _lastSessionId = _client.CurrentSession.SessionId;
            _logger?.LogDebug(
                "Preserved sessionId from previous run: {SessionId}",
                _lastSessionId);
        }

        var request = BuildClaudeAgentSdkRequest(messages, options);

        _logger?.LogInformation(
            "Starting claude-agent-sdk client with model {Model}, maxTurns {MaxTurns}, sessionId {SessionId}",
            request.ModelId,
            request.MaxTurns,
            request.SessionId ?? "(new session)"
        );

        await _client.StartAsync(request, cancellationToken);
    }

    /// <summary>
    ///     Build ClaudeAgentSdkRequest from messages and GenerateReplyOptions
    /// </summary>
    private ClaudeAgentSdkRequest BuildClaudeAgentSdkRequest(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options
    )
    {
        var modelId = options?.ModelId ?? "claude-sonnet-4-5-20250929";
        var maxTurns = options?.MaxToken ?? 40; // MaxToken is repurposed for max turns

        // Max thinking tokens: priority is ExtraProperties > ClaudeAgentSdkOptions
        var maxThinkingTokens = _options.MaxThinkingTokens;
        if (options?.ExtraProperties?.TryGetValue("maxThinkingTokens", out var thinkingObj) == true
            && thinkingObj != null)
        {
            maxThinkingTokens = Convert.ToInt32(thinkingObj);
        }

        // Extract system message from messages (we only use the first one)
        var systemMessage = messages.OfType<TextMessage>().FirstOrDefault(m => m.Role == Role.System);

        var systemPrompt = systemMessage?.Text;

        // Extract session ID: priority is explicit options > preserved from previous run
        string? sessionId = null;
        if (options?.ExtraProperties?.TryGetValue("sessionId", out var sessionIdObj) == true)
        {
            sessionId = sessionIdObj?.ToString();
        }

        // Use preserved sessionId from previous run if not explicitly provided
        // This enables session continuity in OneShot mode across process restarts
        sessionId ??= _lastSessionId;

        // Build MCP server configuration
        // Priority: ExtraProperties > file-based config (merged, ExtraProperties wins on conflict)
        Dictionary<string, McpServerConfig>? mcpServers = null;

        // First, try to load from file
        if (!string.IsNullOrEmpty(_options.McpConfigPath) && File.Exists(_options.McpConfigPath))
        {
            try
            {
                var mcpConfig = ClaudeAgentSdkAgent.LoadMcpConfiguration(_options.McpConfigPath);
                mcpServers = mcpConfig?.McpServers;
                _logger?.LogDebug(
                    "Loaded {Count} MCP servers from file: {Path}",
                    mcpServers?.Count ?? 0,
                    _options.McpConfigPath);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to load MCP configuration from {Path}", _options.McpConfigPath);
            }
        }

        // Then, check ExtraProperties for mcpServers (takes precedence)
        if (options?.ExtraProperties?.TryGetValue("mcpServers", out var mcpServersObj) == true
            && mcpServersObj is Dictionary<string, McpServerConfig> extraMcpServers)
        {
            _logger?.LogDebug(
                "Found {Count} MCP servers in ExtraProperties",
                extraMcpServers.Count);

            if (mcpServers == null)
            {
                mcpServers = extraMcpServers;
            }
            else
            {
                // Merge: ExtraProperties servers override file-based ones
                foreach (var (name, config) in extraMcpServers)
                {
                    mcpServers[name] = config;
                    _logger?.LogDebug("MCP server '{Name}' from ExtraProperties (override)", name);
                }
            }
        }

        _logger?.LogInformation(
            "Final MCP server configuration: {Count} servers configured",
            mcpServers?.Count ?? 0);

        // Build allowed tools list
        // Note: FunctionContract[] is NOT supported - claude-agent-sdk only supports MCP tools
        var allowedTools = "Read,Write,Edit,Bash,Grep,Glob,TodoWrite,Task";
        if (options?.Functions != null && options.Functions.Length > 0)
        {
            _logger?.LogWarning(
                "FunctionContract[] is not supported by claude-agent-sdk. Only MCP tools are supported. "
                    + "Convert your functions to MCP server tools instead."
            );
        }

        return new ClaudeAgentSdkRequest
        {
            ModelId = modelId,
            MaxTurns = maxTurns,
            MaxThinkingTokens = maxThinkingTokens,
            SessionId = sessionId,
            SystemPrompt = systemPrompt,
            AllowedTools = allowedTools,
            McpServers = mcpServers,
            Verbose = true,
        };
    }

    /// <summary>
    ///     Load MCP configuration from file
    /// </summary>
    private static McpConfiguration? LoadMcpConfiguration(string mcpConfigPath)
    {
        var json = File.ReadAllText(mcpConfigPath);
        return JsonSerializer.Deserialize<McpConfiguration>(json);
    }
}
