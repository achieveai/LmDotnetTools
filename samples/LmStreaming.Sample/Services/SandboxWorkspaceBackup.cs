using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.Sandbox;
using LmStreaming.Sample.Persistence;

namespace LmStreaming.Sample.Services;

/// <summary>
/// The sample's thin seam over the gateway's workspace backup API (<c>/api/v1/backup/*</c>) for the
/// conversation inspector. Backups are scoped to an APP, not a session, so this owns one keyless SDK client
/// for the sample's own app (the credential interactive conversations create their sandboxes under); the
/// app key is stamped by the <see cref="GatewayAuthHandler"/> in the borrowed client's pipeline.
/// </summary>
public sealed class SandboxWorkspaceBackup : IDisposable
{
    private readonly SandboxGatewayOptions _options;
    private readonly IWorkspaceStore _workspaces;
    private readonly Lazy<SandboxClient> _client;

    public SandboxWorkspaceBackup(
        SandboxGatewayOptions options,
        Func<string> gatewayBaseUrl,
        IWorkspaceStore workspaces,
        HttpClient httpClient
    )
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(gatewayBaseUrl);
        _workspaces = workspaces ?? throw new ArgumentNullException(nameof(workspaces));
        ArgumentNullException.ThrowIfNull(httpClient);
        // Lazy: the gateway base URL is the one the session registry talks to, which a spawned gateway
        // only settles once it is running.
        _client = new Lazy<SandboxClient>(() =>
            new SandboxClient(
                new SandboxClientOptions(
                    new Uri(gatewayBaseUrl().TrimEnd('/') + "/", UriKind.Absolute),
                    options.AppId,
                    clientSecret: string.Empty,
                    executionTimeout: TimeSpan.FromMinutes(2),
                    transportTimeout: httpClient.Timeout > TimeSpan.Zero
                        ? httpClient.Timeout
                        : TimeSpan.FromSeconds(100),
                    allowInsecureDevelopmentTransport: true
                ),
                httpClient
            )
        );
    }

    /// <summary>
    /// The gateway workspace name a conversation workspace's sandboxes are created on — the same leaf the
    /// session registry sends as <c>workspace</c> — or <see langword="null"/> when none is configured.
    /// </summary>
    public async Task<string?> ResolveGatewayWorkspaceAsync(string workspaceId, CancellationToken ct)
    {
        var workspace = await _workspaces.GetAsync(workspaceId, ct).ConfigureAwait(false);
        var (_, leaf, _) = _options.ResolveWorkspace(workspace?.DirectoryRelPath);
        return string.IsNullOrWhiteSpace(leaf) ? null : leaf;
    }

    public Task<IReadOnlyList<SandboxBackupSnapshot>> ListSnapshotsAsync(
        string gatewayWorkspace,
        CancellationToken ct
    ) => _client.Value.ListBackupSnapshotsAsync(gatewayWorkspace, ct);

    /// <summary>
    /// Restores <paramref name="snapshotId"/>, or returns <see langword="null"/> when the workspace has no such
    /// snapshot. The gateway refuses a restore while any sandbox mounts the workspace, so
    /// <paramref name="releaseSandboxes"/> stops them first — but only once the snapshot is known to exist, so a
    /// stale or mistyped id never costs anyone their live sandbox.
    /// </summary>
    public async Task<SandboxBackupRestoreResult?> RestoreAsync(
        string gatewayWorkspace,
        string snapshotId,
        Func<CancellationToken, Task> releaseSandboxes,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(releaseSandboxes);
        var snapshots = await ListSnapshotsAsync(gatewayWorkspace, ct).ConfigureAwait(false);
        if (!snapshots.Any(snapshot => string.Equals(snapshot.Id, snapshotId, StringComparison.Ordinal)))
        {
            return null;
        }

        await releaseSandboxes(ct).ConfigureAwait(false);
        return await _client.Value.RestoreBackupSnapshotAsync(gatewayWorkspace, snapshotId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Purges the workspace, then lets <paramref name="releaseSandboxes"/> forget the sessions the gateway just
    /// deleted. In that order the result's <see cref="SandboxBackupPurgeResult.SandboxesDeleted"/> names them,
    /// instead of reporting none because they were already stopped.
    /// </summary>
    public async Task<SandboxBackupPurgeResult> PurgeAsync(
        string gatewayWorkspace,
        Func<CancellationToken, Task> releaseSandboxes,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(releaseSandboxes);
        var purged = await _client.Value.PurgeBackupWorkspaceAsync(gatewayWorkspace, ct).ConfigureAwait(false);
        await releaseSandboxes(ct).ConfigureAwait(false);
        return purged;
    }

    public Task<SandboxBackupCosts> GetCostsAsync(CancellationToken ct) =>
        _client.Value.GetBackupCostsAsync(since: null, SandboxBackupCostGrouping.Workspace, ct);

    public void Dispose()
    {
        if (_client.IsValueCreated)
        {
            _client.Value.Dispose();
        }
    }
}
