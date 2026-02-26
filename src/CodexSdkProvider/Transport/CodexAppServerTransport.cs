using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Configuration;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.CodexSdkProvider.Transport;

internal sealed class CodexAppServerTransport : IAsyncDisposable
{
    private readonly CodexSdkOptions _options;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<long, PendingRequest> _pendingRequests = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private StreamReader? _stderr;
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private CancellationTokenSource? _processCts;
    private Func<string, JsonElement?, CancellationToken, Task<JsonElement>>? _requestHandler;
    private Action<string, JsonElement?>? _notificationHandler;
    private CodexRpcTraceWriter? _rpcTraceWriter;
    private long _nextRpcId;
    private int _closeSignalSent;
    private bool _disposed;

    public CodexAppServerTransport(CodexSdkOptions options, ILogger? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    public bool IsRunning => _process is { HasExited: false };

    public event Action<Exception?>? Closed;

    public async Task StartAsync(
        string workingDirectory,
        string? apiKey,
        string? baseUrl,
        Func<string, JsonElement?, CancellationToken, Task<JsonElement>> requestHandler,
        Action<string, JsonElement?> notificationHandler,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(requestHandler);
        ArgumentNullException.ThrowIfNull(notificationHandler);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lifecycleLock.WaitAsync(ct);
        try
        {
            if (IsRunning)
            {
                return;
            }

            _requestHandler = requestHandler;
            _notificationHandler = notificationHandler;
            _ = Interlocked.Exchange(ref _closeSignalSent, 0);

            if (_options.EnableRpcTrace && !string.IsNullOrWhiteSpace(_options.RpcTraceFilePath))
            {
                _rpcTraceWriter = new CodexRpcTraceWriter(_options.RpcTraceFilePath, _options.CodexSessionId, _logger);
                _logger?.LogInformation(
                    "{event_type} {event_status} {provider} {provider_mode} {codex_session_id} {trace_file}",
                    "codex.rpc_trace",
                    "enabled",
                    _options.Provider,
                    _options.ProviderMode,
                    _options.CodexSessionId ?? string.Empty,
                    _options.RpcTraceFilePath);
            }

            var psi = new ProcessStartInfo
            {
                FileName = _options.CodexCliPath,
                Arguments = "app-server --listen stdio://",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                psi.Environment["OPENAI_API_KEY"] = apiKey;
            }

            if (!string.IsNullOrWhiteSpace(baseUrl))
            {
                psi.Environment["OPENAI_BASE_URL"] = baseUrl;
            }

            try
            {
                _process = Process.Start(psi)
                    ?? throw new InvalidOperationException("Failed to start Codex app-server process.");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to start Codex CLI '{_options.CodexCliPath}'. Ensure Codex CLI is installed and accessible.",
                    ex);
            }

            _stdin = new StreamWriter(_process.StandardInput.BaseStream, new UTF8Encoding(false))
            {
                AutoFlush = true,
            };
            _stdout = _process.StandardOutput;
            _stderr = _process.StandardError;

            _processCts = new CancellationTokenSource();
            _stdoutTask = Task.Run(() => ReadStdoutLoopAsync(_processCts.Token), CancellationToken.None);
            _stderrTask = Task.Run(() => ReadStderrLoopAsync(_processCts.Token), CancellationToken.None);
        }
        finally
        {
            _ = _lifecycleLock.Release();
        }
    }

    public async Task<JsonElement> SendRequestAsync(
        string method,
        object? parameters,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsRunning)
        {
            throw new InvalidOperationException("Codex app-server process is not running.");
        }

        var id = Interlocked.Increment(ref _nextRpcId);
        var pending = new PendingRequest(method);
        if (!_pendingRequests.TryAdd(id, pending))
        {
            throw new InvalidOperationException($"Duplicate request id generated: {id}.");
        }

        try
        {
            await WriteJsonLineAsync(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("jsonrpc", "2.0");
                writer.WriteNumber("id", id);
                writer.WriteString("method", method);
                if (parameters != null)
                {
                    writer.WritePropertyName("params");
                    WriteArbitraryValue(writer, parameters, _json);
                }

                writer.WriteEndObject();
            }, ct);
        }
        catch
        {
            _ = _pendingRequests.TryRemove(id, out _);
            throw;
        }

        var requestTimeout = timeout ?? TimeSpan.FromMilliseconds(_options.ProcessTimeoutMs);
        using var timeoutCts = new CancellationTokenSource(requestTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            return await pending.Task.WaitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _ = _pendingRequests.TryRemove(id, out _);
            throw new TimeoutException($"Timed out waiting for App Server response for method '{method}'.");
        }
        finally
        {
            _ = _pendingRequests.TryRemove(id, out _);
        }
    }

    public Task SendNotificationAsync(string method, object? parameters, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ObjectDisposedException.ThrowIf(_disposed, this);

        return WriteJsonLineAsync(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteString("method", method);
            if (parameters != null)
            {
                writer.WritePropertyName("params");
                WriteArbitraryValue(writer, parameters, _json);
            }

            writer.WriteEndObject();
        }, ct);
    }

    public async Task StopAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        await _lifecycleLock.WaitAsync(ct);
        try
        {
            var process = _process;
            if (process == null)
            {
                return;
            }

            _processCts?.Cancel();

            if (!process.HasExited)
            {
                try
                {
                    _stdin?.Close();
                }
                catch
                {
                    // Ignore close failures during shutdown.
                }

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));
                try
                {
                    await process.WaitForExitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException)
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
            }

            if (_stdoutTask != null)
            {
                try
                {
                    await _stdoutTask;
                }
                catch
                {
                    // Read loop errors are already surfaced via Closed event.
                }
            }

            if (_stderrTask != null)
            {
                try
                {
                    await _stderrTask;
                }
                catch
                {
                    // Ignore stderr monitor errors during shutdown.
                }
            }

            FailPendingRequests(new InvalidOperationException("Codex app-server transport has been stopped."));
        }
        finally
        {
            _processCts?.Dispose();
            _stdin?.Dispose();
            _stdout?.Dispose();
            _stderr?.Dispose();
            _process?.Dispose();
            if (_rpcTraceWriter != null)
            {
                await _rpcTraceWriter.DisposeAsync();
            }

            _processCts = null;
            _stdin = null;
            _stdout = null;
            _stderr = null;
            _process = null;
            _rpcTraceWriter = null;
            _stdoutTask = null;
            _stderrTask = null;

            _ = _lifecycleLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        _writeLock.Dispose();
        _lifecycleLock.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task ReadStdoutLoopAsync(CancellationToken ct)
    {
        if (_stdout == null)
        {
            return;
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await _stdout.ReadLineAsync(ct);
                if (line == null)
                {
                    break;
                }

                await HandleStdoutLineAsync(line, ct);
            }

            if (!ct.IsCancellationRequested)
            {
                SignalConnectionClosed(new InvalidOperationException("Codex app-server stdout closed."));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown path.
        }
        catch (Exception ex)
        {
            SignalConnectionClosed(ex);
        }
    }

    private async Task ReadStderrLoopAsync(CancellationToken ct)
    {
        if (_stderr == null)
        {
            return;
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await _stderr.ReadLineAsync(ct);
                if (line == null)
                {
                    break;
                }

                _logger?.LogDebug(
                    "{event_type} {event_status} {provider} {provider_mode} {message}",
                    "codex.app_server.stderr",
                    "observed",
                    _options.Provider,
                    _options.ProviderMode,
                    line);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown path.
        }
    }

    private async Task HandleStdoutLineAsync(string line, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        await WriteTraceAsync("inbound", line, ct);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(
                ex,
                "{event_type} {event_status} {provider} {provider_mode} {error_code} {line}",
                "codex.app_server.parse",
                "failed",
                _options.Provider,
                _options.ProviderMode,
                "invalid_json",
                line);
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            var hasMethod = root.TryGetProperty("method", out var methodProp)
                            && methodProp.ValueKind == JsonValueKind.String;
            var hasId = root.TryGetProperty("id", out var idProp);

            if (hasMethod && hasId)
            {
                var method = methodProp.GetString() ?? string.Empty;
                JsonElement? parameters = root.TryGetProperty("params", out var paramsProp)
                    ? paramsProp.Clone()
                    : null;
                await HandleServerRequestAsync(idProp.Clone(), method, parameters, ct);
                return;
            }

            if (hasMethod)
            {
                var method = methodProp.GetString() ?? string.Empty;
                JsonElement? parameters = root.TryGetProperty("params", out var paramsProp)
                    ? paramsProp.Clone()
                    : null;
                var handler = _notificationHandler;
                if (handler != null)
                {
                    try
                    {
                        handler(method, parameters);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(
                            ex,
                            "{event_type} {event_status} {provider} {provider_mode} {error_code} {method}",
                            "codex.app_server.notification",
                            "failed",
                            _options.Provider,
                            _options.ProviderMode,
                            "notification_handler_failed",
                            method);
                    }
                }

                return;
            }

            if (!hasId || idProp.ValueKind != JsonValueKind.Number || !idProp.TryGetInt64(out var id))
            {
                return;
            }

            if (!_pendingRequests.TryGetValue(id, out var pending))
            {
                return;
            }

            if (root.TryGetProperty("error", out var errorProp) && errorProp.ValueKind == JsonValueKind.Object)
            {
                var message = errorProp.TryGetProperty("message", out var messageProp)
                    && messageProp.ValueKind == JsonValueKind.String
                    ? messageProp.GetString()
                    : "Unknown RPC error";
                pending.TrySetException(new InvalidOperationException(message));
                return;
            }

            if (root.TryGetProperty("result", out var resultProp))
            {
                pending.TrySetResult(resultProp.Clone());
            }
            else
            {
                pending.TrySetResult(CreateEmptyObject());
            }
        }
    }

    private async Task HandleServerRequestAsync(
        JsonElement requestId,
        string method,
        JsonElement? parameters,
        CancellationToken ct)
    {
        var handler = _requestHandler;
        if (handler == null)
        {
            await SendErrorAsync(requestId, -32601, "No server request handler is configured.", ct);
            return;
        }

        try
        {
            var response = await handler(method, parameters, ct);
            await SendResultAsync(requestId, response, ct);
        }
        catch (Exception ex)
        {
            await SendErrorAsync(requestId, -32000, ex.Message, ct);
        }
    }

    private Task SendResultAsync(JsonElement requestId, JsonElement response, CancellationToken ct)
    {
        return WriteJsonLineAsync(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            requestId.WriteTo(writer);
            writer.WritePropertyName("result");
            if (response.ValueKind == JsonValueKind.Undefined)
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
            }
            else
            {
                response.WriteTo(writer);
            }

            writer.WriteEndObject();
        }, ct);
    }

    private Task SendErrorAsync(JsonElement requestId, int code, string message, CancellationToken ct)
    {
        return WriteJsonLineAsync(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            requestId.WriteTo(writer);
            writer.WritePropertyName("error");
            writer.WriteStartObject();
            writer.WriteNumber("code", code);
            writer.WriteString("message", message);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }, ct);
    }

    private async Task WriteJsonLineAsync(Action<Utf8JsonWriter> writeAction, CancellationToken ct)
    {
        if (_stdin == null)
        {
            throw new InvalidOperationException("Codex app-server stdin is not available.");
        }

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writeAction(writer);
            writer.Flush();
        }

        var line = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
        await WriteTraceAsync("outbound", line, ct);

        ct.ThrowIfCancellationRequested();
        await _writeLock.WaitAsync(ct);
        try
        {
            if (_stdin == null)
            {
                throw new InvalidOperationException("Codex app-server stdin is not available.");
            }

            await _stdin.WriteLineAsync(line);
            await _stdin.FlushAsync(ct);
        }
        finally
        {
            _ = _writeLock.Release();
        }
    }

    private async Task WriteTraceAsync(string direction, string line, CancellationToken ct)
    {
        if (_rpcTraceWriter == null)
        {
            return;
        }

        try
        {
            await _rpcTraceWriter.WriteAsync(direction, line, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "{event_type} {event_status} {provider} {provider_mode} {error_code}",
                "codex.rpc_trace.write",
                "failed",
                _options.Provider,
                _options.ProviderMode,
                "trace_write_failed");
        }
    }

    private void SignalConnectionClosed(Exception? ex)
    {
        if (Interlocked.Exchange(ref _closeSignalSent, 1) != 0)
        {
            return;
        }

        FailPendingRequests(ex ?? new InvalidOperationException("Codex app-server transport closed unexpectedly."));
        Closed?.Invoke(ex);
    }

    private void FailPendingRequests(Exception ex)
    {
        foreach (var (_, pending) in _pendingRequests.ToArray())
        {
            pending.TrySetException(ex);
        }

        _pendingRequests.Clear();
    }

    private static void WriteArbitraryValue(Utf8JsonWriter writer, object value, JsonSerializerOptions jsonOptions)
    {
        if (value is JsonElement jsonElement)
        {
            jsonElement.WriteTo(writer);
            return;
        }

        JsonSerializer.Serialize(writer, value, jsonOptions);
    }

    private static JsonElement CreateEmptyObject()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }

    private sealed class PendingRequest
    {
        private readonly TaskCompletionSource<JsonElement> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PendingRequest(string method)
        {
            Method = method;
        }

        public string Method { get; }

        public Task<JsonElement> Task => _tcs.Task;

        public void TrySetResult(JsonElement result)
        {
            _ = _tcs.TrySetResult(result);
        }

        public void TrySetException(Exception ex)
        {
            _ = _tcs.TrySetException(ex);
        }
    }
}
