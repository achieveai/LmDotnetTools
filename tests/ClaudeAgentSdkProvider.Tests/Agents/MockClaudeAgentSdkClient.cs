using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Agents;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Tests.Agents;

/// <summary>
/// Mock implementation of IClaudeAgentSdkClient for testing
/// Validates launch parameters and replays pre-recorded messages
/// </summary>
public class MockClaudeAgentSdkClient : IClaudeAgentSdkClient
{
    private readonly List<IMessage> _messagesToReplay;
    private readonly Action<ClaudeAgentSdkRequest>? _validateRequest;

    public MockClaudeAgentSdkClient(
        List<IMessage> messagesToReplay,
        Action<ClaudeAgentSdkRequest>? validateRequest = null)
    {
        _messagesToReplay = messagesToReplay ?? throw new ArgumentNullException(nameof(messagesToReplay));
        _validateRequest = validateRequest;
    }

    public async Task StartAsync(ClaudeAgentSdkRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

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
        CurrentSession = new SessionInfo
        {
            SessionId = request.SessionId ?? Guid.NewGuid().ToString(),
            CreatedAt = DateTime.UtcNow,
            ProjectRoot = "test-project-root"
        };

        await Task.CompletedTask;
    }

    public async IAsyncEnumerable<IMessage> SendMessagesAsync(
        IEnumerable<IMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
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
            await Task.Delay(5, cancellationToken);  // Simulate streaming delay
            yield return message;
        }
    }

    public bool IsRunning { get; private set; }

    public SessionInfo? CurrentSession { get; private set; }

    public void Dispose()
    {
        IsRunning = false;
        GC.SuppressFinalize(this);
    }
}
