using AchieveAi.LmDotnetTools.CopilotSdkProvider.Models;

namespace AchieveAi.LmDotnetTools.CopilotSdkProvider.Agents;

/// <summary>
/// Abstraction over the Copilot ACP transport and session lifecycle used by
/// <c>CopilotAgentLoop</c>. Implementations must be thread-safe across concurrent
/// calls to <see cref="RunStreamingAsync"/>.
/// </summary>
public interface ICopilotSdkClient : IAsyncDisposable
{
    bool IsRunning { get; }

    string? CurrentCopilotSessionId { get; }

    string DependencyState { get; }

    void ConfigureDynamicToolExecutor(
        Func<CopilotDynamicToolCallRequest, CancellationToken, Task<CopilotDynamicToolCallResponse>>? executor);

    Task StartOrResumeSessionAsync(CopilotBridgeInitOptions options, CancellationToken ct = default);

    Task EnsureStartedAsync(CopilotBridgeInitOptions options, CancellationToken ct = default);

    IAsyncEnumerable<CopilotTurnEventEnvelope> RunStreamingAsync(string input, CancellationToken ct = default);

    Task InterruptTurnAsync(CancellationToken ct = default);

    Task ShutdownAsync(TimeSpan? timeout = null, CancellationToken ct = default);
}
