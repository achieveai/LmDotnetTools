using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.Sandbox;
using CodeReviewDaemon.Sample.Configuration;
using SdkSandboxCommand = AchieveAi.LmDotnetTools.Sandbox.SandboxCommand;
using SdkSandboxCommandResult = AchieveAi.LmDotnetTools.Sandbox.SandboxCommandResult;

namespace CodeReviewDaemon.Sample.Workspace.Sandbox;

/// <summary>
/// The single per-session adapter that binds the daemon's deterministic git/filesystem orchestration
/// to the typed <see cref="SandboxClient"/> SDK (issue #192). It implements BOTH the daemon's
/// <see cref="ISandboxCommandRunner"/> and <see cref="ISandboxFileSystem"/> ports over one borrowed
/// gateway session, replacing the hand-rolled <c>SandboxOrchestrator</c> + <c>SandboxFileSystem</c> +
/// <c>PosixShell</c> protocol duplication: the SDK now owns POSIX quoting, the exit-code capture, the
/// base64/manifest wire format, chunking, retries, and recovery. This class is pure mapping —
/// daemon types ↔ SDK types, and the daemon's PR #121 H4 output cap / per-command timeout contracts.
/// </summary>
/// <remarks>
/// <para>
/// <b>Path model.</b> The daemon addresses container paths absolutely under <c>/workspace</c> (the
/// gateway mount), whereas the SDK's command working directory and file paths are workspace-RELATIVE
/// (rooted at <c>${SANDBOX_WORKSPACE:-/workspace}</c> by the SDK's own wrappers). The adapter strips a
/// leading <c>/workspace</c> so an absolute daemon path resolves to the IDENTICAL container path the
/// old shell <c>cd</c>/<c>base64</c>/<c>ls</c> lines produced. Argument vectors are passed through
/// untouched — a literal <c>/workspace/...</c> token inside argv (e.g. <c>git -C</c>) is a container
/// path the gateway runs verbatim, exactly as before.
/// </para>
/// <para>
/// <b>Ownership.</b> The adapter builds its <see cref="SandboxClient"/> lazily on first use over a
/// dedicated (owned) <see cref="System.Net.Http.HttpClient"/>, so constructing an adapter does no
/// gateway work and validates no credential — mirroring the old orchestrator's lazy connect and
/// keeping the provisioner's construct-per-session path inert. <see cref="DisposeAsync"/> releases only
/// that local transport; it NEVER deletes the remote sandbox session (remote lifecycle stays with the
/// registry/provisioner), satisfying the non-owning-of-the-remote-session contract.
/// </para>
/// </remarks>
internal sealed class SandboxSessionAdapter : ISandboxCommandRunner, ISandboxFileSystem, IAsyncDisposable
{
    /// <summary>The gateway container mount every absolute daemon path is rooted at; stripped to yield the SDK's workspace-relative form.</summary>
    private const string WorkspaceRoot = "/workspace";

    /// <summary>Grace added to the per-command timeout for the SDK's client-side transport deadline, so a
    /// single gateway call is never aborted before a command legitimately running up to the command timeout completes.</summary>
    private static readonly TimeSpan S_transportGrace = TimeSpan.FromSeconds(30);

    private readonly string _gatewayBaseUrl;
    private readonly string _sessionId;
    private readonly SandboxCredential _credential;
    private readonly SandboxLimits _limits;
    private readonly ILogger<SandboxSessionAdapter> _logger;
    private readonly HttpMessageHandler? _testTransport;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private SandboxClient? _client;
    private HttpClient? _borrowedHttpClient;
    private bool _disposed;

    public SandboxSessionAdapter(
        string gatewayBaseUrl,
        string sessionId,
        ILogger<SandboxSessionAdapter> logger,
        SandboxCredential credential,
        SandboxLimits? limits = null
    )
        : this(gatewayBaseUrl, sessionId, logger, credential, limits, testTransport: null) { }

    /// <summary>
    /// Test seam: drives the SDK client over a supplied <paramref name="testTransport"/> (a scripted
    /// gateway <see cref="HttpMessageHandler"/>) instead of the production owned socket, so the adapter's
    /// mapping can be exercised against the real SDK wire protocol without a live gateway. Never used by
    /// production wiring, which always takes the public constructor and an owned transport.
    /// </summary>
    internal SandboxSessionAdapter(
        string gatewayBaseUrl,
        string sessionId,
        ILogger<SandboxSessionAdapter> logger,
        SandboxCredential credential,
        SandboxLimits? limits,
        HttpMessageHandler? testTransport
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayBaseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _gatewayBaseUrl = gatewayBaseUrl;
        _sessionId = sessionId;
        _credential = credential;
        _limits = limits ?? new SandboxLimits();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _testTransport = testTransport;
    }

    public async Task<SandboxCommandResult> RunAsync(SandboxCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var client = await EnsureClientAsync(cancellationToken).ConfigureAwait(false);
        var sdkCommand = new SdkSandboxCommand(command.Argv, ToWorkspaceRelativeDirectory(command.WorkingDirectory));

        // Bound every command with a per-command timeout (PR #121 H4): a command that runs longer than the
        // configured limit is cancelled client-side so untrusted PR code cannot hang the poller. This
        // complements the gateway-side ExecutionTimeout (configured below) — either surfaces as the SAME
        // TimeoutException the old orchestrator threw.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_limits.CommandTimeout);

        SdkSandboxCommandResult sdkResult;
        try
        {
            sdkResult = await client.ExecuteAsync(_sessionId, sdkCommand, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw CommandTimedOut();
        }
        catch (SandboxException ex) when (ex.Kind == SandboxErrorKind.ExecutionTimeout)
        {
            throw CommandTimedOut();
        }

        // Cap BOTH streams before they are materialized into the result, so a command that emits megabytes
        // cannot be persisted to SQLite or fed wholesale to the agent (PR #121 H4). Unlike the old
        // sentinel-parsing runner (which could never separate stderr and always returned it empty), the SDK
        // captures a genuine exit code and distinct stdout/stderr — surfacing the real, capped stderr is a
        // strict improvement that no test depends on being empty.
        return new SandboxCommandResult(
            sdkResult.ExitCode,
            _limits.CapOutput(sdkResult.StandardOutput),
            _limits.CapOutput(sdkResult.StandardError)
        );
    }

    public async Task<string?> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var client = await EnsureClientAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await client
                .ReadTextFileAsync(_sessionId, ToWorkspaceRelativePath(path), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SandboxException ex) when (ex.Kind == SandboxErrorKind.NotFound)
        {
            return null; // Missing file — the contract is null, not throw.
        }
    }

    public async Task WriteFileAsync(string path, string content, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var client = await EnsureClientAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await client
                .WriteTextFileAsync(_sessionId, ToWorkspaceRelativePath(path), content, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SandboxException ex)
        {
            throw new IOException($"Sandbox write of '{path}' failed: {ex.Message}", ex);
        }
    }

    public async Task<IReadOnlyList<string>> ListFilesAsync(string directory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var client = await EnsureClientAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await client
                .ListDirectoryAsync(_sessionId, ToWorkspaceRelativePath(directory), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SandboxException ex) when (ex.Kind == SandboxErrorKind.NotFound)
        {
            return []; // Missing directory — the contract is an empty listing, not throw.
        }
    }

    /// <summary>
    /// Lazily builds the owned <see cref="SandboxClient"/> on first use (thread-safe). Construction does
    /// no gateway I/O; deferring it keeps an adapter constructed with an as-yet-unresolved credential
    /// (e.g. the provisioner's per-session cache priming) inert until a command/file op actually runs.
    /// </summary>
    private async Task<SandboxClient> EnsureClientAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
        {
            return _client;
        }

        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _client ??= BuildClient();
        }
        finally
        {
            _ = _connectGate.Release();
        }

        return _client;
    }

    /// <summary>
    /// Builds the per-app SDK options from the daemon's gateway config + resolved credential. The daemon
    /// pre-validates its app key as standard base64 (Program.cs / ReviewBotInitCommand) before building the
    /// <see cref="SandboxCredential"/>, so <see cref="SandboxCredential.AppKey"/> is always empty (keyless)
    /// or valid — it is passed straight through as <c>clientSecret</c>, yielding the IDENTICAL
    /// X-Sbx-App-Id/X-Sbx-App-Key stamping the old <c>BuildTransportHeaders</c> produced.
    /// <c>ExecutionTimeout</c> is the daemon's per-command timeout (mapped to the gateway Bash timeout);
    /// <c>TransportTimeout</c> adds a grace so a single call is never aborted before that deadline. Plain
    /// HTTP is allowed because the daemon's gateway is a local/dev endpoint, exactly as the old MCP
    /// transport assumed.
    /// </summary>
    private SandboxClient BuildClient()
    {
        var options = new SandboxClientOptions(
            new Uri(_gatewayBaseUrl, UriKind.Absolute),
            _credential.AppId,
            _credential.AppKey,
            executionTimeout: _limits.CommandTimeout,
            transportTimeout: _limits.CommandTimeout + S_transportGrace,
            allowInsecureDevelopmentTransport: true
        );

        // Production takes an owned socket; a test supplies a scripted transport we borrow (and dispose
        // in DisposeAsync, since the SDK never disposes a borrowed HttpClient).
        SandboxClient client;
        if (_testTransport is not null)
        {
            _borrowedHttpClient = new HttpClient(_testTransport, disposeHandler: false);
            client = new SandboxClient(options, _borrowedHttpClient);
        }
        else
        {
            client = new SandboxClient(options);
        }

        _logger.LogInformation(
            "Bound typed sandbox client to {Gateway} for session {SessionId}",
            _gatewayBaseUrl,
            _sessionId
        );
        return client;
    }

    private TimeoutException CommandTimedOut()
    {
        _logger.LogWarning(
            "Sandbox command exceeded the {Timeout} per-command timeout and was cancelled.",
            _limits.CommandTimeout
        );
        return new TimeoutException($"Sandbox command exceeded the configured {_limits.CommandTimeout} timeout.");
    }

    /// <summary>Maps an absolute daemon working directory to the SDK's workspace-relative form (<c>null</c> ⇒ workspace root).</summary>
    private static string? ToWorkspaceRelativeDirectory(string? workingDirectory) =>
        string.IsNullOrEmpty(workingDirectory) ? null : StripWorkspaceRoot(workingDirectory);

    /// <summary>Maps an absolute daemon file/dir path to the SDK's workspace-relative form.</summary>
    private static string ToWorkspaceRelativePath(string path) => StripWorkspaceRoot(path);

    /// <summary>
    /// Strips a leading <c>/workspace</c> mount prefix so an absolute daemon path resolves to the same
    /// container location under the SDK's own workspace root. A path that is already relative, or absolute
    /// but outside the mount, is returned unchanged for the SDK to validate.
    /// </summary>
    private static string StripWorkspaceRoot(string path)
    {
        if (string.Equals(path, WorkspaceRoot, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return path.StartsWith(WorkspaceRoot + "/", StringComparison.Ordinal)
            ? path[(WorkspaceRoot.Length + 1)..]
            : path;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _client?.Dispose();
        _borrowedHttpClient?.Dispose();
        _connectGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
