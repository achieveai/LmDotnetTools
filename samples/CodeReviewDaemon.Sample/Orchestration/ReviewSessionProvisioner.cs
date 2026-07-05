using System.Collections.Concurrent;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace.Sandbox;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>The per-run sandbox binding: the gateway session id, its host workspace path, and the
/// command runner + filesystem bound to that session. All of a run's deterministic checkout/diff git AND
/// the review agent's MCP tools address this one session/container (design §4).</summary>
internal sealed record ReviewRunSession(
    string SessionId,
    string HostPath,
    ISandboxCommandRunner CommandRunner,
    ISandboxFileSystem FileSystem);

internal interface IReviewSessionProvisioner
{
    Task<ReviewRunSession> GetOrCreateAsync(ReviewRun run, CancellationToken ct);
    Task DestroyAsync(ReviewRun run, CancellationToken ct);
}

/// <summary>
/// The two session-lifecycle operations the provisioner needs from the registry. Implemented by
/// <see cref="AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox.SandboxSessionRegistry"/> (adapter in Program.cs)
/// and by a fake in tests.
/// </summary>
internal interface ISandboxSessionSource
{
    Task<SandboxSession> GetOrCreateLiveSessionAsync(WorkspaceRef workspaceRef, CancellationToken ct);

    Task DestroyWorkspaceSessionAsync(string workspaceId, CancellationToken ct);
}

/// <summary>
/// Provisions one sandbox session per review run and tears it down afterward. The session is keyed by a
/// stable per-run workspace id, so every stage of a run resolves the SAME session (recreated only if the
/// gateway evicted it mid-run — a retryable condition, design §7). The command runner + filesystem are
/// cached per session id so repeated stage calls reuse one <see cref="SandboxOrchestrator"/> connection.
/// </summary>
internal sealed class ReviewSessionProvisioner : IReviewSessionProvisioner
{
    private readonly ISandboxSessionSource _sessions;
    private readonly CodeReviewDaemonOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ReviewSessionProvisioner> _logger;
    private readonly ConcurrentDictionary<string, ReviewRunSession> _bySession = new(StringComparer.Ordinal);

    private readonly string _gatewayBaseUrl =
        Environment.GetEnvironmentVariable("CRD_SANDBOX_GATEWAY") ?? "http://127.0.0.1:3000";

    public ReviewSessionProvisioner(
        ISandboxSessionSource sessions,
        CodeReviewDaemonOptions options,
        ILoggerFactory loggerFactory)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = loggerFactory.CreateLogger<ReviewSessionProvisioner>();
    }

    public static string WorkspaceId(ReviewRun run) => $"review-run-{run.Id}";

    public async Task<ReviewRunSession> GetOrCreateAsync(ReviewRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);

        var workspaceId = WorkspaceId(run);
        var session = await _sessions
            .GetOrCreateLiveSessionAsync(
                new WorkspaceRef(workspaceId, DirectoryRelPath: workspaceId, Marketplaces: _options.Marketplaces),
                ct)
            .ConfigureAwait(false);

        return _bySession.GetOrAdd(session.SessionId, id =>
        {
            var runner = new SandboxOrchestrator(
                _gatewayBaseUrl,
                id,
                _loggerFactory.CreateLogger<SandboxOrchestrator>(),
                _options.Limits);
            return new ReviewRunSession(id, session.HostPath, runner, new SandboxFileSystem(runner));
        });
    }

    public async Task DestroyAsync(ReviewRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);

        var workspaceId = WorkspaceId(run);
        try
        {
            await _sessions.DestroyWorkspaceSessionAsync(workspaceId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Best-effort destroy of session for {WorkspaceId} failed.", workspaceId);
        }

        foreach (var (sessionId, runSession) in _bySession)
        {
            if (runSession.CommandRunner is IAsyncDisposable d && _bySession.TryRemove(sessionId, out _))
            {
                await d.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
