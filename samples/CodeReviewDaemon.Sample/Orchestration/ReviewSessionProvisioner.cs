using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Workspace.Sandbox;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>The daemon's shared Gateway session binding. Slot isolation is supplied by explicit command/file
/// paths inside the one mounted review workspace, never by creating one workspace per run or slot.</summary>
internal sealed record ReviewRunSession(
    string SessionId,
    ISandboxCommandRunner CommandRunner,
    ISandboxFileSystem FileSystem
);

internal interface IReviewSessionProvisioner
{
    Task<ReviewRunSession> GetOrCreateSharedAsync(CancellationToken ct);
}

internal interface ISandboxSessionSource
{
    Task<SandboxSession> GetOrCreateLiveSessionAsync(WorkspaceRef workspaceRef, CancellationToken ct);
}

/// <summary>
/// Resolves one application-lifetime Gateway session for the configured review workspace. Every run and slot
/// shares the same <see cref="WorkspaceRef"/>; callers scope effects with explicit paths beneath its mount.
/// </summary>
internal sealed class ReviewSessionProvisioner : IReviewSessionProvisioner, IAsyncDisposable
{
    public const string DefaultWorkspaceId = "nova-reviews";

    private readonly ISandboxSessionSource _sessions;
    private readonly CodeReviewDaemonOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ReviewSessionProvisioner> _logger;
    private readonly SandboxCredential _credential;
    private readonly string _gatewayBaseUrl;
    private readonly string _workspaceId;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private ReviewRunSession? _sharedSession;
    private readonly List<IAsyncDisposable> _adapters = [];
    private bool _disposed;

    public ReviewSessionProvisioner(
        ISandboxSessionSource sessions,
        CodeReviewDaemonOptions options,
        ILoggerFactory loggerFactory,
        SandboxCredential credential = default,
        string? gatewayBaseUrl = null,
        string? workspaceId = null
    )
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = loggerFactory.CreateLogger<ReviewSessionProvisioner>();
        _credential = credential;
        _gatewayBaseUrl =
            gatewayBaseUrl ?? Environment.GetEnvironmentVariable("CRD_SANDBOX_GATEWAY") ?? "http://127.0.0.1:3000";
        _workspaceId = string.IsNullOrWhiteSpace(workspaceId) ? DefaultWorkspaceId : workspaceId;
    }

    internal string GatewayBaseUrl => _gatewayBaseUrl;

    internal string WorkspaceId => _workspaceId;

    public async Task<ReviewRunSession> GetOrCreateSharedAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _sessionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var session = await _sessions
                .GetOrCreateLiveSessionAsync(
                    new WorkspaceRef(_workspaceId, DirectoryRelPath: _workspaceId, Marketplaces: _options.Marketplaces),
                    ct
                )
                .ConfigureAwait(false);
            if (_sharedSession?.SessionId == session.SessionId)
                return _sharedSession;
            // Existing borrowers can still be using the prior adapter. Revalidation never replays their
            // commands or disposes their transport; all generations live until application shutdown.
            var adapter = new SandboxSessionAdapter(
                _gatewayBaseUrl,
                session.SessionId,
                _loggerFactory.CreateLogger<SandboxSessionAdapter>(),
                _credential,
                _options.Limits
            );
            _adapters.Add(adapter);
            _sharedSession = new ReviewRunSession(session.SessionId, adapter, adapter);
            _logger.LogInformation(
                "Bound the review daemon to shared Gateway workspace {WorkspaceId} using session {SessionId}.",
                _workspaceId,
                session.SessionId
            );
            return _sharedSession;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _sessionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var adapter in _adapters)
                await adapter.DisposeAsync().ConfigureAwait(false);
            _adapters.Clear();
        }
        finally
        {
            _sessionGate.Release();
        }
    }
}
