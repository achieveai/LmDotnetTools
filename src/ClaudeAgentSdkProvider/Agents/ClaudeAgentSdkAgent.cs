using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Agents;

/// <summary>
/// Agent implementation using claude-agent-sdk CLI
/// Wraps long-lived Node.js process for continuous agent interaction
/// </summary>
public class ClaudeAgentSdkAgent : IStreamingAgent, IDisposable
{
    private readonly IClaudeAgentSdkClient _client;
    private readonly ClaudeAgentSdkOptions _options;
    private readonly ILogger<ClaudeAgentSdkAgent>? _logger;
    private bool _disposed;

    public ClaudeAgentSdkAgent(
        string name,
        IClaudeAgentSdkClient client,
        ClaudeAgentSdkOptions options,
        ILogger<ClaudeAgentSdkAgent>? logger = null)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    public string Name { get; }

    /// <summary>
    /// Generate reply in streaming mode
    /// </summary>
    public async Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default)
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
    /// Generate reply in non-streaming mode (aggregates streaming responses)
    /// </summary>
    public async Task<IEnumerable<IMessage>> GenerateReplyAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default)
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
    /// Start the underlying CLI client with configured options
    /// </summary>
    private async Task StartClientAsync(IEnumerable<IMessage> messages, GenerateReplyOptions? options, CancellationToken cancellationToken)
    {
        var request = BuildClaudeAgentSdkRequest(messages, options);

        _logger?.LogInformation(
            "Starting claude-agent-sdk client with model {Model}, maxTurns {MaxTurns}",
            request.ModelId,
            request.MaxTurns
        );

        await _client.StartAsync(request, cancellationToken);
    }

    /// <summary>
    /// Build ClaudeAgentSdkRequest from messages and GenerateReplyOptions
    /// </summary>
    private ClaudeAgentSdkRequest BuildClaudeAgentSdkRequest(IEnumerable<IMessage> messages, GenerateReplyOptions? options)
    {
        var modelId = options?.ModelId ?? "claude-sonnet-4-5-20250929";
        var maxTurns = options?.MaxToken ?? 40;  // MaxToken is repurposed for max turns
        var maxThinkingTokens = 0;  // Default: no extended thinking

        // Extract system message from messages (we only use the first one)
        var systemMessage = messages
            .OfType<TextMessage>()
            .FirstOrDefault(m => m.Role == Role.System);

        string? systemPrompt = systemMessage?.Text;

        // Extract session ID from options if provided
        string? sessionId = null;
        if (options?.ExtraProperties?.TryGetValue("sessionId", out var sessionIdObj) == true)
        {
            sessionId = sessionIdObj?.ToString();
        }

        // Build MCP server configuration
        Dictionary<string, McpServerConfig>? mcpServers = null;
        if (!string.IsNullOrEmpty(_options.McpConfigPath) && File.Exists(_options.McpConfigPath))
        {
            try
            {
                var mcpConfig = ClaudeAgentSdkAgent.LoadMcpConfiguration(_options.McpConfigPath);
                mcpServers = mcpConfig?.McpServers;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to load MCP configuration from {Path}", _options.McpConfigPath);
            }
        }

        // Build allowed tools list
        // Note: FunctionContract[] is NOT supported - claude-agent-sdk only supports MCP tools
        var allowedTools = "Read,Write,Edit,Bash,Grep,Glob,TodoWrite,Task";
        if (options?.Functions != null && options.Functions.Length > 0)
        {
            _logger?.LogWarning(
                "FunctionContract[] is not supported by claude-agent-sdk. Only MCP tools are supported. " +
                "Convert your functions to MCP server tools instead."
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
            Verbose = true
        };
    }

    /// <summary>
    /// Load MCP configuration from file
    /// </summary>
    private static McpConfiguration? LoadMcpConfiguration(string mcpConfigPath)
    {
        var json = File.ReadAllText(mcpConfigPath);
        return System.Text.Json.JsonSerializer.Deserialize<McpConfiguration>(json);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _client?.Dispose();
            _disposed = true;
        }

        GC.SuppressFinalize(this);
    }
}
