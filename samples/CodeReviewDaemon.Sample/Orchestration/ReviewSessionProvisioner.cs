using System.Collections.Concurrent;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace;
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
    /// <summary>
    /// Resolves (creating if needed) the per-run sandbox session, or <c>null</c> when the host-dir disk
    /// guard declines to provision one (design §7, Task 18) — callers treat a null result exactly like "no
    /// provisioner registered" and fall back to the diff-only path rather than failing the stage.
    /// </summary>
    Task<ReviewRunSession?> GetOrCreateAsync(ReviewRun run, CancellationToken ct);

    /// <summary>
    /// Resolves the per-run sandbox session mounted OVER the leased pool <paramref name="slot"/> instead of
    /// a fresh per-run dir, so <c>/workspace</c> is the slot itself — <c>/workspace/store</c> is the slot's
    /// store clone and the reviewer's scoped writes/store reads point at real files (design §4.1). The
    /// session is still keyed by the per-run workspace id (every stage resolves the SAME session); only the
    /// mounted directory differs. Falls back to <see cref="GetOrCreateAsync"/> — and returns exactly what it
    /// returns — when the slot cannot be expressed as a path under the configured workspace base (a
    /// misconfigured pool root degrades to the per-run mount rather than failing the stage).
    /// </summary>
    Task<ReviewRunSession?> GetOrCreateForSlotAsync(ReviewRun run, ReviewSlot slot, CancellationToken ct);

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
    /// <summary>
    /// The free-disk floor the host workspace root must clear before a new session is provisioned
    /// (Task 18, design §7). Below this, <see cref="GetOrCreateAsync"/> logs and degrades (returns
    /// <c>null</c>) rather than provisioning onto a near-full disk — the executor falls back to diff-only.
    /// </summary>
    private const long MinFreeDiskBytes = 1L * 1024 * 1024 * 1024; // 1 GiB

    private readonly ISandboxSessionSource _sessions;
    private readonly CodeReviewDaemonOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ReviewSessionProvisioner> _logger;
    private readonly SandboxCredential _credential;
    private readonly ConcurrentDictionary<string, ReviewRunSession> _bySession = new(StringComparer.Ordinal);

    /// <summary>
    /// The gateway's host workspace base directory (its <c>WORKSPACE_BASE_PATH</c>). A leased pool slot is
    /// mounted at <c>/workspace</c> by expressing its host path RELATIVE to this base (see
    /// <see cref="GetOrCreateForSlotAsync"/>); <c>null</c>/blank (or a slot outside it) makes the slot mount
    /// degrade to the per-run mount.
    /// </summary>
    private readonly string? _workspaceBasePath;

    private readonly string _gatewayBaseUrl;

    public ReviewSessionProvisioner(
        ISandboxSessionSource sessions,
        CodeReviewDaemonOptions options,
        ILoggerFactory loggerFactory,
        SandboxCredential credential = default,
        string? workspaceBasePath = null,
        string? gatewayBaseUrl = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = loggerFactory.CreateLogger<ReviewSessionProvisioner>();
        _credential = credential;
        _workspaceBasePath = workspaceBasePath;
        _gatewayBaseUrl = gatewayBaseUrl
            ?? Environment.GetEnvironmentVariable("CRD_SANDBOX_GATEWAY")
            ?? "http://127.0.0.1:3000";
    }

    public static string WorkspaceId(ReviewRun run) => $"review-run-{run.Id}";

    /// <summary>
    /// Host directory that per-run sandbox workspaces are created under (<see
    /// cref="CodeReviewDaemonOptions.WorkspaceHostRoot"/>), defaulted beside the binary exactly like
    /// Program.cs's ReviewBot host-root default when the operator has not configured one.
    /// </summary>
    private string HostWorkspaceRoot => string.IsNullOrWhiteSpace(_options.WorkspaceHostRoot)
        ? Path.Combine(AppContext.BaseDirectory, "workspaces")
        : _options.WorkspaceHostRoot;

    public async Task<ReviewRunSession?> GetOrCreateAsync(ReviewRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);

        // The per-run mount: /workspace is a fresh dir named review-run-{id} under the gateway's base.
        return await ProvisionAsync(run, directoryRelPath: WorkspaceId(run), ct).ConfigureAwait(false);
    }

    public async Task<ReviewRunSession?> GetOrCreateForSlotAsync(ReviewRun run, ReviewSlot slot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(slot);

        // Mount /workspace OVER the leased slot by expressing its host path relative to the gateway's base.
        // A misconfigured pool root (base unset, or the slot outside it) is not a hard failure: degrade to
        // the per-run mount so the reviewer still gets a session (design §7) — its store/scratch just won't
        // resolve, exactly as on a non-pooled run.
        var slotRelPath = ResolveSlotRelPath(slot);
        if (slotRelPath is null)
        {
            _logger.LogWarning(
                "Run {RunId}: cannot mount pooled slot '{SlotPath}' under workspace base '{Base}'; "
                    + "falling back to the per-run session mount.",
                run.Id, slot.HostPath, _workspaceBasePath ?? "(unset)");
            return await GetOrCreateAsync(run, ct).ConfigureAwait(false);
        }

        return await ProvisionAsync(run, slotRelPath, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The shared session-provisioning tail: applies the host-dir disk guard, then resolves (creating once)
    /// the run's sandbox session mounting <paramref name="directoryRelPath"/> under the gateway base, and
    /// caches the runner/filesystem per session id. The session is always keyed by <see cref="WorkspaceId"/>
    /// so every stage of a run resolves the SAME session regardless of the mounted directory.
    /// </summary>
    private async Task<ReviewRunSession?> ProvisionAsync(ReviewRun run, string directoryRelPath, CancellationToken ct)
    {
        if (!HasSufficientDiskSpace())
        {
            _logger.LogWarning(
                "Run {RunId}: host workspace root '{HostRoot}' has less than {MinFreeDiskBytes} bytes free; "
                    + "declining to provision a sandbox session.",
                run.Id, HostWorkspaceRoot, MinFreeDiskBytes);
            return null;
        }

        var workspaceId = WorkspaceId(run);
        var session = await _sessions
            .GetOrCreateLiveSessionAsync(
                new WorkspaceRef(workspaceId, DirectoryRelPath: directoryRelPath, Marketplaces: _options.Marketplaces),
                ct)
            .ConfigureAwait(false);

        return _bySession.GetOrAdd(session.SessionId, id =>
        {
            var runner = new SandboxOrchestrator(
                _gatewayBaseUrl,
                id,
                _loggerFactory.CreateLogger<SandboxOrchestrator>(),
                _credential,
                _options.Limits);
            return new ReviewRunSession(id, session.HostPath, runner, new SandboxFileSystem(runner));
        });
    }

    /// <summary>
    /// The leased slot's host path as a forward-slashed directory leaf RELATIVE to
    /// <see cref="_workspaceBasePath"/>, or <c>null</c> when no base is configured or the slot is not under
    /// it. <see cref="Path.GetRelativePath(string, string)"/> yields a rooted path (different drive/root) or
    /// a <c>..</c>-prefixed path (same drive, outside the base) when the slot escapes the base — both are
    /// rejected so the mount can never point outside the gateway's configured workspace root.
    /// </summary>
    private string? ResolveSlotRelPath(ReviewSlot slot)
    {
        if (string.IsNullOrWhiteSpace(_workspaceBasePath))
        {
            return null;
        }

        var relative = Path.GetRelativePath(_workspaceBasePath, slot.HostPath);
        if (Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith("../", StringComparison.Ordinal))
        {
            return null;
        }

        return relative.Replace('\\', '/');
    }

    /// <summary>
    /// Whether the drive hosting <see cref="HostWorkspaceRoot"/> has at least <see cref="MinFreeDiskBytes"/>
    /// free. Fails OPEN (returns <c>true</c>) when the check itself cannot complete (e.g. the root does not
    /// exist yet, or the drive cannot be queried) — mirrors the registry's own fail-open probe pattern
    /// (design §7): an inability to check disk space must never itself wedge the daemon.
    /// </summary>
    private bool HasSufficientDiskSpace()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(HostWorkspaceRoot));
            if (string.IsNullOrEmpty(root))
            {
                return true;
            }

            var drive = new DriveInfo(root);
            return !drive.IsReady || drive.AvailableFreeSpace >= MinFreeDiskBytes;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to determine free disk space for '{HostRoot}'; assuming sufficient.", HostWorkspaceRoot);
            return true;
        }
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

        // Best-effort remove the per-run HOST workspace dir (Task 18) — untrusted checkouts can leave
        // read-only files behind, so read-only is cleared before the recursive delete.
        var hostDir = Path.Combine(HostWorkspaceRoot, workspaceId);
        try
        {
            if (Directory.Exists(hostDir))
            {
                ClearReadOnly(hostDir);
                Directory.Delete(hostDir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Best-effort host-dir cleanup failed for {HostDir}.", hostDir);
        }
    }

    /// <summary>Recursively clears the read-only attribute so an untrusted checkout's read-only files
    /// (e.g. from a git object store) do not block the subsequent recursive delete.</summary>
    private static void ClearReadOnly(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }
        }
    }
}
