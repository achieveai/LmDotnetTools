using AchieveAi.LmDotnetTools.CodexSdkProvider.Models;

namespace AchieveAi.LmDotnetTools.CodexSdkProvider.Agents;

public interface ICodexSdkClient : IAsyncDisposable
{
    bool IsRunning { get; }

    string? CurrentCodexThreadId { get; }

    string? CurrentTurnId { get; }

    string DependencyState { get; }

    void ConfigureDynamicToolExecutor(
        Func<CodexDynamicToolCallRequest, CancellationToken, Task<CodexDynamicToolCallResponse>>? executor);

    Task StartOrResumeThreadAsync(CodexBridgeInitOptions options, CancellationToken ct = default);

    Task EnsureStartedAsync(CodexBridgeInitOptions options, CancellationToken ct = default);

    IAsyncEnumerable<CodexTurnEventEnvelope> RunStreamingAsync(string input, CancellationToken ct = default);

    Task InterruptTurnAsync(CancellationToken ct = default);

    Task ShutdownAsync(TimeSpan? timeout = null, CancellationToken ct = default);
}
