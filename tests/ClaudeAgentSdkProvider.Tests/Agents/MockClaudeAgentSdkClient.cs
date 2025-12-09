using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Agents;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Tests.Agents;

/// <summary>
///     Mock implementation of IClaudeAgentSdkClient for testing
///     Validates launch parameters and replays pre-recorded messages
/// </summary>
public class MockClaudeAgentSdkClient : IClaudeAgentSdkClient
{
    private readonly List<IMessage> _messagesToReplay;
    private readonly Action<ClaudeAgentSdkRequest>? _validateRequest;
    private readonly bool _simulateOneShotMode;

    /// <summary>
    ///     Number of times StartAsync has been called
    /// </summary>
    public int StartCallCount { get; private set; }

    /// <summary>
    ///     History of all requests received by StartAsync
    /// </summary>
    public List<ClaudeAgentSdkRequest> RequestHistory { get; } = [];

    public MockClaudeAgentSdkClient(
        List<IMessage> messagesToReplay,
        Action<ClaudeAgentSdkRequest>? validateRequest = null,
        bool simulateOneShotMode = false
    )
    {
        _messagesToReplay = messagesToReplay ?? throw new ArgumentNullException(nameof(messagesToReplay));
        _validateRequest = validateRequest;
        _simulateOneShotMode = simulateOneShotMode;
    }

    public async Task StartAsync(ClaudeAgentSdkRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Track call count and history
        StartCallCount++;
        RequestHistory.Add(request);

        // Store the request for potential restart
        LastRequest = request;

        // Validate launch parameters
        _validateRequest?.Invoke(request);

        // Basic validations
        if (string.IsNullOrEmpty(request.ModelId))
        {
            throw new ArgumentException("ModelId is required", nameof(request));
        }

        if (request.MaxTurns <= 0)
        {
            throw new ArgumentException("MaxTurns must be greater than 0", nameof(request));
        }

        IsRunning = true;

        // Generate a consistent sessionId for session continuity testing
        // On first call, use provided sessionId or generate new one
        // On subsequent calls with --resume, the same sessionId should be passed
        var sessionId = request.SessionId ?? $"session-{Guid.NewGuid():N}";
        CurrentSession = new SessionInfo
        {
            SessionId = sessionId,
            CreatedAt = DateTime.UtcNow,
            ProjectRoot = "test-project-root",
        };

        await Task.CompletedTask;
    }

    public async IAsyncEnumerable<IMessage> SendMessagesAsync(
        IEnumerable<IMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        if (!IsRunning)
        {
            throw new InvalidOperationException("Client must be started before sending messages");
        }

        ArgumentNullException.ThrowIfNull(messages);

        // Simulate latency
        await Task.Delay(10, cancellationToken);

        // Replay recorded messages
        foreach (var message in _messagesToReplay)
        {
            await Task.Delay(5, cancellationToken); // Simulate streaming delay
            yield return message;
        }

        // In OneShot mode, the process exits after sending all messages
        // This simulates the CLI exiting after ResultEvent
        if (_simulateOneShotMode)
        {
            IsRunning = false;
        }
    }

    public bool IsRunning { get; private set; }

    public SessionInfo? CurrentSession { get; private set; }

    public ClaudeAgentSdkRequest? LastRequest { get; private set; }

    /// <summary>
    ///     Number of times SendExitCommandAsync has been called
    /// </summary>
    public int ExitCommandCallCount { get; private set; }

    /// <summary>
    ///     Number of times ShutdownAsync has been called
    /// </summary>
    public int ShutdownCallCount { get; private set; }

    public Task<bool> SendExitCommandAsync(CancellationToken cancellationToken = default)
    {
        ExitCommandCallCount++;
        return Task.FromResult(IsRunning);
    }

    public Task ShutdownAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ShutdownCallCount++;
        IsRunning = false;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        IsRunning = false;
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        IsRunning = false;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Subscribe to all messages from the mock client.
    ///     In Interactive mode, this reads continuously until cancellation or process exit.
    /// </summary>
    public async IAsyncEnumerable<IMessage> SubscribeToMessagesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!IsRunning)
        {
            throw new InvalidOperationException("Client must be started before subscribing to messages");
        }

        // Replay recorded messages
        foreach (var message in _messagesToReplay)
        {
            await Task.Delay(5, cancellationToken); // Simulate streaming delay
            yield return message;
        }
    }

    /// <summary>
    ///     Send messages to the mock client (fire-and-forget).
    /// </summary>
    public Task SendAsync(IEnumerable<IMessage> messages, CancellationToken cancellationToken = default)
    {
        if (!IsRunning)
        {
            throw new InvalidOperationException("Client must be started before sending messages");
        }

        ArgumentNullException.ThrowIfNull(messages);

        // In a real implementation, this writes to stdin
        // For mock, we just validate and return
        return Task.CompletedTask;
    }
}
