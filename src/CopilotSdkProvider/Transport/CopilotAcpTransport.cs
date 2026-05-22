using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.LmCore.AgentRuntime;
using AchieveAi.LmDotnetTools.ProcessLauncher;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.CopilotSdkProvider.Transport;

/// <summary>
/// JSON-RPC 2.0 over stdio transport for the Copilot CLI in ACP mode
/// (<c>copilot --acp --stdio</c>). Every message includes the <c>"jsonrpc":"2.0"</c>
/// header. Property names use camelCase to match the Copilot ACP wire format.
/// </summary>
internal sealed class CopilotAcpTransport : IAsyncDisposable
{
    private readonly CopilotSdkOptions _options;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<long, PendingRequest> _pendingRequests = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private IProcessHandle? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private StreamReader? _stderr;
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private CancellationTokenSource? _processCts;
    private Func<string, JsonElement?, CancellationToken, Task<JsonElement>>? _requestHandler;
    private Action<string, JsonElement?>? _notificationHandler;
    private CopilotRpcTraceWriter? _rpcTraceWriter;
    private string? _mcpConfigTempFilePath;
    private long _nextRpcId;
    private int _closeSignalSent;
    private int _disposed;

    public CopilotAcpTransport(CopilotSdkOptions options, ILogger? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    public bool IsRunning => _process is { HasExited: false };

    public event Action<Exception?>? Closed;

    public Task StartAsync(
        string workingDirectory,
        string? apiKey,
        string? baseUrl,
        Func<string, JsonElement?, CancellationToken, Task<JsonElement>> requestHandler,
        Action<string, JsonElement?> notificationHandler,
        CancellationToken ct)
    {
        return StartAsync(
            workingDirectory,
            apiKey,
            baseUrl,
            mcpServers: null,
            requestHandler,
            notificationHandler,
            ct);
    }

    public async Task StartAsync(
        string workingDirectory,
        string? apiKey,
        string? baseUrl,
        IReadOnlyDictionary<string, McpServerConfig>? mcpServers,
        Func<string, JsonElement?, CancellationToken, Task<JsonElement>> requestHandler,
        Action<string, JsonElement?> notificationHandler,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(requestHandler);
        ArgumentNullException.ThrowIfNull(notificationHandler);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

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
                _rpcTraceWriter = new CopilotRpcTraceWriter(_options.RpcTraceFilePath, _options.CopilotSessionId, _logger);
                _logger?.LogInformation(
                    "{event_type} {event_status} {provider} {provider_mode} {copilot_session_id} {trace_file}",
                    "copilot.rpc_trace",
                    "enabled",
                    _options.Provider,
                    _options.ProviderMode,
                    _options.CopilotSessionId ?? string.Empty,
                    _options.RpcTraceFilePath);
            }

            var envOverrides = new Dictionary<string, string?>(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                envOverrides["COPILOT_API_KEY"] = apiKey;
                envOverrides["GITHUB_TOKEN"] = apiKey;
            }

            if (!string.IsNullOrWhiteSpace(baseUrl))
            {
                envOverrides["COPILOT_BASE_URL"] = baseUrl;
            }

            // Default args; appended with --additional-mcp-config=@<file> when
            // external MCP servers are configured (Copilot CLI's ACP session/new
            // wire shape is stdio-hostile, so all MCP routing flows through the
            // CLI's own config-file loader instead).
            var arguments = new List<string> { "--acp", "--stdio" };
            var hostPaths = new List<HostPathReference>
            {
                new(workingDirectory, HostPathKind.WorkingDirectory),
            };

            var mcpConfigPath = TryWriteMcpConfigFile(mcpServers);
            if (mcpConfigPath != null)
            {
                _mcpConfigTempFilePath = mcpConfigPath;
                arguments.Add($"--additional-mcp-config=@{mcpConfigPath}");
                hostPaths.Add(new HostPathReference(mcpConfigPath, HostPathKind.McpConfigFile));
            }

            var launchRequest = new ProcessLaunchRequest
            {
                Agent = CliAgentKind.Copilot,
                ExecutableHint = _options.CopilotCliPath,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                EnvironmentOverrides = envOverrides,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                HostPaths = hostPaths,
            };

            try
            {
                _process = await _options.ProcessLauncher.LaunchAsync(launchRequest, ct).ConfigureAwait(false);
            }
            catch (ProcessLauncherException ex)
            {
                CleanupMcpConfigTempFile();
                throw new InvalidOperationException(
                    $"Failed to start Copilot CLI (configured as '{_options.CopilotCliPath}'). Ensure Copilot CLI is installed and accessible.",
                    ex);
            }
            catch (Exception ex)
            {
                CleanupMcpConfigTempFile();
                throw new InvalidOperationException(
                    $"Failed to start Copilot CLI (configured as '{_options.CopilotCliPath}'). Ensure Copilot CLI is installed and accessible.",
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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (!IsRunning)
        {
            throw new InvalidOperationException("Copilot ACP process is not running.");
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
            throw new TimeoutException($"Timed out waiting for Copilot ACP response for method '{method}'.");
        }
        finally
        {
            _ = _pendingRequests.TryRemove(id, out _);
        }
    }

    public Task SendNotificationAsync(string method, object? parameters, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

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
                    _ = await process.WaitForExitAsync(timeoutCts.Token);
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
                    // Read loop errors are surfaced via the Closed event.
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

            FailPendingRequests(new InvalidOperationException("Copilot ACP transport has been stopped."));
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

            CleanupMcpConfigTempFile();

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
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

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
                SignalConnectionClosed(new InvalidOperationException("Copilot ACP stdout closed."));
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
                    "copilot.acp_server.stderr",
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
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "{event_type} {event_status} {provider} {provider_mode}",
                "copilot.acp_server.stderr_loop",
                "failed",
                _options.Provider,
                _options.ProviderMode);
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
                "copilot.acp_server.parse",
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
                            "copilot.acp_server.notification",
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
            _logger?.LogWarning(
                ex,
                "{event_type} {event_status} {provider} {provider_mode} {method}",
                "copilot.acp_server.request",
                "failed",
                _options.Provider,
                _options.ProviderMode,
                method);
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
            throw new InvalidOperationException("Copilot ACP stdin is not available.");
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
                throw new InvalidOperationException("Copilot ACP stdin is not available.");
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
                "copilot.rpc_trace.write",
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

        FailPendingRequests(ex ?? new InvalidOperationException("Copilot ACP transport closed unexpectedly."));
        Closed?.Invoke(ex);
    }

    private void FailPendingRequests(Exception ex)
    {
        while (true)
        {
            var keys = _pendingRequests.Keys;
            if (keys.Count == 0)
            {
                return;
            }

            var key = keys.First();
            if (_pendingRequests.TryRemove(key, out var pending))
            {
                pending.TrySetException(ex);
            }
        }
    }

    /// <summary>
    /// Serialise external MCP servers to a per-session JSON config file and return the
    /// host-side path. The Copilot CLI consumes this via <c>--additional-mcp-config=@&lt;path&gt;</c>
    /// and accepts stdio entries there (unlike the ACP <c>session/new</c> mcpServers
    /// array, which is HTTP/SSE-only). Returns <c>null</c> when the map is empty or
    /// every entry is invalid — callers should NOT emit the flag in that case so
    /// session-startup behaviour is unchanged from before this feature.
    /// </summary>
    internal string? TryWriteMcpConfigFile(IReadOnlyDictionary<string, McpServerConfig>? mcpServers)
    {
        var json = BuildMcpConfigFileContent(mcpServers);
        if (json == null)
        {
            return null;
        }

        var path = Path.Combine(Path.GetTempPath(), $"copilot-mcp-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        _logger?.LogInformation(
            "{event_type} {event_status} {provider} {provider_mode} {server_count} {path}",
            "copilot.mcp.config_file.write",
            "completed",
            _options.Provider,
            _options.ProviderMode,
            mcpServers?.Count ?? 0,
            path);

        return path;
    }

    /// <summary>
    /// Build the JSON document Copilot CLI expects at <c>--additional-mcp-config</c>.
    /// Schema: <c>{ "mcpServers": { "&lt;name&gt;": { "type", "command", "args", "env": { K: V } } } }</c>
    /// for stdio entries, with <c>"type", "url", "headers": { K: V }</c> for http/sse.
    /// Returns <c>null</c> when no valid entries remain (so callers can short-circuit
    /// the flag/file emission).
    /// </summary>
    internal string? BuildMcpConfigFileContent(IReadOnlyDictionary<string, McpServerConfig>? mcpServers)
    {
        if (mcpServers is null || mcpServers.Count == 0)
        {
            return null;
        }

        var entries = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (name, config) in mcpServers)
        {
            if (string.IsNullOrWhiteSpace(name) || config is null)
            {
                _logger?.LogWarning(
                    "{event_type} {event_status} {server_name} {reason}",
                    "copilot.mcp.server.skip",
                    "skipped",
                    name ?? "<null>",
                    "name_or_config_null");
                continue;
            }

            var transport = string.IsNullOrWhiteSpace(config.Type) ? "stdio" : config.Type;
            var entry = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = transport,
            };

            if (string.Equals(transport, "http", StringComparison.OrdinalIgnoreCase)
                || string.Equals(transport, "sse", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(config.Url))
                {
                    _logger?.LogWarning(
                        "{event_type} {event_status} {server_name} {reason}",
                        "copilot.mcp.server.skip",
                        "skipped",
                        name,
                        "http_missing_url");
                    continue;
                }

                entry["url"] = config.Url;
                if (config.Headers is { Count: > 0 })
                {
                    entry["headers"] = config.Headers;
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(config.Command))
                {
                    _logger?.LogWarning(
                        "{event_type} {event_status} {server_name} {reason}",
                        "copilot.mcp.server.skip",
                        "skipped",
                        name,
                        "stdio_missing_command");
                    continue;
                }

                entry["command"] = config.Command;
                entry["args"] = config.Args ?? [];
                if (config.Env is { Count: > 0 })
                {
                    entry["env"] = config.Env;
                }
            }

            entries[name] = entry;
        }

        if (entries.Count == 0)
        {
            return null;
        }

        var document = new Dictionary<string, object> { ["mcpServers"] = entries };
        return JsonSerializer.Serialize(document, _json);
    }

    private void CleanupMcpConfigTempFile()
    {
        var path = _mcpConfigTempFilePath;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        _mcpConfigTempFilePath = null;
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            // Cleanup is best-effort: stale files in the OS temp directory are
            // recovered by the OS on reboot/cleanup sweeps. Log and continue so
            // shutdown isn't blocked by an I/O error.
            _logger?.LogWarning(
                ex,
                "{event_type} {event_status} {provider} {provider_mode} {path}",
                "copilot.mcp.config_file.cleanup",
                "failed",
                _options.Provider,
                _options.ProviderMode,
                path);
        }
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
