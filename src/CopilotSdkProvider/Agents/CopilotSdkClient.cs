using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Models;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Transport;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.CopilotSdkProvider.Agents;

/// <summary>
/// Default <see cref="ICopilotSdkClient"/> implementation. Owns the ACP transport
/// lifecycle, the <c>initialize</c>/<c>session/new</c>/model allowlist probe sequence,
/// and per-turn <c>session/prompt</c> streaming. Intentionally leaner than the Codex
/// client: no internal-tool span tracking, no MCP config, no feature-flag routing.
/// </summary>
public sealed class CopilotSdkClient : ICopilotSdkClient
{
    private readonly CopilotSdkOptions _options;
    private readonly ILogger<CopilotSdkClient>? _logger;
    private readonly JsonSerializerOptions _json;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly object _runStateLock = new();

    private CopilotAcpTransport? _transport;
    private ActiveRunState? _activeRun;
    private CopilotBridgeInitOptions? _startupOptions;
    private Func<CopilotDynamicToolCallRequest, CancellationToken, Task<CopilotDynamicToolCallResponse>>? _dynamicToolExecutor;
    private int _isShuttingDown;
    private int _disposed;

    public bool IsRunning => Volatile.Read(ref _transport) is { IsRunning: true };

    public string? CurrentCopilotSessionId { get; private set; }

    public string DependencyState { get; private set; } = "unknown";

    public CopilotSdkClient(CopilotSdkOptions options, ILogger<CopilotSdkClient>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        _json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
    }

    public void ConfigureDynamicToolExecutor(
        Func<CopilotDynamicToolCallRequest, CancellationToken, Task<CopilotDynamicToolCallResponse>>? executor)
    {
        _dynamicToolExecutor = executor;
    }

    public Task EnsureStartedAsync(CopilotBridgeInitOptions options, CancellationToken ct = default)
    {
        return StartOrResumeSessionAsync(options, ct);
    }

    public async Task StartOrResumeSessionAsync(CopilotBridgeInitOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _operationLock.WaitAsync(ct);
        try
        {
            if (IsRunning)
            {
                return;
            }

            _ = Interlocked.Exchange(ref _isShuttingDown, 0);

            var effectiveOptions = ResolveEffectiveOptions(options);
            var timeout = TimeSpan.FromMilliseconds(Math.Max(_options.AcpStartupTimeoutMs, 5_000));
            var copilotCliVersion = await CopilotVersionChecker.EnsureCopilotCliVersionAsync(
                _options.CopilotCliPath,
                _options.CopilotCliMinVersion,
                timeout,
                ct);

            var workingDirectory = effectiveOptions.WorkingDirectory
                ?? _options.WorkingDirectory
                ?? Directory.GetCurrentDirectory();
            var transport = new CopilotAcpTransport(_options, _logger);
            transport.Closed += OnTransportClosed;

            await transport.StartAsync(
                workingDirectory,
                effectiveOptions.ApiKey,
                effectiveOptions.BaseUrl,
                HandleServerRequestAsync,
                HandleServerNotification,
                ct);

            Volatile.Write(ref _transport, transport);
            _startupOptions = effectiveOptions;

            var startupTimeout = TimeSpan.FromMilliseconds(_options.AcpStartupTimeoutMs);

            var initializeResponse = await transport.SendRequestAsync(
                "initialize",
                new
                {
                    clientInfo = new
                    {
                        name = "lm-dotnet-tools-copilot-client",
                        version = "0.3.0",
                    },
                    capabilities = new
                    {
                        dynamicTools = true,
                    },
                },
                ct,
                startupTimeout);

            if (_options.ModelAllowlistProbeEnabled)
            {
                EnsureModelAllowed(initializeResponse, effectiveOptions.Model);
            }

            var sessionResponse = await transport.SendRequestAsync(
                "session/new",
                BuildSessionNewParams(effectiveOptions),
                ct,
                startupTimeout);

            CurrentCopilotSessionId = CopilotEventParser.ExtractSessionId(sessionResponse) ?? effectiveOptions.SessionId;
            DependencyState = "ready";

            _logger?.LogInformation(
                "{event_type} {event_status} {provider} {provider_mode} {copilot_cli_path} {copilot_cli_version} {copilot_session_id}",
                "copilot.acp_server.ready",
                "completed",
                _options.Provider,
                _options.ProviderMode,
                _options.CopilotCliPath,
                copilotCliVersion,
                CurrentCopilotSessionId);
        }
        catch (Exception ex)
        {
            DependencyState = "failed";
            _logger?.LogError(ex, "Failed to start or resume Copilot ACP session");
            await ShutdownInternalAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
            throw;
        }
        finally
        {
            _ = _operationLock.Release();
        }
    }

    public async IAsyncEnumerable<CopilotTurnEventEnvelope> RunStreamingAsync(
        string input,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        var transport = Volatile.Read(ref _transport);
        if (transport is null || !transport.IsRunning)
        {
            throw new InvalidOperationException("Copilot ACP process is not running. Call StartOrResumeSessionAsync first.");
        }

        if (string.IsNullOrWhiteSpace(CurrentCopilotSessionId))
        {
            throw new InvalidOperationException("Copilot session is not initialized.");
        }

        var requestId = Guid.NewGuid().ToString("N");
        var runState = new ActiveRunState(requestId, CurrentCopilotSessionId);
        if (!TryActivateRun(runState))
        {
            throw new InvalidOperationException("A Copilot run is already in progress.");
        }

        var stopwatch = Stopwatch.StartNew();

        _logger?.LogInformation(
            "{event_type} {event_status} {provider} {provider_mode} {bridge_request_id} {copilot_session_id}",
            "copilot.bridge.run.started",
            "started",
            _options.Provider,
            _options.ProviderMode,
            requestId,
            CurrentCopilotSessionId);

        var runTask = ExecuteTurnAsync(transport, input, runState, requestId, stopwatch, ct);
        try
        {
            await foreach (var envelope in runState.ReadAllEventsAsync(ct))
            {
                yield return envelope;
            }

            await runTask;
        }
        finally
        {
            DeactivateRun(runState);
        }
    }

    private async Task ExecuteTurnAsync(
        CopilotAcpTransport transport,
        string input,
        ActiveRunState runState,
        string requestId,
        Stopwatch stopwatch,
        CancellationToken ct)
    {
        try
        {
            var timeout = TimeSpan.FromMilliseconds(Math.Max(_options.TurnCompletionTimeoutMs, 1_000));

            var promptResponse = await transport.SendRequestAsync(
                "session/prompt",
                BuildSessionPromptParams(runState.SessionId, input),
                ct,
                timeout);

            // Emit a synthetic final envelope so the translator can produce a UsageMessage
            // and any terminal processing.
            runState.TryWriteEvent(new CopilotTurnEventEnvelope
            {
                Type = "event",
                Event = WrapEvent("session/prompt/completed", promptResponse),
                RequestId = runState.RequestId,
                SessionId = runState.SessionId,
            });

            runState.TryComplete();

            _logger?.LogInformation(
                "{event_type} {event_status} {provider} {provider_mode} {bridge_request_id} {copilot_session_id} {latency_ms}",
                "copilot.bridge.run.completed",
                "completed",
                _options.Provider,
                _options.ProviderMode,
                requestId,
                runState.SessionId,
                stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await InterruptTurnAsync(CancellationToken.None);
            runState.TryFail(new OperationCanceledException("Copilot turn was cancelled."));
            throw;
        }
        catch (Exception ex)
        {
            runState.TryFail(ex);
            _logger?.LogError(
                ex,
                "{event_type} {event_status} {provider} {provider_mode} {bridge_request_id} {copilot_session_id} {error_code} {latency_ms}",
                "copilot.bridge.run.failed",
                "failed",
                _options.Provider,
                _options.ProviderMode,
                requestId,
                runState.SessionId,
                "run_failed",
                stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    public async Task InterruptTurnAsync(CancellationToken ct = default)
    {
        var transport = Volatile.Read(ref _transport);
        if (transport is null || !transport.IsRunning)
        {
            return;
        }

        var run = GetActiveRun();
        if (run == null)
        {
            return;
        }

        try
        {
            await transport.SendNotificationAsync(
                "session/cancel",
                new { sessionId = run.SessionId },
                ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "{event_type} {event_status} {provider} {provider_mode} {copilot_session_id}",
                "copilot.acp_server.interrupt",
                "failed",
                _options.Provider,
                _options.ProviderMode,
                run.SessionId);
        }
    }

    public async Task ShutdownAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        await _operationLock.WaitAsync(ct);
        try
        {
            await ShutdownInternalAsync(timeout ?? TimeSpan.FromSeconds(5), ct);
        }
        finally
        {
            _ = _operationLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await ShutdownAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        _operationLock.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task ShutdownInternalAsync(TimeSpan timeout, CancellationToken ct)
    {
        // Order matters for race avoidance:
        // 1. Mark shutdown first so a racing OnTransportClosed logs "completed" (not "failed").
        // 2. Unsubscribe before touching the transport so no further handler invocations observe torn state.
        // 3. Capture + null the field so concurrent notification handlers skip cleanly.
        _ = Interlocked.Exchange(ref _isShuttingDown, 1);

        var transport = Volatile.Read(ref _transport);
        if (transport == null)
        {
            return;
        }

        transport.Closed -= OnTransportClosed;
        Volatile.Write(ref _transport, null);

        try
        {
            var run = GetActiveRun();
            if (run != null)
            {
                try
                {
                    await transport.SendNotificationAsync(
                        "session/cancel",
                        new { sessionId = run.SessionId },
                        CancellationToken.None);
                }
                catch
                {
                    // best-effort cancellation during shutdown
                }
            }

            await transport.StopAsync(timeout, ct);
            await transport.DisposeAsync();
        }
        finally
        {
            var run = GetActiveRun();
            run?.TryFail(new InvalidOperationException("Copilot client shut down before run completion."));
            ClearActiveRun();

            _logger?.LogInformation(
                "{event_type} {event_status} {provider} {provider_mode} {copilot_session_id}",
                "copilot.acp_server.shutdown",
                "completed",
                _options.Provider,
                _options.ProviderMode,
                CurrentCopilotSessionId);

            _ = Interlocked.Exchange(ref _isShuttingDown, 0);
        }
    }

    private void OnTransportClosed(Exception? exception)
    {
        // Only this path may null out _transport when the shutdown is initiated by the
        // process exiting. When ShutdownInternalAsync is the initiator, it already
        // cleared _transport under _operationLock and we must not double-clear.
        var isShuttingDown = Interlocked.CompareExchange(ref _isShuttingDown, 0, 0) == 1;
        if (!isShuttingDown)
        {
            Volatile.Write(ref _transport, null);
        }

        if (exception != null)
        {
            if (isShuttingDown)
            {
                _logger?.LogInformation(
                    exception,
                    "{event_type} {event_status} {provider} {provider_mode} {error_code}",
                    "copilot.acp_server.exit",
                    "completed",
                    _options.Provider,
                    _options.ProviderMode,
                    "acp_server_stopped");
            }
            else
            {
                DependencyState = "failed";
                _logger?.LogError(
                    exception,
                    "{event_type} {event_status} {provider} {provider_mode} {error_code}",
                    "copilot.acp_server.exit",
                    "failed",
                    _options.Provider,
                    _options.ProviderMode,
                    "acp_server_exited");
            }
        }

        var run = GetActiveRun();
        if (run != null)
        {
            run.TryFail(exception ?? new InvalidOperationException("Copilot ACP process exited before run completion."));
            ClearActiveRun();
        }
    }

    private void HandleServerNotification(string method, JsonElement? parameters)
    {
        if (Volatile.Read(ref _disposed) != 0
            || Interlocked.CompareExchange(ref _isShuttingDown, 0, 0) == 1)
        {
            return;
        }

        var run = GetActiveRun();
        if (run == null)
        {
            return;
        }

        if (!string.Equals(method, "session/update", StringComparison.Ordinal))
        {
            // Non-update notifications are still forwarded as events for visibility.
            run.TryWriteEvent(new CopilotTurnEventEnvelope
            {
                Type = "event",
                Event = WrapEvent(method, parameters),
                RequestId = run.RequestId,
                SessionId = CopilotEventParser.ExtractSessionId(parameters) ?? run.SessionId,
            });
            return;
        }

        var sessionId = CopilotEventParser.ExtractSessionId(parameters) ?? run.SessionId;
        run.TryWriteEvent(new CopilotTurnEventEnvelope
        {
            Type = "event",
            Event = WrapEvent(method, parameters),
            RequestId = run.RequestId,
            SessionId = sessionId,
        });
    }

    private async Task<JsonElement> HandleServerRequestAsync(string method, JsonElement? parameters, CancellationToken ct)
    {
        return method switch
        {
            "session/request_permission" => SerializeToElement(BuildPermissionResponse(parameters)),
            "fs/read_text_file" => throw new InvalidOperationException("Copilot client does not expose local filesystem reads."),
            "fs/write_text_file" => throw new InvalidOperationException("Copilot client does not expose local filesystem writes."),
            _ => await HandleDynamicToolCallAsync(method, parameters, ct),
        };
    }

    private async Task<JsonElement> HandleDynamicToolCallAsync(string method, JsonElement? parameters, CancellationToken ct)
    {
        // Copilot ACP routes dynamic tools via the tool name as the RPC method, or a
        // wrapped tools/call request. Support both shapes by extracting the canonical
        // tool name and arguments.
        var (toolName, arguments) = ExtractDynamicToolInvocation(method, parameters);
        if (string.IsNullOrWhiteSpace(toolName))
        {
            throw new InvalidOperationException($"Unsupported Copilot ACP request method '{method}'.");
        }

        var request = new CopilotDynamicToolCallRequest
        {
            SessionId = CopilotEventParser.GetPropertyString(parameters, "sessionId") ?? CurrentCopilotSessionId,
            CallId = CopilotEventParser.GetPropertyString(parameters, "callId"),
            Tool = toolName,
            Arguments = arguments,
        };

        var stopwatch = Stopwatch.StartNew();
        CopilotDynamicToolCallResponse response;
        if (_dynamicToolExecutor == null)
        {
            response = BuildToolBridgeFailure("Dynamic tool bridge is not configured.");
        }
        else
        {
            try
            {
                response = await _dynamicToolExecutor(request, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(
                    ex,
                    "{event_type} {event_status} {provider} {provider_mode} {tool_name}",
                    "copilot.dynamic_tool.execution",
                    "failed",
                    _options.Provider,
                    _options.ProviderMode,
                    toolName);
                response = BuildToolBridgeFailure(ex.Message);
            }
        }

        _logger?.LogInformation(
            "{event_type} {event_status} {provider} {provider_mode} {tool_name} {tool_call_id} {copilot_session_id} {latency_ms}",
            response.Success ? "copilot.dynamic_tool.completed" : "copilot.dynamic_tool.denied",
            response.Success ? "completed" : "failed",
            _options.Provider,
            _options.ProviderMode,
            toolName,
            request.CallId,
            request.SessionId,
            stopwatch.ElapsedMilliseconds);

        return SerializeToElement(new
        {
            success = response.Success,
            content = NormalizeToolContentItems(response.ContentItems),
        });
    }

    private (string ToolName, JsonElement Arguments) ExtractDynamicToolInvocation(string method, JsonElement? parameters)
    {
        var directTool = CopilotEventParser.GetPropertyString(parameters, "tool")
            ?? CopilotEventParser.GetPropertyString(parameters, "name");
        var directArgs = CopilotEventParser.GetPropertyElement(parameters, "arguments")
            ?? CopilotEventParser.GetPropertyElement(parameters, "input")
            ?? CopilotEventParser.CreateEmptyObject();

        if (!string.IsNullOrWhiteSpace(directTool))
        {
            return (directTool!, directArgs);
        }

        // Fallback: RPC method carries the tool name directly (e.g., tools/call::<name>).
        return (method, directArgs);
    }

    private object BuildPermissionResponse(JsonElement? parameters)
    {
        var decision = string.IsNullOrWhiteSpace(_options.DefaultPermissionDecision)
            ? "allow"
            : _options.DefaultPermissionDecision;
        _logger?.LogInformation(
            "{event_type} {event_status} {provider} {provider_mode} {decision}",
            "copilot.permission_request.handled",
            "completed",
            _options.Provider,
            _options.ProviderMode,
            decision);
        _ = parameters; // decision is static; parameters reserved for future inspection
        return new { decision };
    }

    private void EnsureModelAllowed(JsonElement initializeResponse, string? requestedModel)
    {
        if (string.IsNullOrWhiteSpace(requestedModel))
        {
            return;
        }

        var models = ExtractModelList(initializeResponse);
        if (models.Count == 0)
        {
            // Server did not advertise a model list; nothing to assert against.
            return;
        }

        if (models.Any(m => string.Equals(m, requestedModel, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var joined = string.Join(",", models);
        throw new InvalidOperationException(
            $"Copilot CLI does not expose requested model '{requestedModel}'. Available models: {joined}.");
    }

    private static List<string> ExtractModelList(JsonElement response)
    {
        var result = new List<string>();
        if (response.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var propertyName in new[] { "models", "supportedModels", "availableModels" })
        {
            if (!response.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in property.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var value = item.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        result.Add(value!);
                    }
                }
                else if (item.ValueKind == JsonValueKind.Object
                         && item.TryGetProperty("id", out var idProp)
                         && idProp.ValueKind == JsonValueKind.String)
                {
                    var value = idProp.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        result.Add(value!);
                    }
                }
            }

            if (result.Count > 0)
            {
                return result;
            }
        }

        return result;
    }

    private CopilotBridgeInitOptions ResolveEffectiveOptions(CopilotBridgeInitOptions options)
    {
        return options with
        {
            Model = string.IsNullOrWhiteSpace(options.Model) ? _options.Model : options.Model,
            WorkingDirectory = string.IsNullOrWhiteSpace(options.WorkingDirectory) ? _options.WorkingDirectory : options.WorkingDirectory,
            BaseInstructions = string.IsNullOrWhiteSpace(options.BaseInstructions) ? _options.BaseInstructions : options.BaseInstructions,
            DeveloperInstructions = string.IsNullOrWhiteSpace(options.DeveloperInstructions) ? _options.DeveloperInstructions : options.DeveloperInstructions,
            ModelInstructionsFile = string.IsNullOrWhiteSpace(options.ModelInstructionsFile) ? _options.ModelInstructionsFile : options.ModelInstructionsFile,
            BaseUrl = string.IsNullOrWhiteSpace(options.BaseUrl) ? _options.BaseUrl : options.BaseUrl,
            ApiKey = string.IsNullOrWhiteSpace(options.ApiKey) ? _options.ApiKey : options.ApiKey,
            SessionId = string.IsNullOrWhiteSpace(options.SessionId) ? _options.CopilotSessionId : options.SessionId,
        };
    }

    private object BuildSessionNewParams(CopilotBridgeInitOptions options)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["model"] = options.Model,
            ["cwd"] = options.WorkingDirectory,
        };

        if (!string.IsNullOrWhiteSpace(options.SessionId))
        {
            parameters["sessionId"] = options.SessionId;
        }

        if (!string.IsNullOrWhiteSpace(options.BaseInstructions))
        {
            parameters["baseInstructions"] = options.BaseInstructions;
        }

        if (!string.IsNullOrWhiteSpace(options.DeveloperInstructions))
        {
            parameters["developerInstructions"] = options.DeveloperInstructions;
        }

        if (!string.IsNullOrWhiteSpace(options.ModelInstructionsFile))
        {
            parameters["modelInstructionsFile"] = options.ModelInstructionsFile;
        }

        if (options.Tools is { Count: > 0 })
        {
            parameters["tools"] = options.Tools.Select(tool =>
            {
                object inputSchema = tool.InputSchema.ValueKind == JsonValueKind.Undefined
                    ? new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = true,
                    }
                    : tool.InputSchema;
                return new Dictionary<string, object?>
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description ?? string.Empty,
                    ["inputSchema"] = inputSchema,
                };
            }).ToArray();
        }

        return parameters;
    }

    private static object BuildSessionPromptParams(string sessionId, string input)
    {
        return new
        {
            sessionId,
            prompt = new[]
            {
                new
                {
                    type = "text",
                    text = input,
                },
            },
        };
    }

    private static IReadOnlyList<object> NormalizeToolContentItems(IReadOnlyList<CopilotDynamicToolContentItem> items)
    {
        var normalized = new List<object>();
        foreach (var item in items)
        {
            if (string.Equals(item.Type, "image", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(item.ImageUrl))
            {
                normalized.Add(new
                {
                    type = "image",
                    imageUrl = item.ImageUrl,
                });
                continue;
            }

            normalized.Add(new
            {
                type = "text",
                text = item.Text ?? string.Empty,
            });
        }

        if (normalized.Count == 0)
        {
            normalized.Add(new
            {
                type = "text",
                text = string.Empty,
            });
        }

        return normalized;
    }

    private static CopilotDynamicToolCallResponse BuildToolBridgeFailure(string message)
    {
        return new CopilotDynamicToolCallResponse
        {
            Success = false,
            ContentItems =
            [
                new CopilotDynamicToolContentItem
                {
                    Type = "text",
                    Text = message,
                },
            ],
        };
    }

    private bool TryActivateRun(ActiveRunState run)
    {
        lock (_runStateLock)
        {
            if (_activeRun != null)
            {
                return false;
            }

            _activeRun = run;
            return true;
        }
    }

    private void DeactivateRun(ActiveRunState run)
    {
        lock (_runStateLock)
        {
            if (ReferenceEquals(_activeRun, run))
            {
                _activeRun = null;
            }
        }
    }

    private ActiveRunState? GetActiveRun()
    {
        lock (_runStateLock)
        {
            return _activeRun;
        }
    }

    private void ClearActiveRun()
    {
        lock (_runStateLock)
        {
            _activeRun = null;
        }
    }

    private JsonElement SerializeToElement<T>(T value)
    {
        return JsonSerializer.SerializeToElement(value, _json);
    }

    private static JsonElement WrapEvent(string method, JsonElement? parameters)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", method);
            if (parameters.HasValue)
            {
                if (parameters.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in parameters.Value.EnumerateObject())
                    {
                        property.WriteTo(writer);
                    }
                }
                else
                {
                    writer.WritePropertyName("params");
                    parameters.Value.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
            writer.Flush();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private sealed class ActiveRunState
    {
        private readonly object _stateLock = new();
        private readonly Channel<CopilotTurnEventEnvelope> _events = Channel.CreateUnbounded<CopilotTurnEventEnvelope>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });

        private bool _completed;

        public ActiveRunState(string requestId, string sessionId)
        {
            RequestId = requestId;
            SessionId = sessionId;
        }

        public string RequestId { get; }

        public string SessionId { get; }

        public void TryWriteEvent(CopilotTurnEventEnvelope envelope)
        {
            _ = _events.Writer.TryWrite(envelope);
        }

        public IAsyncEnumerable<CopilotTurnEventEnvelope> ReadAllEventsAsync(CancellationToken ct)
        {
            return _events.Reader.ReadAllAsync(ct);
        }

        public void TryComplete()
        {
            lock (_stateLock)
            {
                if (_completed)
                {
                    return;
                }

                _completed = true;
            }

            _ = _events.Writer.TryComplete();
        }

        public void TryFail(Exception ex)
        {
            lock (_stateLock)
            {
                if (_completed)
                {
                    return;
                }

                _completed = true;
            }

            _ = _events.Writer.TryComplete(ex);
        }
    }
}
