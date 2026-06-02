using System.Diagnostics;
using System.Text;

namespace LmStreaming.Sample.Services;

/// <summary>
/// Owns the lifecycle of the Rust sandbox MCP gateway process for the sample app.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a singleton <see cref="IHostedService"/> so it boots eagerly during
/// <c>Host.StartAsync</c>. On startup it either <em>adopts</em> an already-running gateway
/// (detected via the health endpoint) or <em>spawns</em> one when
/// <see cref="SandboxGatewayOptions.AutoSpawn"/> is enabled, then polls until the gateway
/// reports healthy.
/// </para>
/// <para>
/// This type owns ONLY the gateway process and the base URL. Sandbox sessions are owned by
/// <see cref="SandboxSessionRegistry"/>, which depends on this type for
/// <see cref="GatewayBaseUrl"/>.
/// </para>
/// <para>
/// When the gateway was spawned by us we kill it on shutdown; when it was adopted we leave it
/// running (we did not start it, so it is not ours to stop).
/// </para>
/// </remarks>
public sealed class SandboxGatewayLifetime : IHostedService, IAsyncDisposable
{
    private const int DefaultGatewayPort = 3000;
    private static readonly TimeSpan HealthProbeTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SpawnReadyTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SpawnPollInterval = TimeSpan.FromMilliseconds(500);

    private readonly SandboxGatewayOptions _options;
    private readonly ILogger<SandboxGatewayLifetime> _logger;
    private readonly HttpClient _httpClient;

    // Serialises adopt-or-spawn so concurrent EnsureReadyAsync callers never spawn twice.
    private readonly SemaphoreSlim _readyGate = new(1, 1);

    // True only when this process spawned the gateway; gates whether StopAsync kills it.
    private bool _ownsProcess;
    private Process? _process;
    private bool _disposed;

    // True once the gateway has been observed healthy (adopted or spawned); lets EnsureReadyAsync
    // short-circuit without re-probing on the hot path.
    private bool _ready;

    /// <summary>
    /// Initialises the lifetime with the gateway options, a logger, and the
    /// <see cref="HttpClient"/> used for health probes during startup.
    /// </summary>
    /// <param name="options">Strongly-typed gateway configuration.</param>
    /// <param name="logger">Logger for lifecycle diagnostics.</param>
    /// <param name="httpClient">Client used to probe the gateway health endpoint.</param>
    public SandboxGatewayLifetime(
        SandboxGatewayOptions options,
        ILogger<SandboxGatewayLifetime> logger,
        HttpClient httpClient
    )
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    /// <summary>
    /// Base URL of the gateway (trailing slash trimmed). Consumers append paths such as
    /// <c>/api/v1/sandboxes</c> to this value.
    /// </summary>
    public string GatewayBaseUrl => _options.BaseUrl.TrimEnd('/');

    /// <summary>
    /// True when this process spawned (and therefore owns) the gateway, as opposed to having
    /// adopted an already-running one.
    /// </summary>
    public bool OwnsProcess => _ownsProcess;

    /// <inheritdoc />
    /// <remarks>
    /// Startup is best-effort and NON-fatal: the app must boot for non-workspace modes even when
    /// the gateway is not configured or not reachable. Any adopt/spawn failure is logged as a
    /// warning here; the hard, actionable error is deferred to <see cref="EnsureReadyAsync"/>,
    /// which Workspace Agent mode calls when it actually needs the gateway.
    /// </remarks>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown during startup — propagate cancellation, don't swallow it.
            throw;
        }
        catch (Exception ex)
        {
            // Non-fatal at boot: other modes don't need the gateway. The clear error resurfaces
            // from EnsureReadyAsync when Workspace Agent mode is first used.
            _logger.LogWarning(
                ex,
                "Sandbox gateway is not ready at startup ({GatewayBaseUrl}); Workspace Agent mode "
                    + "will retry on first use. Other modes are unaffected.",
                GatewayBaseUrl
            );
        }
    }

    /// <summary>
    /// Ensures the gateway is healthy, adopting an already-running one or spawning it on demand.
    /// Idempotent and safe to call concurrently. Throws a clear, actionable
    /// <see cref="InvalidOperationException"/> only if a healthy gateway still cannot be reached.
    /// </summary>
    /// <param name="ct">Cancellation token observed while probing/spawning.</param>
    public async Task EnsureReadyAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_ready)
        {
            return;
        }

        await _readyGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ready)
            {
                return;
            }

            // 1. Adopt an already-running gateway when one answers the health endpoint.
            if (await IsHealthyAsync(HealthProbeTimeout, ct).ConfigureAwait(false))
            {
                _logger.LogInformation("Adopted existing sandbox gateway at {GatewayBaseUrl}", GatewayBaseUrl);
                _ready = true;
                return;
            }

            // 2. No gateway is running and auto-spawn is disabled — fail with actionable guidance.
            if (!_options.AutoSpawn)
            {
                throw new InvalidOperationException(
                    $"No sandbox gateway is reachable at {GatewayBaseUrl} and AutoSpawn is disabled. "
                        + "Build the gateway with 'cargo build --release', then run it with "
                        + "SANDBOX_BACKEND=local, LOCAL_AGENT_CLI_PATH=<agent-cli>, BIND_ADDRESS=127.0.0.1, "
                        + $"PORT={ResolvePort()} and LOCAL_SANDBOX_APPCONTAINER=false — or set "
                        + $"{SandboxGatewayOptions.SectionName}:AutoSpawn=true to have the app spawn it."
                );
            }

            // 3. Spawn the gateway ourselves and wait until it is healthy.
            await SpawnAndWaitAsync(ct).ConfigureAwait(false);
            _ready = true;
        }
        finally
        {
            _ = _readyGate.Release();
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Adopted gateways are left running; only kill what we spawned.
        if (!_ownsProcess || _process is null)
        {
            return Task.CompletedTask;
        }

        TryKillProcess();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;

        if (_ownsProcess)
        {
            TryKillProcess();
        }

        _process?.Dispose();
        _process = null;
        _readyGate.Dispose();
        _httpClient.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Spawns the gateway process with the required environment and polls the health endpoint
    /// until it reports healthy or the ready timeout elapses.
    /// </summary>
    private async Task SpawnAndWaitAsync(CancellationToken cancellationToken)
    {
        var exePath = RequireFile(
            _options.GatewayExePath,
            nameof(SandboxGatewayOptions.GatewayExePath),
            "gateway executable"
        );
        var agentCliPath = RequireFile(_options.AgentCliPath, nameof(SandboxGatewayOptions.AgentCliPath), "agent CLI");

        // Dispose any process object left over from a prior failed spawn attempt before replacing it.
        _process?.Dispose();
        _process = null;

        var psi = BuildStartInfo(exePath, agentCliPath);
        var process =
            Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start sandbox gateway process '{exePath}'.");

        _process = process;
        _ownsProcess = true;

        // Drain stdout/stderr asynchronously so the child never blocks on a full pipe; mirror
        // both streams into the app log so gateway startup failures are diagnosable.
        process.OutputDataReceived += (_, e) => LogGatewayLine("stdout", e.Data);
        process.ErrorDataReceived += (_, e) => LogGatewayLine("stderr", e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        _logger.LogInformation(
            "Spawned sandbox gateway (pid {Pid}) at {GatewayBaseUrl}; waiting for healthy",
            process.Id,
            GatewayBaseUrl
        );

        if (await WaitForHealthyAsync(cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation("Sandbox gateway is healthy at {GatewayBaseUrl}", GatewayBaseUrl);
            return;
        }

        // Never went healthy in time. Surface whether the process already died and tear it down.
        var exited = process.HasExited;
        TryKillProcess();
        throw new InvalidOperationException(
            $"Sandbox gateway at {GatewayBaseUrl} did not become healthy within "
                + $"{SpawnReadyTimeout.TotalSeconds:0}s"
                + (exited ? $" (process exited with code {SafeExitCode(process)})." : ".")
                + " Check the application log for the gateway's stdout/stderr output."
        );
    }

    /// <summary>
    /// Builds the <see cref="ProcessStartInfo"/> for the gateway, including the exact environment
    /// the gateway requires for the local backend.
    /// </summary>
    private ProcessStartInfo BuildStartInfo(string exePath, string agentCliPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory,
        };

        psi.Environment["SANDBOX_BACKEND"] = "local";
        psi.Environment["LOCAL_AGENT_CLI_PATH"] = agentCliPath;
        psi.Environment["BIND_ADDRESS"] = "127.0.0.1";
        psi.Environment["PORT"] = ResolvePort().ToString(System.Globalization.CultureInfo.InvariantCulture);

        // The gateway defaults LOCAL_SANDBOX_APPCONTAINER to TRUE on Windows; we MUST opt out
        // explicitly because the sample runs unsandboxed against the developer's workspace.
        psi.Environment["LOCAL_SANDBOX_APPCONTAINER"] = "false";

        // SSRF loopback allowlist: lets the gateway call our http loopback auth webhook. Harmless
        // when auth providers are not configured; required when they are.
        psi.Environment["AUTH_WEBHOOK_HTTP_LOOPBACK_HOSTS"] = "127.0.0.1,localhost";

        if (!string.IsNullOrWhiteSpace(_options.WorkspaceBasePath))
        {
            psi.Environment["WORKSPACE_BASE_PATH"] = _options.WorkspaceBasePath;
        }

        if (!string.IsNullOrWhiteSpace(_options.SkillsDir))
        {
            psi.Environment["SKILLS_DIRS"] = _options.SkillsDir;
        }

        return psi;
    }

    /// <summary>
    /// Polls the health endpoint until healthy, the ready timeout elapses, the spawned process
    /// exits, or cancellation is requested.
    /// </summary>
    private async Task<bool> WaitForHealthyAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + SpawnReadyTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Bail early if the process already died — no point polling a dead gateway.
            if (_process is { HasExited: true })
            {
                return false;
            }

            if (await IsHealthyAsync(HealthProbeTimeout, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            try
            {
                await Task.Delay(SpawnPollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }

        return false;
    }

    /// <summary>
    /// Probes <c>GET {BaseUrl}/health</c> and returns true only on a success status code.
    /// Transient connection failures (e.g. refusals during startup) are treated as "not healthy"
    /// rather than propagated, so they never crash the host.
    /// </summary>
    private async Task<bool> IsHealthyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            using var response = await _httpClient
                .GetAsync($"{GatewayBaseUrl}/health", timeoutCts.Token)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown requested — let the caller observe cancellation.
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // Connection refused / per-probe timeout during startup: not healthy yet.
            return false;
        }
    }

    private void LogGatewayLine(string stream, string? line)
    {
        if (!string.IsNullOrEmpty(line))
        {
            _logger.LogDebug("[sandbox-gateway {Stream}] {Line}", stream, line);
        }
    }

    /// <summary>Resolves the gateway port from the configured base URL, defaulting to 3000.</summary>
    private int ResolvePort()
    {
        return Uri.TryCreate(GatewayBaseUrl, UriKind.Absolute, out var uri) && uri.Port > 0
            ? uri.Port
            : DefaultGatewayPort;
    }

    private static string RequireFile(string? path, string optionName, string description)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException(
                $"Cannot spawn the sandbox gateway: {SandboxGatewayOptions.SectionName}:{optionName} "
                    + $"(the {description}) is not configured."
            );
        }

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"Cannot spawn the sandbox gateway: the {description} was not found at '{path}' "
                    + $"(from {SandboxGatewayOptions.SectionName}:{optionName})."
            );
        }

        return path;
    }

    private void TryKillProcess()
    {
        var process = _process;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                // Kill the whole tree: the gateway may have spawned the agent CLI as a child.
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
                _logger.LogInformation("Stopped spawned sandbox gateway (pid {Pid})", SafePid(process));
            }
        }
        catch (Exception ex)
        {
            // Shutdown best-effort: a kill failure must not throw out of StopAsync/DisposeAsync.
            _logger.LogWarning(ex, "Failed to stop spawned sandbox gateway");
        }
        finally
        {
            _ownsProcess = false;
        }
    }

    private static int SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    private static int SafePid(Process process)
    {
        try
        {
            return process.Id;
        }
        catch
        {
            return -1;
        }
    }
}
