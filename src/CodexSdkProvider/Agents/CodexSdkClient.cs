using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Bootstrap;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Models;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.CodexSdkProvider.Agents;

public sealed class CodexSdkClient : ICodexSdkClient
{
    private readonly CodexSdkOptions _options;
    private readonly ILogger<CodexSdkClient>? _logger;
    private readonly CodexBridgeDependencyInstaller _installer;
    private readonly JsonSerializerOptions _json;

    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private StreamReader? _stderr;
    private Task? _stderrTask;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private bool _disposed;

    public bool IsRunning => _process is { HasExited: false };

    public string? CurrentCodexThreadId { get; private set; }

    public string DependencyState { get; private set; } = "unknown";

    public CodexSdkClient(
        CodexSdkOptions options,
        ILogger<CodexSdkClient>? logger = null,
        CodexBridgeDependencyInstaller? installer = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        _installer = installer ?? new CodexBridgeDependencyInstaller();
        _json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
        };
    }

    public async Task EnsureStartedAsync(CodexBridgeInitOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _operationLock.WaitAsync(ct);
        try
        {
            if (IsRunning)
            {
                return;
            }

            var bridgeScript = ResolveBridgeScriptPath();
            var bridgeDir = Path.GetDirectoryName(bridgeScript)
                ?? throw new InvalidOperationException("Bridge script directory could not be determined.");

            var nodePath = _options.NodeJsPath ?? "node";
            var npmPath = _options.NpmPath ?? "npm";

            if (_options.AutoInstallBridgeDependencies)
            {
                await _installer.EnsureInstalledAsync(bridgeDir, npmPath, _logger, ct);
                DependencyState = "ready";
            }
            else
            {
                DependencyState = "skipped";
            }

            var psi = new ProcessStartInfo
            {
                FileName = nodePath,
                Arguments = $"\"{bridgeScript}\"",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = options.WorkingDirectory ?? _options.WorkingDirectory ?? Directory.GetCurrentDirectory(),
            };

            _process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start Codex bridge process.");

            _stdin = new StreamWriter(_process.StandardInput.BaseStream, new System.Text.UTF8Encoding(false))
            {
                AutoFlush = true,
            };
            _stdout = _process.StandardOutput;
            _stderr = _process.StandardError;
            _stderrTask = Task.Run(() => MonitorStdErrAsync(ct), CancellationToken.None);

            _logger?.LogInformation(
                "{event_type} {event_status} {provider} {provider_mode} {model}",
                "codex.bridge.start",
                "started",
                _options.Provider,
                _options.ProviderMode,
                options.Model ?? _options.Model);

            var requestId = Guid.NewGuid().ToString("N");
            var initReq = new CodexBridgeRequest
            {
                Type = "init",
                RequestId = requestId,
                Options = options,
            };

            await WriteAsync(initReq, ct);

            var timeout = TimeSpan.FromMilliseconds(_options.ProcessTimeoutMs);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            while (true)
            {
                var line = await _stdout.ReadLineAsync(timeoutCts.Token);
                if (line is null)
                {
                    throw new InvalidOperationException("Codex bridge stdout closed before ready signal.");
                }

                var msg = JsonSerializer.Deserialize<CodexBridgeResponse>(line, _json);
                if (msg is null)
                {
                    continue;
                }

                if (msg.Type == "ready" && msg.RequestId == requestId)
                {
                    _logger?.LogInformation(
                        "{event_type} {event_status} {provider} {provider_mode} {bridge_request_id}",
                        "codex.bridge.ready",
                        "completed",
                        _options.Provider,
                        _options.ProviderMode,
                        requestId);
                    break;
                }

                if (msg.Type is "fatal" or "run_failed")
                {
                    throw new InvalidOperationException($"Codex bridge init failed: {msg.Error}");
                }
            }
        }
        catch (Exception ex)
        {
            DependencyState = "failed";
            _logger?.LogError(
                ex,
                "{event_type} {event_status} {provider} {provider_mode} {error_code} {exception_type}",
                "codex.bridge.start",
                "failed",
                _options.Provider,
                _options.ProviderMode,
                "bridge_start_failed",
                ex.GetType().Name);

            await ShutdownAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
            throw;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async IAsyncEnumerable<CodexTurnEventEnvelope> RunStreamingAsync(
        string input,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        if (!IsRunning || _stdin is null || _stdout is null)
        {
            throw new InvalidOperationException("Codex bridge is not running. Call EnsureStartedAsync first.");
        }

        var requestId = Guid.NewGuid().ToString("N");

        var start = Stopwatch.StartNew();
        _logger?.LogInformation(
            "{event_type} {event_status} {provider} {provider_mode} {bridge_request_id} {codex_thread_id}",
            "codex.bridge.run.started",
            "started",
            _options.Provider,
            _options.ProviderMode,
            requestId,
            CurrentCodexThreadId);

        await WriteAsync(new CodexBridgeRequest
        {
            Type = "run",
            RequestId = requestId,
            Input = input,
        }, ct);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await _stdout.ReadLineAsync(ct);
            if (line is null)
            {
                throw new InvalidOperationException("Codex bridge stdout closed while streaming run events.");
            }

            var msg = JsonSerializer.Deserialize<CodexBridgeResponse>(line, _json);
            if (msg is null || msg.RequestId != requestId)
            {
                continue;
            }

            switch (msg.Type)
            {
                case "event" when msg.Event.HasValue:
                {
                    var eventPayload = msg.Event.Value;
                    if (TryExtractThreadId(eventPayload, out var threadId))
                    {
                        CurrentCodexThreadId = threadId;
                    }

                    yield return new CodexTurnEventEnvelope
                    {
                        Type = "event",
                        Event = eventPayload,
                        RequestId = requestId,
                        ThreadId = CurrentCodexThreadId,
                    };
                    break;
                }
                case "run_completed":
                {
                    if (!string.IsNullOrEmpty(msg.ThreadId))
                    {
                        CurrentCodexThreadId = msg.ThreadId;
                    }

                    _logger?.LogInformation(
                        "{event_type} {event_status} {provider} {provider_mode} {bridge_request_id} {codex_thread_id} {latency_ms}",
                        "codex.bridge.run.completed",
                        "completed",
                        _options.Provider,
                        _options.ProviderMode,
                        requestId,
                        CurrentCodexThreadId,
                        start.ElapsedMilliseconds);
                    yield break;
                }
                case "run_failed":
                {
                    _logger?.LogError(
                        "{event_type} {event_status} {provider} {provider_mode} {bridge_request_id} {codex_thread_id} {error_code} {latency_ms}",
                        "codex.bridge.run.failed",
                        "failed",
                        _options.Provider,
                        _options.ProviderMode,
                        requestId,
                        CurrentCodexThreadId,
                        msg.ErrorCode ?? "run_failed",
                        start.ElapsedMilliseconds);
                    throw new InvalidOperationException(msg.Error ?? "Codex bridge run failed.");
                }
                case "fatal":
                    throw new InvalidOperationException(msg.Error ?? "Codex bridge fatal error.");
            }
        }
    }

    public async Task ShutdownAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        await _operationLock.WaitAsync(ct);
        try
        {
            if (_process == null)
            {
                return;
            }

            if (_stdin is not null && IsRunning)
            {
                try
                {
                    await WriteAsync(new CodexBridgeRequest
                    {
                        Type = "shutdown",
                        RequestId = Guid.NewGuid().ToString("N"),
                    }, ct);
                }
                catch
                {
                    // Ignore shutdown write errors.
                }
            }

            var wait = timeout ?? TimeSpan.FromSeconds(5);
            if (IsRunning)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(wait);
                try
                {
                    await _process.WaitForExitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }

            _logger?.LogInformation(
                "{event_type} {event_status} {provider} {provider_mode} {codex_thread_id}",
                "codex.bridge.shutdown",
                "completed",
                _options.Provider,
                _options.ProviderMode,
                CurrentCodexThreadId);
        }
        finally
        {
            _stdin?.Dispose();
            _stdout?.Dispose();
            _stderr?.Dispose();
            _process?.Dispose();
            _stdin = null;
            _stdout = null;
            _stderr = null;
            _process = null;
            _operationLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await ShutdownAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        _operationLock.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task MonitorStdErrAsync(CancellationToken ct)
    {
        if (_stderr is null)
        {
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            var line = await _stderr.ReadLineAsync(ct);
            if (line is null)
            {
                return;
            }

            _logger?.LogDebug(
                "{event_type} {event_status} {provider} {provider_mode} {message}",
                "codex.bridge.stderr",
                "observed",
                _options.Provider,
                _options.ProviderMode,
                line);
        }
    }

    private async Task WriteAsync(CodexBridgeRequest request, CancellationToken ct)
    {
        if (_stdin is null)
        {
            throw new InvalidOperationException("Bridge stdin is not available.");
        }

        var line = JsonSerializer.Serialize(request, _json);
        await _stdin.WriteLineAsync(line);
        await _stdin.FlushAsync(ct);
    }

    private string ResolveBridgeScriptPath()
    {
        if (!string.IsNullOrWhiteSpace(_options.BridgeScriptPath))
        {
            return _options.BridgeScriptPath;
        }

        var candidate = Path.Combine(AppContext.BaseDirectory, "bridge", "codex-bridge.mjs");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        candidate = Path.Combine(AppContext.BaseDirectory, "Bridge", "codex-bridge.mjs");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        throw new FileNotFoundException(
            "Could not find codex-bridge.mjs. Ensure bridge files are copied to output directory.",
            candidate);
    }

    private static bool TryExtractThreadId(JsonElement eventElement, out string? threadId)
    {
        threadId = null;

        if (!eventElement.TryGetProperty("type", out var typeProp)
            || typeProp.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var type = typeProp.GetString();
        if (!string.Equals(type, "thread.started", StringComparison.Ordinal))
        {
            return false;
        }

        if (!eventElement.TryGetProperty("thread_id", out var threadIdProp)
            || threadIdProp.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        threadId = threadIdProp.GetString();
        return !string.IsNullOrEmpty(threadId);
    }
}
