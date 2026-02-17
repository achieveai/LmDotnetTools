using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Models;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Transport;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.CodexSdkProvider.Agents;

public sealed class CodexSdkClient : ICodexSdkClient
{
    private readonly CodexSdkOptions _options;
    private readonly ILogger<CodexSdkClient>? _logger;
    private readonly JsonSerializerOptions _json;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly object _runStateLock = new();
    private readonly Dictionary<string, InternalToolSpan> _internalToolSpans = new(StringComparer.Ordinal);

    private CodexAppServerTransport? _transport;
    private ActiveRunState? _activeRun;
    private CodexBridgeInitOptions? _startupOptions;
    private Func<CodexDynamicToolCallRequest, CancellationToken, Task<CodexDynamicToolCallResponse>>? _dynamicToolExecutor;
    private int _isShuttingDown;
    private bool _disposed;

    public bool IsRunning => _transport is { IsRunning: true };

    public string? CurrentCodexThreadId { get; private set; }

    public string? CurrentTurnId { get; private set; }

    public string DependencyState { get; private set; } = "unknown";

    public CodexSdkClient(
        CodexSdkOptions options,
        ILogger<CodexSdkClient>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        _json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
    }

    public void ConfigureDynamicToolExecutor(
        Func<CodexDynamicToolCallRequest, CancellationToken, Task<CodexDynamicToolCallResponse>>? executor)
    {
        _dynamicToolExecutor = executor;
    }

    public Task EnsureStartedAsync(CodexBridgeInitOptions options, CancellationToken ct = default)
    {
        return StartOrResumeThreadAsync(options, ct);
    }

    public async Task StartOrResumeThreadAsync(CodexBridgeInitOptions options, CancellationToken ct = default)
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

            _ = Interlocked.Exchange(ref _isShuttingDown, 0);

            var effectiveOptions = ResolveEffectiveOptions(options);
            var timeout = TimeSpan.FromMilliseconds(Math.Max(_options.AppServerStartupTimeoutMs, 5_000));
            var codexCliVersion = await CodexVersionChecker.EnsureCodexCliVersionAsync(
                _options.CodexCliPath,
                _options.CodexCliMinVersion,
                timeout,
                ct);

            var workingDirectory = effectiveOptions.WorkingDirectory
                ?? _options.WorkingDirectory
                ?? Directory.GetCurrentDirectory();
            var transport = new CodexAppServerTransport(_options, _logger);
            transport.Closed += OnTransportClosed;

            await transport.StartAsync(
                workingDirectory,
                effectiveOptions.ApiKey,
                effectiveOptions.BaseUrl,
                HandleServerRequestAsync,
                HandleServerNotification,
                ct);

            _transport = transport;
            _startupOptions = effectiveOptions;

            var startupTimeout = TimeSpan.FromMilliseconds(_options.AppServerStartupTimeoutMs);
            await transport.SendRequestAsync(
                "initialize",
                new
                {
                    clientInfo = new
                    {
                        name = "lm-dotnet-tools-codex-client",
                        version = "0.3.0",
                    },
                    capabilities = new
                    {
                        experimentalApi = true,
                    },
                },
                ct,
                startupTimeout);

            await transport.SendNotificationAsync("initialized", null, ct);

            JsonElement threadResponse;
            if (!string.IsNullOrWhiteSpace(effectiveOptions.ThreadId))
            {
                threadResponse = await transport.SendRequestAsync(
                    "thread/resume",
                    BuildThreadResumeParams(effectiveOptions),
                    ct);
            }
            else
            {
                threadResponse = await transport.SendRequestAsync(
                    "thread/start",
                    BuildThreadStartParams(effectiveOptions),
                    ct);
            }

            CurrentCodexThreadId = CodexEventParser.ExtractThreadId(threadResponse) ?? effectiveOptions.ThreadId;
            CurrentTurnId = null;
            DependencyState = "ready";

            _logger?.LogInformation(
                "{event_type} {event_status} {provider} {provider_mode} {codex_cli_path} {codex_cli_version} {codex_thread_id}",
                "codex.app_server.ready",
                "completed",
                _options.Provider,
                _options.ProviderMode,
                _options.CodexCliPath,
                codexCliVersion,
                CurrentCodexThreadId);
        }
        catch
        {
            DependencyState = "failed";
            await ShutdownInternalAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
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

        var transport = _transport;
        if (transport is null || !transport.IsRunning)
        {
            throw new InvalidOperationException("Codex app-server is not running. Call StartOrResumeThreadAsync first.");
        }

        if (string.IsNullOrWhiteSpace(CurrentCodexThreadId))
        {
            throw new InvalidOperationException("Codex thread is not initialized.");
        }

        var requestId = Guid.NewGuid().ToString("N");
        var runState = new ActiveRunState(requestId);
        if (!TryActivateRun(runState))
        {
            throw new InvalidOperationException("A Codex run is already in progress.");
        }

        var start = Stopwatch.StartNew();

        _logger?.LogInformation(
            "{event_type} {event_status} {provider} {provider_mode} {bridge_request_id} {codex_thread_id}",
            "codex.bridge.run.started",
            "started",
            _options.Provider,
            _options.ProviderMode,
            requestId,
            CurrentCodexThreadId);

        var runTask = ExecuteTurnAsync(transport, input, runState, requestId, start, ct);
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
        CodexAppServerTransport transport,
        string input,
        ActiveRunState runState,
        string requestId,
        Stopwatch start,
        CancellationToken ct)
    {
        try
        {
            var turnStart = await transport.SendRequestAsync(
                "turn/start",
                BuildTurnStartParams(CurrentCodexThreadId!, input),
                ct);

            var turnId = CodexEventParser.ExtractTurnId(turnStart);
            if (!string.IsNullOrWhiteSpace(turnId))
            {
                runState.SetTurnId(turnId);
                CurrentTurnId = turnId;
            }

            if (runState.TryConsumePendingInterrupt() && !string.IsNullOrWhiteSpace(turnId))
            {
                await TryInterruptTurnInternalAsync(transport, turnId, CancellationToken.None);
            }

            var immediateStatus = CodexEventParser.ExtractTurnStatus(turnStart);
            if (!string.IsNullOrWhiteSpace(immediateStatus) && CodexEventParser.IsTerminalTurnStatus(immediateStatus))
            {
                if (CodexEventParser.IsTurnFailureStatus(immediateStatus))
                {
                    throw new InvalidOperationException(CodexEventParser.ExtractTurnErrorMessage(turnStart) ?? "Codex turn failed.");
                }

                runState.TryComplete(turnId);
            }

            var completedTurnId = await WaitForTurnCompletionAsync(transport, runState, turnId, requestId, ct);
            if (!string.IsNullOrWhiteSpace(completedTurnId))
            {
                CurrentTurnId = completedTurnId;
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
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await InterruptTurnAsync(CancellationToken.None);
            runState.TryFail(new OperationCanceledException("Codex turn was cancelled."));
            throw;
        }
        catch (Exception ex)
        {
            runState.TryFail(ex);
            _logger?.LogError(
                ex,
                "{event_type} {event_status} {provider} {provider_mode} {bridge_request_id} {codex_thread_id} {error_code} {latency_ms}",
                "codex.bridge.run.failed",
                "failed",
                _options.Provider,
                _options.ProviderMode,
                requestId,
                CurrentCodexThreadId,
                "run_failed",
                start.ElapsedMilliseconds);
            throw;
        }
    }

    public async Task InterruptTurnAsync(CancellationToken ct = default)
    {
        var transport = _transport;
        if (transport is null || !transport.IsRunning)
        {
            return;
        }

        var run = GetActiveRun();
        if (run == null)
        {
            return;
        }

        var turnId = run.TurnId;
        if (!string.IsNullOrWhiteSpace(turnId))
        {
            await TryInterruptTurnInternalAsync(transport, turnId, ct);
            return;
        }

        run.MarkPendingInterrupt();
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

    private async Task ShutdownInternalAsync(TimeSpan timeout, CancellationToken ct)
    {
        var transport = _transport;
        if (transport == null)
        {
            return;
        }

        _ = Interlocked.Exchange(ref _isShuttingDown, 1);
        _transport = null;
        transport.Closed -= OnTransportClosed;

        try
        {
            var run = GetActiveRun();
            if (!string.IsNullOrWhiteSpace(run?.TurnId))
            {
                await TryInterruptTurnInternalAsync(transport, run.TurnId!, CancellationToken.None);
            }

            await transport.StopAsync(timeout, ct);
            await transport.DisposeAsync();
        }
        finally
        {
            var run = GetActiveRun();
            run?.TryFail(new InvalidOperationException("Codex client shut down before run completion."));
            ClearActiveRun();

            _logger?.LogInformation(
                "{event_type} {event_status} {provider} {provider_mode} {codex_thread_id} {turn_id}",
                "codex.app_server.shutdown",
                "completed",
                _options.Provider,
                _options.ProviderMode,
                CurrentCodexThreadId,
                CurrentTurnId);

            _ = Interlocked.Exchange(ref _isShuttingDown, 0);
        }
    }

    private void OnTransportClosed(Exception? exception)
    {
        _transport = null;

        if (exception != null)
        {
            if (Interlocked.CompareExchange(ref _isShuttingDown, 0, 0) == 1)
            {
                _logger?.LogInformation(
                    exception,
                    "{event_type} {event_status} {provider} {provider_mode} {error_code}",
                    "codex.app_server.exit",
                    "completed",
                    _options.Provider,
                    _options.ProviderMode,
                    "app_server_stopped");
            }
            else
            {
                DependencyState = "failed";
                _logger?.LogError(
                    exception,
                    "{event_type} {event_status} {provider} {provider_mode} {error_code}",
                    "codex.app_server.exit",
                    "failed",
                    _options.Provider,
                    _options.ProviderMode,
                    "app_server_exited");
            }
        }

        var run = GetActiveRun();
        if (run != null)
        {
            run.TryFail(exception ?? new InvalidOperationException("Codex app-server exited before run completion."));
            ClearActiveRun();
        }
    }

    private void HandleServerNotification(string method, JsonElement? parameters)
    {
        if (string.Equals(method, "thread/started", StringComparison.Ordinal)
            || string.Equals(method, "thread.started", StringComparison.Ordinal))
        {
            var threadId = CodexEventParser.ExtractThreadId(parameters);
            if (!string.IsNullOrWhiteSpace(threadId))
            {
                CurrentCodexThreadId = threadId;
            }
        }

        var run = GetActiveRun();
        if (run == null)
        {
            return;
        }

        if (string.Equals(method, "turn/started", StringComparison.Ordinal)
            || string.Equals(method, "turn.started", StringComparison.Ordinal))
        {
            var turnId = CodexEventParser.ExtractTurnId(parameters);
            if (!string.IsNullOrWhiteSpace(turnId))
            {
                run.SetTurnId(turnId);
                CurrentTurnId = turnId;

                if (run.TryConsumePendingInterrupt())
                {
                    var transport = _transport;
                    if (transport != null)
                    {
                        _ = Task.Run(() => TryInterruptTurnInternalAsync(transport, turnId, CancellationToken.None));
                    }
                }
            }
        }

        var suppressOriginalNotification = TryHandleInternalToolNotification(method, parameters, run);
        if (suppressOriginalNotification)
        {
            return;
        }

        if (!ShouldForwardToActiveRun(method, parameters, run))
        {
            return;
        }

        var eventTurnId = CodexEventParser.ExtractTurnId(parameters) ?? run.TurnId;
        if (!string.IsNullOrWhiteSpace(eventTurnId))
        {
            CurrentTurnId = eventTurnId;
        }

        run.TryWriteEvent(new CodexTurnEventEnvelope
        {
            Type = "event",
            Event = CreateRunEvent(method, parameters),
            RequestId = run.RequestId,
            ThreadId = CurrentCodexThreadId,
            TurnId = eventTurnId,
        });

        if (string.Equals(method, "turn/completed", StringComparison.Ordinal)
            || string.Equals(method, "turn.completed", StringComparison.Ordinal))
        {
            var status = CodexEventParser.ExtractTurnStatus(parameters);
            if (CodexEventParser.IsTurnFailureStatus(status))
            {
                run.TryFail(new InvalidOperationException(CodexEventParser.ExtractTurnErrorMessage(parameters) ?? "Codex turn failed."));
            }
            else
            {
                run.TryComplete(eventTurnId);
            }
        }
        else if (CodexEventParser.IsTurnFailureNotification(method))
        {
            run.TryFail(new InvalidOperationException(CodexEventParser.ExtractTurnErrorMessage(parameters) ?? "Codex turn failed."));
        }
        else if (string.Equals(method, "turn/updated", StringComparison.Ordinal)
                 || string.Equals(method, "turn.updated", StringComparison.Ordinal))
        {
            var status = CodexEventParser.ExtractTurnStatus(parameters);
            if (!string.IsNullOrWhiteSpace(status) && CodexEventParser.IsTerminalTurnStatus(status))
            {
                if (CodexEventParser.IsTurnFailureStatus(status))
                {
                    run.TryFail(new InvalidOperationException(CodexEventParser.ExtractTurnErrorMessage(parameters) ?? $"Codex turn {status}."));
                }
                else
                {
                    run.TryComplete(eventTurnId);
                }
            }
        }
    }

    private async Task<JsonElement> HandleServerRequestAsync(string method, JsonElement? parameters, CancellationToken ct)
    {
        return method switch
        {
            "item/commandExecution/requestApproval" => SerializeToElement(new { decision = BuildDefaultCommandApprovalDecision() }),
            "item/fileChange/requestApproval" => SerializeToElement(new { decision = BuildDefaultFileApprovalDecision() }),
            "item/tool/requestUserInput" => SerializeToElement(new { answers = new Dictionary<string, string>() }),
            "item/tool/call" => await HandleDynamicToolCallAsync(parameters, ct),
            "account/chatgptAuthTokens/refresh" => CodexEventParser.CreateEmptyObject(),
            _ => throw new InvalidOperationException($"Unsupported App Server request method '{method}'."),
        };
    }

    private async Task<JsonElement> HandleDynamicToolCallAsync(JsonElement? parameters, CancellationToken ct)
    {
        var toolName = CodexEventParser.GetPropertyString(parameters, "tool") ?? string.Empty;
        var stopwatch = Stopwatch.StartNew();
        var request = new CodexDynamicToolCallRequest
        {
            ThreadId = CodexEventParser.GetPropertyString(parameters, "threadId"),
            TurnId = CodexEventParser.GetPropertyString(parameters, "turnId"),
            CallId = CodexEventParser.GetPropertyString(parameters, "callId"),
            Tool = toolName,
            Arguments = CodexEventParser.GetPropertyElement(parameters, "arguments") ?? CodexEventParser.CreateEmptyObject(),
        };
        EmitDynamicToolLifecycleEvent("item/started", request, null, null);

        _logger?.LogInformation(
            "{event_type} {event_status} {provider} {provider_mode} {tool_name} {tool_call_id} {thread_id} {turn_id}",
            "codex.dynamic_tool.requested",
            "started",
            _options.Provider,
            _options.ProviderMode,
            toolName,
            request.CallId,
            request.ThreadId,
            request.TurnId);

        CodexDynamicToolCallResponse response;
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
            catch (Exception ex)
            {
                _logger?.LogWarning(
                    ex,
                    "{event_type} {event_status} {provider} {provider_mode} {tool_name}",
                    "codex.dynamic_tool.execution",
                    "failed",
                    _options.Provider,
                    _options.ProviderMode,
                    toolName);

                response = BuildToolBridgeFailure(ex.Message);
            }
        }

        _logger?.LogInformation(
            "{event_type} {event_status} {provider} {provider_mode} {tool_name} {tool_call_id} {thread_id} {turn_id} {decision} {latency_ms}",
            response.Success ? "codex.dynamic_tool.completed" : "codex.dynamic_tool.denied",
            response.Success ? "completed" : "failed",
            _options.Provider,
            _options.ProviderMode,
            toolName,
            request.CallId,
            request.ThreadId,
            request.TurnId,
            response.Success ? "allow" : "deny",
            stopwatch.ElapsedMilliseconds);
        EmitDynamicToolLifecycleEvent("item/completed", request, response, stopwatch.ElapsedMilliseconds);

        return SerializeToElement(new
        {
            success = response.Success,
            contentItems = NormalizeToolResponseItems(response.ContentItems),
        });
    }

    private async Task TryInterruptTurnInternalAsync(CodexAppServerTransport transport, string turnId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(CurrentCodexThreadId) || string.IsNullOrWhiteSpace(turnId))
        {
            return;
        }

        try
        {
            await transport.SendRequestAsync(
                "turn/interrupt",
                new
                {
                    threadId = CurrentCodexThreadId,
                    turnId,
                },
                ct,
                TimeSpan.FromSeconds(30));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "{event_type} {event_status} {provider} {provider_mode} {turn_id}",
                "codex.app_server.interrupt",
                "failed",
                _options.Provider,
                _options.ProviderMode,
                turnId);
        }
    }

    private CodexBridgeInitOptions ResolveEffectiveOptions(CodexBridgeInitOptions options)
    {
        return options with
        {
            Model = string.IsNullOrWhiteSpace(options.Model) ? _options.Model : options.Model,
            ApprovalPolicy = string.IsNullOrWhiteSpace(options.ApprovalPolicy) ? _options.ApprovalPolicy : options.ApprovalPolicy,
            SandboxMode = string.IsNullOrWhiteSpace(options.SandboxMode) ? _options.SandboxMode : options.SandboxMode,
            WebSearchMode = string.IsNullOrWhiteSpace(options.WebSearchMode) ? _options.WebSearchMode : options.WebSearchMode,
            WorkingDirectory = string.IsNullOrWhiteSpace(options.WorkingDirectory) ? _options.WorkingDirectory : options.WorkingDirectory,
            BaseInstructions = string.IsNullOrWhiteSpace(options.BaseInstructions) ? null : options.BaseInstructions,
            DeveloperInstructions = string.IsNullOrWhiteSpace(options.DeveloperInstructions) ? null : options.DeveloperInstructions,
            ModelInstructionsFile = string.IsNullOrWhiteSpace(options.ModelInstructionsFile) ? null : options.ModelInstructionsFile,
            BaseUrl = string.IsNullOrWhiteSpace(options.BaseUrl) ? _options.BaseUrl : options.BaseUrl,
            ApiKey = string.IsNullOrWhiteSpace(options.ApiKey) ? _options.ApiKey : options.ApiKey,
        };
    }

    private object BuildThreadStartParams(CodexBridgeInitOptions options)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["model"] = options.Model,
            ["approvalPolicy"] = options.ApprovalPolicy,
            ["sandbox"] = options.SandboxMode,
            ["cwd"] = options.WorkingDirectory,
            ["config"] = BuildConfig(options),
            ["baseInstructions"] = options.BaseInstructions,
            ["developerInstructions"] = options.DeveloperInstructions,
            ["experimentalRawEvents"] = false,
        };

        if (options.DynamicTools is { Count: > 0 })
        {
            parameters["dynamicTools"] = options.DynamicTools.Select(tool =>
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

    private object BuildThreadResumeParams(CodexBridgeInitOptions options)
    {
        return new Dictionary<string, object?>
        {
            ["threadId"] = options.ThreadId,
            ["model"] = options.Model,
            ["approvalPolicy"] = options.ApprovalPolicy,
            ["sandbox"] = options.SandboxMode,
            ["cwd"] = options.WorkingDirectory,
            ["config"] = BuildConfig(options),
            ["baseInstructions"] = options.BaseInstructions,
            ["developerInstructions"] = options.DeveloperInstructions,
        };
    }

    private static object BuildTurnStartParams(string threadId, string input)
    {
        return new
        {
            threadId,
            input = new[]
            {
                new
                {
                    type = "text",
                    text = input,
                    text_elements = Array.Empty<object>(),
                },
            },
        };
    }

    private static Dictionary<string, object?> BuildConfig(CodexBridgeInitOptions options)
    {
        var config = new Dictionary<string, object?>
        {
            ["sandbox_workspace_write"] = new
            {
                network_access = options.NetworkAccessEnabled,
            },
        };

        if (!string.IsNullOrWhiteSpace(options.WebSearchMode))
        {
            config["web_search"] = options.WebSearchMode;
            config["tools"] = new
            {
                web_search = !string.Equals(options.WebSearchMode, "disabled", StringComparison.OrdinalIgnoreCase),
            };
        }

        if (options.McpServers is { Count: > 0 })
        {
            config["mcp_servers"] = options.McpServers;
        }

        if (!string.IsNullOrWhiteSpace(options.ModelInstructionsFile))
        {
            config["model_instructions_file"] = options.ModelInstructionsFile;
        }

        return config;
    }

    private string BuildDefaultCommandApprovalDecision()
    {
        var policy = _startupOptions?.ApprovalPolicy ?? _options.ApprovalPolicy;
        return string.Equals(policy, "never", StringComparison.OrdinalIgnoreCase)
            ? "decline"
            : "acceptForSession";
    }

    private string BuildDefaultFileApprovalDecision()
    {
        var policy = _startupOptions?.ApprovalPolicy ?? _options.ApprovalPolicy;
        return string.Equals(policy, "never", StringComparison.OrdinalIgnoreCase)
            ? "decline"
            : "acceptForSession";
    }

    private bool TryHandleInternalToolNotification(string method, JsonElement? parameters, ActiveRunState run)
    {
        if (!_options.ExposeCodexInternalToolsAsToolMessages)
        {
            return false;
        }

        if (CodexEventParser.IsItemStartedMethod(method) || CodexEventParser.IsItemCompletedMethod(method))
        {
            if (!CodexEventParser.TryParseInternalToolItem(parameters, out var item, out var toolName, out var toolCallId))
            {
                return false;
            }

            var eventThreadId = CodexEventParser.ExtractThreadId(parameters) ?? CurrentCodexThreadId;
            var eventTurnId = CodexEventParser.ExtractTurnId(parameters) ?? run.TurnId;
            var sourceMethod = CodexEventParser.IsItemStartedMethod(method) ? "item/started" : "item/completed";

            if (CodexEventParser.IsItemStartedMethod(method))
            {
                var arguments = BuildInternalToolArguments(toolName, item, sourceMethod);
                EmitInternalToolCall(run, toolCallId, toolName, arguments, sourceMethod, eventThreadId, eventTurnId);
            }
            else
            {
                var arguments = BuildInternalToolArguments(toolName, item, sourceMethod);
                var completion = BuildInternalToolCompletion(toolName, item, sourceMethod);
                EmitInternalToolResult(
                    run,
                    toolCallId,
                    toolName,
                    arguments,
                    completion.Status,
                    completion.Result,
                    completion.Error,
                    sourceMethod,
                    eventThreadId,
                    eventTurnId);
            }

            return true;
        }

        if (CodexEventParser.IsWebSearchBeginMethod(method) || CodexEventParser.IsWebSearchEndMethod(method))
        {
            var eventPayload = CodexEventParser.GetPropertyElement(parameters, "msg")
                               ?? CodexEventParser.GetPropertyElement(parameters, "event")
                               ?? parameters?.Clone()
                               ?? CodexEventParser.CreateEmptyObject();
            var toolCallId = CodexEventParser.GetPropertyString(eventPayload, "call_id")
                             ?? CodexEventParser.GetPropertyString(eventPayload, "callId")
                             ?? CodexEventParser.GetPropertyString(eventPayload, "id");
            if (string.IsNullOrWhiteSpace(toolCallId))
            {
                _logger?.LogWarning(
                    "{event_type} {event_status} {provider} {provider_mode} {method} {reason}",
                    "codex.internal_tool.mapping_failed",
                    "failed",
                    _options.Provider,
                    _options.ProviderMode,
                    method,
                    "missing_call_id");
                return false;
            }

            const string toolName = "web_search";
            var eventThreadId = CodexEventParser.ExtractThreadId(parameters) ?? CurrentCodexThreadId;
            var eventTurnId = CodexEventParser.ExtractTurnId(parameters) ?? run.TurnId;

            if (CodexEventParser.IsWebSearchBeginMethod(method))
            {
                var arguments = BuildInternalToolArguments(toolName, eventPayload, "codex/event/web_search_begin");
                EmitInternalToolCall(
                    run,
                    toolCallId,
                    toolName,
                    arguments,
                    "codex/event/web_search_begin",
                    eventThreadId,
                    eventTurnId);
            }
            else
            {
                var arguments = BuildInternalToolArguments(toolName, eventPayload, "codex/event/web_search_end");
                var completion = BuildInternalToolCompletion(toolName, eventPayload, "codex/event/web_search_end");
                EmitInternalToolResult(
                    run,
                    toolCallId,
                    toolName,
                    arguments,
                    completion.Status,
                    completion.Result,
                    completion.Error,
                    "codex/event/web_search_end",
                    eventThreadId,
                    eventTurnId);
            }
        }

        return false;
    }

    private void EmitInternalToolCall(
        ActiveRunState run,
        string toolCallId,
        string toolName,
        JsonElement arguments,
        string sourceMethod,
        string? threadId,
        string? turnId)
    {
        InternalToolSpan span;
        var now = DateTimeOffset.UtcNow;
        lock (_runStateLock)
        {
            if (!_internalToolSpans.TryGetValue(toolCallId, out span!))
            {
                span = new InternalToolSpan
                {
                    ToolCallId = toolCallId,
                    ToolName = toolName,
                };
                _internalToolSpans[toolCallId] = span;
            }

            span.ToolName = toolName;
            span.ArgumentsJson = arguments;
            span.HasArguments = true;

            if (span.CallEmitted)
            {
                _logger?.LogDebug(
                    "{event_type} {event_status} {provider} {provider_mode} {tool_name} {tool_call_id} {source_event}",
                    "codex.internal_tool.duplicate_ignored",
                    "ignored",
                    _options.Provider,
                    _options.ProviderMode,
                    toolName,
                    toolCallId,
                    sourceMethod);
                return;
            }

            span.CallEmitted = true;
            span.StartedAt = now;
            span.StartSource = sourceMethod;
        }

        _logger?.LogInformation(
            "{event_type} {event_status} {provider} {provider_mode} {tool_name} {tool_call_id} {thread_id} {turn_id} {source_event}",
            "codex.internal_tool.call.emitted",
            "started",
            _options.Provider,
            _options.ProviderMode,
            toolName,
            toolCallId,
            threadId,
            turnId,
            sourceMethod);

        EmitInternalToolLifecycleEvent(
            run,
            "item/started",
            toolCallId,
            toolName,
            arguments,
            result: null,
            error: null,
            threadId,
            turnId);
    }

    private void EmitInternalToolResult(
        ActiveRunState run,
        string toolCallId,
        string toolName,
        JsonElement arguments,
        string status,
        JsonElement result,
        JsonElement? error,
        string sourceMethod,
        string? threadId,
        string? turnId)
    {
        InternalToolSpan span;
        var now = DateTimeOffset.UtcNow;
        var emitSynthesizedStart = false;
        lock (_runStateLock)
        {
            if (!_internalToolSpans.TryGetValue(toolCallId, out span!))
            {
                span = new InternalToolSpan
                {
                    ToolCallId = toolCallId,
                    ToolName = toolName,
                };
                _internalToolSpans[toolCallId] = span;
            }

            span.ToolName = toolName;
            if (!span.HasArguments)
            {
                span.ArgumentsJson = arguments;
                span.HasArguments = true;
            }

            if (!span.CallEmitted)
            {
                span.CallEmitted = true;
                span.StartedAt = now;
                span.StartSource = $"{sourceMethod}:synthetic";
                emitSynthesizedStart = true;
            }

            if (span.ResultEmitted)
            {
                _logger?.LogDebug(
                    "{event_type} {event_status} {provider} {provider_mode} {tool_name} {tool_call_id} {source_event}",
                    "codex.internal_tool.duplicate_ignored",
                    "ignored",
                    _options.Provider,
                    _options.ProviderMode,
                    toolName,
                    toolCallId,
                    sourceMethod);
                return;
            }

            span.ResultEmitted = true;
            span.CompletedAt = now;
            span.EndSource = sourceMethod;
            span.Status = status;
            span.ResultJson = result;
            span.HasResult = true;
        }

        if (emitSynthesizedStart)
        {
            _logger?.LogInformation(
                "{event_type} {event_status} {provider} {provider_mode} {tool_name} {tool_call_id} {thread_id} {turn_id} {source_event}",
                "codex.internal_tool.synthesized_start",
                "started",
                _options.Provider,
                _options.ProviderMode,
                toolName,
                toolCallId,
                threadId,
                turnId,
                sourceMethod);

            EmitInternalToolLifecycleEvent(
                run,
                "item/started",
                toolCallId,
                toolName,
                span.ArgumentsJson,
                result: null,
                error: null,
                threadId,
                turnId);
        }

        var normalizedStatus = string.Equals(status, "success", StringComparison.OrdinalIgnoreCase)
            ? "completed"
            : "failed";
        var elapsedMs = span.StartedAt == default
            ? 0L
            : Math.Max(0L, (long)(span.CompletedAt - span.StartedAt).TotalMilliseconds);

        _logger?.LogInformation(
            "{event_type} {event_status} {provider} {provider_mode} {tool_name} {tool_call_id} {thread_id} {turn_id} {source_event} {status} {latency_ms}",
            "codex.internal_tool.result.emitted",
            string.Equals(status, "success", StringComparison.OrdinalIgnoreCase) ? "completed" : "failed",
            _options.Provider,
            _options.ProviderMode,
            toolName,
            toolCallId,
            threadId,
            turnId,
            sourceMethod,
            status,
            elapsedMs);

        EmitInternalToolLifecycleEvent(
            run,
            "item/completed",
            toolCallId,
            toolName,
            span.ArgumentsJson,
            result,
            error,
            threadId,
            turnId,
            normalizedStatus);
    }

    private void EmitInternalToolLifecycleEvent(
        ActiveRunState run,
        string eventType,
        string toolCallId,
        string toolName,
        JsonElement arguments,
        JsonElement? result,
        JsonElement? error,
        string? threadId,
        string? turnId,
        string status = "inProgress")
    {
        var eventThreadId = threadId ?? CurrentCodexThreadId;
        var eventTurnId = turnId ?? run.TurnId;
        object? resultValue = result.HasValue ? result.Value : null;
        object? errorValue = error.HasValue ? error.Value : null;

        var eventParams = SerializeToElement(new
        {
            threadId = eventThreadId,
            turnId = eventTurnId,
            item = new
            {
                type = "toolCall",
                id = toolCallId,
                tool = toolName,
                server = "codex_internal",
                status,
                arguments,
                result = resultValue,
                error = errorValue,
            },
        });

        if (!ShouldForwardToActiveRun(eventType, eventParams, run))
        {
            return;
        }

        run.TryWriteEvent(new CodexTurnEventEnvelope
        {
            Type = "event",
            Event = CreateRunEvent(eventType, eventParams),
            RequestId = run.RequestId,
            ThreadId = eventThreadId,
            TurnId = eventTurnId,
        });
    }

    private InternalToolCompletion BuildInternalToolCompletion(string toolName, JsonElement payload, string sourceMethod)
    {
        var hasError = CodexEventParser.TryGetProperty(payload, "error", out var errorElement)
                       && errorElement.ValueKind != JsonValueKind.Null
                       && errorElement.ValueKind != JsonValueKind.Undefined;
        var status = CodexEventParser.NormalizeInternalToolStatus(CodexEventParser.GetPropertyString(payload, "status"), hasError);

        var result = new Dictionary<string, object?>
        {
            ["status"] = status,
            ["source"] = sourceMethod,
        };

        CodexEventParser.AddToolSpecificFields(result, toolName, payload, isResultPayload: true);
        result["raw"] = payload;

        JsonElement? error = null;
        if (!string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
        {
            var message = CodexEventParser.ExtractErrorMessage(payload)
                          ?? $"Codex internal tool '{toolName}' completed with status '{status}'.";
            var errorObject = new Dictionary<string, object?>
            {
                ["message"] = message,
                ["status"] = status,
            };
            if (hasError)
            {
                errorObject["raw"] = errorElement;
            }

            error = SerializeToElement(errorObject);
        }

        return new InternalToolCompletion
        {
            Status = status,
            Result = SerializeToElement(result),
            Error = error,
        };
    }

    private JsonElement BuildInternalToolArguments(string toolName, JsonElement payload, string sourceMethod)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["source"] = sourceMethod,
        };

        CodexEventParser.AddToolSpecificFields(arguments, toolName, payload, isResultPayload: false);
        arguments["raw"] = payload;

        return SerializeToElement(arguments);
    }

    private bool ShouldForwardToActiveRun(string method, JsonElement? parameters, ActiveRunState run)
    {
        if (string.Equals(method, "thread/started", StringComparison.Ordinal)
            || string.Equals(method, "thread.started", StringComparison.Ordinal)
            || string.Equals(method, "error", StringComparison.Ordinal))
        {
            return true;
        }

        var eventTurnId = CodexEventParser.ExtractTurnId(parameters);
        if (string.IsNullOrWhiteSpace(eventTurnId))
        {
            return true;
        }

        var activeTurnId = run.TurnId;
        var shouldForward = string.IsNullOrWhiteSpace(activeTurnId)
                            || string.Equals(activeTurnId, eventTurnId, StringComparison.Ordinal);
        if (!shouldForward)
        {
            _logger?.LogWarning(
                "{event_type} {event_status} {provider} {provider_mode} {method} {reason} {active_turn_id} {event_turn_id}",
                "codex.app_server.notification",
                "dropped",
                _options.Provider,
                _options.ProviderMode,
                method,
                "turn_mismatch",
                activeTurnId,
                eventTurnId);
        }

        return shouldForward;
    }

    private async Task<string?> WaitForTurnCompletionAsync(
        CodexAppServerTransport transport,
        ActiveRunState runState,
        string? turnId,
        string requestId,
        CancellationToken ct)
    {
        var timeout = TimeSpan.FromMilliseconds(Math.Max(_options.TurnCompletionTimeoutMs, 1_000));
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            return await runState.Completion.Task.WaitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            var activeTurnId = runState.TurnId ?? turnId;
            _logger?.LogWarning(
                "{event_type} {event_status} {provider} {provider_mode} {bridge_request_id} {turn_id} {timeout_ms}",
                "codex.turn.timeout",
                "timed_out",
                _options.Provider,
                _options.ProviderMode,
                requestId,
                activeTurnId,
                timeout.TotalMilliseconds);

            if (!string.IsNullOrWhiteSpace(activeTurnId))
            {
                await TryInterruptTurnInternalAsync(transport, activeTurnId, CancellationToken.None);
            }

            var gracePeriod = TimeSpan.FromMilliseconds(Math.Max(_options.TurnInterruptGracePeriodMs, 250));
            using var graceCts = new CancellationTokenSource(gracePeriod);
            using var graceLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, graceCts.Token);
            try
            {
                return await runState.Completion.Task.WaitAsync(graceLinkedCts.Token);
            }
            catch (OperationCanceledException) when (graceCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Codex turn '{activeTurnId ?? "unknown"}' did not complete after timeout {timeout.TotalMilliseconds}ms " +
                    $"and interrupt grace {gracePeriod.TotalMilliseconds}ms.");
            }
        }
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
            _internalToolSpans.Clear();
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
                _internalToolSpans.Clear();
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
            _internalToolSpans.Clear();
        }
    }

    private JsonElement SerializeToElement<T>(T value)
    {
        return JsonSerializer.SerializeToElement(value, _json);
    }

    private static JsonElement CreateRunEvent(string eventType, JsonElement? parameters)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", eventType);

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

    private static IReadOnlyList<object> NormalizeToolResponseItems(IReadOnlyList<CodexDynamicToolContentItem> items)
    {
        var normalized = new List<object>();
        foreach (var item in items)
        {
            if (string.Equals(item.Type, "input_image", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Type, "inputImage", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(item.ImageUrl))
                {
                    continue;
                }

                normalized.Add(new
                {
                    type = "inputImage",
                    imageUrl = item.ImageUrl,
                });
                continue;
            }

            normalized.Add(new
            {
                type = "inputText",
                text = item.Text ?? string.Empty,
            });
        }

        if (normalized.Count == 0)
        {
            normalized.Add(new
            {
                type = "inputText",
                text = string.Empty,
            });
        }

        return normalized;
    }

    private void EmitDynamicToolLifecycleEvent(
        string eventType,
        CodexDynamicToolCallRequest request,
        CodexDynamicToolCallResponse? response,
        long? durationMs)
    {
        var run = GetActiveRun();
        if (run == null)
        {
            return;
        }

        var eventTurnId = request.TurnId ?? run.TurnId;
        var eventThreadId = request.ThreadId ?? CurrentCodexThreadId;
        var callId = string.IsNullOrWhiteSpace(request.CallId)
            ? $"dynamic-call-{Guid.NewGuid():N}"
            : request.CallId;

        var eventParams = SerializeToElement(new
        {
            threadId = eventThreadId,
            turnId = eventTurnId,
            item = new
            {
                type = "dynamicToolCall",
                id = callId,
                tool = request.Tool,
                status = response == null ? "inProgress" : response.Success ? "completed" : "failed",
                arguments = request.Arguments,
                result = response is { Success: true } ? BuildDynamicToolResultPayload(response) : null,
                error = response is { Success: false } ? BuildDynamicToolErrorPayload(response) : null,
                durationMs = response == null ? null : durationMs,
            },
        });

        if (!ShouldForwardToActiveRun(eventType, eventParams, run))
        {
            return;
        }

        run.TryWriteEvent(new CodexTurnEventEnvelope
        {
            Type = "event",
            Event = CreateRunEvent(eventType, eventParams),
            RequestId = run.RequestId,
            ThreadId = eventThreadId,
            TurnId = eventTurnId,
        });
    }

    private static object BuildDynamicToolResultPayload(CodexDynamicToolCallResponse response)
    {
        return new
        {
            content = NormalizeToolResponseItems(response.ContentItems),
            structuredContent = (object?)null,
        };
    }

    private static object BuildDynamicToolErrorPayload(CodexDynamicToolCallResponse response)
    {
        var message = response.ContentItems
            .FirstOrDefault(static item => !string.IsNullOrWhiteSpace(item.Text))
            ?.Text
            ?? "Dynamic tool call failed.";

        return new
        {
            message,
        };
    }

    private static CodexDynamicToolCallResponse BuildToolBridgeFailure(string message)
    {
        return new CodexDynamicToolCallResponse
        {
            Success = false,
            ContentItems =
            [
                new CodexDynamicToolContentItem
                {
                    Type = "input_text",
                    Text = message,
                },
            ],
        };
    }

    private sealed class InternalToolCompletion
    {
        public string Status { get; init; } = "success";

        public JsonElement Result { get; init; }

        public JsonElement? Error { get; init; }
    }

    private sealed class InternalToolSpan
    {
        public string ToolCallId { get; init; } = string.Empty;

        public string ToolName { get; set; } = string.Empty;

        public JsonElement ArgumentsJson { get; set; }

        public bool HasArguments { get; set; }

        public DateTimeOffset StartedAt { get; set; }

        public DateTimeOffset CompletedAt { get; set; }

        public JsonElement ResultJson { get; set; }

        public bool HasResult { get; set; }

        public string? Status { get; set; }

        public string? StartSource { get; set; }

        public string? EndSource { get; set; }

        public bool CallEmitted { get; set; }

        public bool ResultEmitted { get; set; }
    }

    private sealed class ActiveRunState
    {
        private readonly object _stateLock = new();
        private readonly Channel<CodexTurnEventEnvelope> _events = Channel.CreateUnbounded<CodexTurnEventEnvelope>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });

        private bool _completed;
        private bool _pendingInterrupt;
        private string? _turnId;

        public ActiveRunState(string requestId)
        {
            RequestId = requestId;
            Completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public string RequestId { get; }

        public TaskCompletionSource<string?> Completion { get; }

        public string? TurnId
        {
            get
            {
                lock (_stateLock)
                {
                    return _turnId;
                }
            }
        }

        public void SetTurnId(string? turnId)
        {
            if (string.IsNullOrWhiteSpace(turnId))
            {
                return;
            }

            lock (_stateLock)
            {
                _turnId = turnId;
            }
        }

        public void MarkPendingInterrupt()
        {
            lock (_stateLock)
            {
                _pendingInterrupt = true;
            }
        }

        public bool TryConsumePendingInterrupt()
        {
            lock (_stateLock)
            {
                if (!_pendingInterrupt)
                {
                    return false;
                }

                _pendingInterrupt = false;
                return true;
            }
        }

        public void TryWriteEvent(CodexTurnEventEnvelope eventEnvelope)
        {
            _ = _events.Writer.TryWrite(eventEnvelope);
        }

        public IAsyncEnumerable<CodexTurnEventEnvelope> ReadAllEventsAsync(CancellationToken ct)
        {
            return _events.Reader.ReadAllAsync(ct);
        }

        public void TryComplete(string? turnId)
        {
            lock (_stateLock)
            {
                if (_completed)
                {
                    return;
                }

                _completed = true;
                _turnId = string.IsNullOrWhiteSpace(turnId) ? _turnId : turnId;
            }

            _ = Completion.TrySetResult(_turnId);
            _events.Writer.TryComplete();
        }

        public void TryFail(Exception exception)
        {
            lock (_stateLock)
            {
                if (_completed)
                {
                    return;
                }

                _completed = true;
            }

            _ = Completion.TrySetException(exception);
            _events.Writer.TryComplete();
        }
    }
}
