using AchieveAi.LmDotnetTools.CodexSdkProvider.Models;

namespace AchieveAi.LmDotnetTools.CodexSdkProvider.Agents;

public interface ICodexSdkClient : IAsyncDisposable
{
    bool IsRunning { get; }

    string? CurrentCodexThreadId { get; }

    string DependencyState { get; }

    Task EnsureStartedAsync(CodexBridgeInitOptions options, CancellationToken ct = default);

    IAsyncEnumerable<CodexTurnEventEnvelope> RunStreamingAsync(string input, CancellationToken ct = default);

    Task ShutdownAsync(TimeSpan? timeout = null, CancellationToken ct = default);
}
