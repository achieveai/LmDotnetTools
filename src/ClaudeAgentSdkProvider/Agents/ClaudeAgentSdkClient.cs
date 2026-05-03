using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Exceptions;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models.JsonlEvents;
#pragma warning disable IDE0058 // Expression value is never used
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Parsers;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Agents;

/// <summary>
///     Real implementation of IClaudeAgentSdkClient using Node.js process
///     Manages long-lived claude-agent-sdk CLI process
/// </summary>
public class ClaudeAgentSdkClient : IClaudeAgentSdkClient
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogger<ClaudeAgentSdkClient>? _logger;
    private readonly ClaudeAgentSdkOptions _options;
    private readonly JsonlStreamParser _parser;
    private bool _disposed;

    // Process and stream management
    private Process? _process;
    private StreamReader? _stderrReader;
    private StreamWriter? _stdinWriter;
    private StreamReader? _stdoutReader;
    private string? _systemPromptTempFile;

    // Shutdown and lifecycle management
    private Task? _stderrMonitorTask;
    private CancellationTokenSource? _shutdownCts;
    private volatile int _state; // 0=NotStarted, 1=Starting, 2=Running, 3=ShuttingDown, 4=Stopped
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    // Keepalive for Interactive mode
    private Task? _keepaliveTask;
    private CancellationTokenSource? _keepaliveCts;

    // Thread-safe stdin writing
    private readonly SemaphoreSlim _stdinSemaphore = new(1, 1);

    // Subscription tracking (single subscriber only)
    private volatile bool _subscriptionActive;

    public ClaudeAgentSdkClient(ClaudeAgentSdkOptions options, ILogger<ClaudeAgentSdkClient>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        // Create parser with shared logger for consistent multi-modal content parsing logs
        _parser = new JsonlStreamParser(logger);
    }

    public bool IsRunning => _process != null && !_process.HasExited;
    public SessionInfo? CurrentSession { get; private set; }

    /// <summary>
    ///     The last request used to start the client. Can be used for restart.
    /// </summary>
    public ClaudeAgentSdkRequest? LastRequest { get; private set; }

    public async Task StartAsync(ClaudeAgentSdkRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        _logger?.LogDebug(
            "StartAsync called. Current state: {State}, IsRunning: {IsRunning}, HasProcess: {HasProcess}",
            _state,
            IsRunning,
            _process != null);

        // Store the request for potential restart
        LastRequest = request;

        // State transition: NotStarted/Stopped -> Starting
        var currentState = Interlocked.CompareExchange(ref _state, 1, 0);
        if (currentState != 0)
        {
            // Try from Stopped state (allows restart)
            currentState = Interlocked.CompareExchange(ref _state, 1, 4);
            if (currentState != 4)
            {
                // Handle state desync - process exited but _state still Running
                if (currentState == 2 && !IsRunning)
                {
                    _logger?.LogWarning(
                        "State desync detected: _state={State}, IsRunning={IsRunning}, HasExited={HasExited}. Resetting to allow restart.",
                        currentState,
                        IsRunning,
                        _process?.HasExited);
                    // Force state to Stopped, then try again
                    _ = Interlocked.Exchange(ref _state, 4);
                    currentState = Interlocked.CompareExchange(ref _state, 1, 4);
                    if (currentState != 4)
                    {
                        throw new InvalidOperationException(
                            $"Cannot start after state reset: state is {currentState}.");
                    }
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Cannot start: invalid state {currentState}. Expected NotStarted(0) or Stopped(4).");
                }
            }
        }

        try
        {
            _logger?.LogInformation("Starting claude-agent-sdk CLI process with model {Model}", request.ModelId);

            // 1. Locate the CLI. Two supported forms:
            //    - claude(.exe): standalone native binary from @anthropic-ai/claude-code
            //                    (the modern path, no Node.js needed)
            //    - cli.js:       legacy bundle from @anthropic-ai/claude-agent-sdk@0.1.x
            //                    (still supported; spawned via Node)
            // We prefer the native binary when both are installed because 0.1.x carries
            // a known null-deref in the OAuth client_data path (see ResultEvent failure
            // handler below for how that surfaces).
            var cliPath = _options.CliPath ?? FindClaudeCli();
            var useNativeBinary = IsNativeClaudeBinary(cliPath);
            _logger?.LogDebug(
                "Using Claude CLI at: {CliPath} ({Mode})",
                cliPath,
                useNativeBinary ? "native binary" : "Node.js bundle");

            // 1a. Sanity-check the SDK package version. The CLI surfaces internal exceptions
            // through ResultEvent.Subtype = "error_during_execution" with IsError = false,
            // so without explicit handling (see SubscribeToMessagesAsync / SendMessagesAsync)
            // failed turns look like clean completions and the caller silently advances.
            // Recent versions also carry upstream fixes for known bugs (e.g. the v0.1.55
            // null-deref in x80() on the OAuth client_data path); newer is preferable.
            WarnIfStaleSdkVersion(cliPath);

            // 2. Locate Node.js only if we need it (legacy cli.js path)
            string? nodePath = null;
            if (!useNativeBinary)
            {
                nodePath = _options.NodeJsPath ?? FindNodeJs();
                _logger?.LogDebug("Using Node.js at: {NodePath}", nodePath);
            }

            // 3. Create system prompt temp file if provided
            if (!string.IsNullOrEmpty(request.SystemPrompt))
            {
                _systemPromptTempFile = Path.GetTempFileName();
                await File.WriteAllTextAsync(_systemPromptTempFile, request.SystemPrompt, cancellationToken);
                _logger?.LogDebug("Created system prompt temp file: {File}", _systemPromptTempFile);
            }

            // 4. Build CLI arguments. The arg surface differs between the two CLIs — the
            // modern claude.exe (2.x) supports --effort directly; the legacy cli.js (0.1.x)
            // doesn't, and unknown flags crash it silently. BuildCliArguments knows which
            // form to emit based on useNativeBinary.
            var args = BuildCliArguments(request, useNativeBinary);
            _logger?.LogDebug(
                "CLI invocation: {Exe} {Args}",
                useNativeBinary ? cliPath : $"{nodePath} \"{cliPath}\"",
                args);

            // 5. Configure process. Native binary is invoked directly; cli.js goes through Node.
            var startInfo = new ProcessStartInfo
            {
                FileName = useNativeBinary ? cliPath : nodePath!,
                Arguments = useNativeBinary ? args : $"\"{cliPath}\" {args}",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = _options.ProjectRoot ?? Directory.GetCurrentDirectory(),
                // Use UTF-8 encoding for reading from Node.js process
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };

            // Set environment variable for stream close timeout (in seconds)
            startInfo.Environment["CLAUDE_CODE_STREAM_CLOSE_TIMEOUT"] = "300";

            // Reasoning-effort wiring depends on the CLI form:
            // - claude.exe 2.x exposes a documented --effort flag (added in BuildCliArguments).
            // - Legacy cli.js (0.1.x) doesn't support it (and unknown flags crash it), so we
            //   fall back to the MAX_THINKING_TOKENS env var with the canonical token-count
            //   mapping the CLI used internally for that version.
            if (!useNativeBinary)
            {
                var thinkingTokens = TokenBudgetForEffort(request.ReasoningEffort);
                if (thinkingTokens != null)
                {
                    startInfo.Environment["MAX_THINKING_TOKENS"] = thinkingTokens.Value.ToString();
                    _logger?.LogInformation(
                        "Reasoning effort '{Effort}' mapped to MAX_THINKING_TOKENS={Tokens} (legacy cli.js path)",
                        request.ReasoningEffort,
                        thinkingTokens);
                }
            }

            // 6. Start process
            _process = Process.Start(startInfo);
            if (_process == null)
            {
                throw new InvalidOperationException("Failed to start claude-agent-sdk process");
            }

            // Use UTF-8 WITHOUT BOM for writing to Node.js process
            // BOM would corrupt the first JSON line and cause parsing failures
            _stdinWriter = new StreamWriter(
                _process.StandardInput.BaseStream,
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = false, // We manually flush after writing
            };
            _stdoutReader = _process.StandardOutput;
            _stderrReader = _process.StandardError;

            // 7. Create shutdown CTS and start stderr monitor in background (tracked)
            _shutdownCts = new CancellationTokenSource();
            _stderrMonitorTask = Task.Run(() => MonitorStdErrWithPolling(_shutdownCts.Token), cancellationToken);

            // 8. Start keepalive task (Interactive mode only)
            if (_options.Mode == ClaudeAgentSdkMode.Interactive)
            {
                _keepaliveCts = new CancellationTokenSource();
                _keepaliveTask = Task.Run(() => RunKeepaliveAsync(_keepaliveCts.Token), cancellationToken);
            }

            // 9. Create session info
            CurrentSession = new SessionInfo
            {
                SessionId = request.SessionId ?? Guid.NewGuid().ToString(),
                CreatedAt = DateTime.UtcNow,
                ProjectRoot = _options.ProjectRoot,
            };

            _logger?.LogInformation(
                "[Agent:{SessionId}] claude-agent-sdk CLI started successfully. PID: {ProcessId}",
                CurrentSession?.SessionId,
                _process.Id
            );

            // Transition to Running state
            var startState = Interlocked.CompareExchange(ref _state, 2, 1);
            if (startState != 1)
            {
                throw new InvalidOperationException($"State changed during startup from 1 to {startState}");
            }
        }
        catch
        {
            // Reset to NotStarted on failure
            _ = Interlocked.Exchange(ref _state, 0);
            throw;
        }
    }

    public async IAsyncEnumerable<IMessage> SendMessagesAsync(
        IEnumerable<IMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(messages);

        _logger?.LogDebug(
            "SendMessagesAsync called. State: {State}, IsRunning: {IsRunning}, Mode: {Mode}",
            _state,
            IsRunning,
            _options.Mode);

        // In Interactive mode, throw to guide users to the new API
        if (_options.Mode == ClaudeAgentSdkMode.Interactive)
        {
            throw new InvalidOperationException(
                "SendMessagesAsync is not supported in Interactive mode. " +
                "Use SendAsync() to write messages and SubscribeToMessagesAsync() to read responses.");
        }

        if (!IsRunning)
        {
            throw new InvalidOperationException("Client is not running. Call StartAsync first.");
        }

        // Get all user messages to combine text + images in a single turn
        // (claude-agent-sdk maintains its own conversation history, so we only send current user input)
        var userMessages = messages.Where(m => m.Role == Role.User).ToList();

        if (userMessages.Count == 0)
        {
            _logger?.LogWarning("No user message found in the message collection");
            yield break;
        }

        // Convert to JSONL input format and send to stdin
        if (_stdinWriter != null)
        {
            // Combine all user messages into a single InputMessage with multiple content blocks
            var inputWrapper = ConvertToInputMessage(userMessages);
            var jsonLine = JsonSerializer.Serialize(inputWrapper, _jsonOptions);

            _logger?.LogDebug(
                "Sending JSONL message to claude-agent-sdk: {Message}",
                jsonLine.Length > 200 ? jsonLine[..200] + "..." : jsonLine
            );

            var (firstText, lastText) = GetFirstLastTextMessages(userMessages);
            _logger?.LogInformation(
                "[Agent:{SessionId}] Submitting to LLM - MessageCount: {Count}, FirstText: {First}, LastText: {Last}",
                CurrentSession?.SessionId,
                userMessages.Count,
                firstText ?? "(none)",
                lastText ?? "(same as first)");

            await _stdinWriter.WriteLineAsync(jsonLine);
            await _stdinWriter.FlushAsync(cancellationToken);

            // In OneShot mode, close stdin immediately after sending the message
            // This signals the CLI to run to completion and exit
            if (_options.Mode == ClaudeAgentSdkMode.OneShot)
            {
                _logger?.LogDebug("OneShot mode: Closing stdin to signal completion");
                _stdinWriter.Close();
                _stdinWriter = null;
            }
        }

        var isWorking = false;

        // Read and parse stdout for the response
        await foreach (var line in ReadStdoutLinesAsync(cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var jsonlEvent = _parser.ParseLine(line);

            if (jsonlEvent == null)
            {
                _logger?.LogWarning("Failed to parse JSONL line (length={Length}): {Line}",
                    line.Length, line.Length > 200 ? line[..200] + "..." : line);
                continue;
            }

            if (jsonlEvent is AssistantMessageEvent assistantEvent)
            {
                // Check for API errors (e.g., billing_error) before processing normal messages
                if (assistantEvent.IsApiErrorMessage)
                {
                    var errorText = assistantEvent.Message?.Content?.FirstOrDefault()?.Text;
                    _logger?.LogError(
                        "[Agent:{SessionId}] API error received: Type={ErrorType}, Message={ErrorMessage}. Agent must be recreated.",
                        CurrentSession?.SessionId,
                        assistantEvent.Error,
                        errorText);

                    throw new BillingErrorException(
                        assistantEvent.Error,
                        errorText,
                        CurrentSession?.SessionId);
                }

                var eventMessages = _parser.ConvertToMessages(assistantEvent).ToList();
                _logger?.LogTrace(
                    "AssistantMessageEvent: MessageId={MessageId}, BlockCount={BlockCount}, Messages=[{Messages}]",
                    assistantEvent.Message?.Id,
                    assistantEvent.Message?.Content?.Count ?? 0,
                    string.Join("; ", eventMessages.Select(m => FormatMessageForLog(m, 80))));
                foreach (var msg in eventMessages)
                {
                    isWorking = true;
                    yield return msg;
                }
            }
            // User messages contain tool results and other user inputs
            else if (jsonlEvent is UserMessageEvent userEvent)
            {
                var eventMessages = _parser.ConvertToMessages(userEvent).ToList();
                _logger?.LogTrace(
                    "UserMessageEvent: Uuid={Uuid}, Role={Role}, Messages=[{Messages}]",
                    userEvent.Uuid,
                    userEvent.Message?.Role,
                    string.Join("; ", eventMessages.Select(m => FormatMessageForLog(m, 80))));
                foreach (var msg in eventMessages)
                {
                    yield return msg;
                }
            }
            // Summary events are informational, we can log them but don't emit as messages
            else if (jsonlEvent is SummaryEvent summaryEvent)
            {
                _logger?.LogDebug("Summary: {Summary}", summaryEvent.Summary);
            }
            // Rate-limit advisories from claude-code 2.x — log per-status so a throttled
            // or denied request is obvious without flooding the log on the happy path.
            else if (jsonlEvent is RateLimitEvent rateLimitEvent)
            {
                LogRateLimit(rateLimitEvent);
            }
            // System init events contain session info and available tools
            else if (jsonlEvent is SystemInitEvent systemInitEvent)
            {
                _logger?.LogInformation(
                    "[Agent:{SessionId}] System initialized - Model: {Model}, Tools: {ToolCount}, MCP Servers: {McpServers}",
                    CurrentSession?.SessionId,
                    systemInitEvent.Model,
                    systemInitEvent.Tools?.Count ?? 0,
                    string.Join(", ", systemInitEvent.McpServers?.Select(s => $"{s.Name}({s.Status})") ?? [])
                );

                // Update current session with info from init event
                if (CurrentSession != null && !string.IsNullOrEmpty(systemInitEvent.SessionId))
                {
                    CurrentSession = CurrentSession with { SessionId = systemInitEvent.SessionId };
                }
            }
            // Result events contain final execution summary with usage and cost info
            else if (jsonlEvent is ResultEvent resultEvent)
            {
                var failed = !IsSuccessSubtype(resultEvent.Subtype);
                _logger?.LogTrace(
                    "Received ResultEvent: Subtype={Subtype}, IsError={IsError}, NumTurns={NumTurns}, ApiMs={ApiMs}, SessionId={SessionId}",
                    resultEvent.Subtype,
                    resultEvent.IsError,
                    resultEvent.NumTurns,
                    resultEvent.DurationApiMs,
                    resultEvent.SessionId);

                if (failed)
                {
                    LogResultFailure(resultEvent);

                    if (isWorking && _stdinWriter != null)
                    {
                        // Mid-turn failure with an open stdin — ask the model to retry. We avoid
                        // doing this for "cold" failures (no assistant content yet) because those
                        // typically indicate a CLI/config problem that retrying won't fix.
                        _logger?.LogWarning(
                            "Failed turn (subtype={Subtype}) with prior assistant output present; sending retry.",
                            resultEvent.Subtype);

                        isWorking = false;
                        var jsonLine = JsonSerializer.Serialize(
                            new InputMessageWrapper
                            {
                                Type = "user",
                                Message = new InputMessage
                                {
                                    Role = "user",
                                    Content =
                                    [
                                        new InputTextContentBlock
                                        {
                                            Text = "retry...."
                                        }
                                    ]
                                }
                            }, _jsonOptions);

                        _logger?.LogDebug(
                            "Sending JSONL message to claude-agent-sdk: {Message}",
                            jsonLine.Length > 200 ? jsonLine[..200] + "..." : jsonLine
                        );

                        await _stdinWriter.WriteLineAsync(jsonLine);
                        await _stdinWriter.FlushAsync(cancellationToken);

                        continue;
                    }
                }

                if (resultEvent.PermissionDenials?.Count > 0)
                {
                    _logger?.LogWarning(
                        "Permission denials: {Denials}",
                        string.Join(", ", resultEvent.PermissionDenials)
                    );
                }

                // ResultEvent signals the end of current turn in BOTH modes
                // In OneShot mode: stdin is already closed, process will exit
                // In Interactive mode: stdin remains open for next SendMessagesAsync call
                _logger?.LogDebug(
                    "{Mode} mode: ResultEvent received (subtype={Subtype}), ending current turn",
                    _options.Mode,
                    resultEvent.Subtype
                );

                // In OneShot mode, clean up BEFORE returning from the iterator
                if (_options.Mode == ClaudeAgentSdkMode.OneShot)
                {
                    await WaitForOneShotCompletionAsync(cancellationToken);
                }

                yield break;
            }
            else
            {
                _logger?.LogWarning("Unhandled JSONL event type: {EventType}. Line was not processed.",
                    jsonlEvent.GetType().Name);
            }
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<IMessage> SubscribeToMessagesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!IsRunning)
        {
            throw new InvalidOperationException("Client is not running. Call StartAsync first.");
        }

        if (_subscriptionActive)
        {
            throw new InvalidOperationException(
                "A subscription is already active. Only one subscriber is allowed at a time.");
        }

        _subscriptionActive = true;
        _logger?.LogDebug("Subscription started for stdout messages");

        try
        {
            await foreach (var line in ReadStdoutLinesAsync(cancellationToken))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var jsonlEvent = _parser.ParseLine(line);

                if (jsonlEvent == null)
                {
                    _logger?.LogWarning(
                        "Failed to parse JSONL line (length={Length}): {Line}",
                        line.Length,
                        line.Length > 200 ? line[..200] + "..." : line);
                    continue;
                }

                if (jsonlEvent is AssistantMessageEvent assistantEvent)
                {
                    // Check for API errors (e.g., billing_error) before processing normal messages
                    if (assistantEvent.IsApiErrorMessage)
                    {
                        var errorText = assistantEvent.Message?.Content?.FirstOrDefault()?.Text;
                        _logger?.LogError(
                            "[Agent:{SessionId}] API error received: Type={ErrorType}, Message={ErrorMessage}. Agent must be recreated.",
                            CurrentSession?.SessionId,
                            assistantEvent.Error,
                            errorText);

                        throw new BillingErrorException(
                            assistantEvent.Error,
                            errorText,
                            CurrentSession?.SessionId);
                    }

                    var eventMessages = _parser.ConvertToMessages(assistantEvent).ToList();
                    _logger?.LogTrace(
                        "AssistantMessageEvent: MessageId={MessageId}, BlockCount={BlockCount}, Messages=[{Messages}]",
                        assistantEvent.Message?.Id,
                        assistantEvent.Message?.Content?.Count ?? 0,
                        string.Join("; ", eventMessages.Select(m => FormatMessageForLog(m, 80))));

                    foreach (var msg in eventMessages)
                    {
                        yield return msg;
                    }
                }
                else if (jsonlEvent is UserMessageEvent userEvent)
                {
                    var eventMessages = _parser.ConvertToMessages(userEvent).ToList();
                    _logger?.LogTrace(
                        "UserMessageEvent: Uuid={Uuid}, Role={Role}, Messages=[{Messages}]",
                        userEvent.Uuid,
                        userEvent.Message?.Role,
                        string.Join("; ", eventMessages.Select(m => FormatMessageForLog(m, 80))));

                    foreach (var msg in eventMessages)
                    {
                        yield return msg;
                    }
                }
                else if (jsonlEvent is SummaryEvent summaryEvent)
                {
                    _logger?.LogDebug("Summary: {Summary}", summaryEvent.Summary);
                }
                else if (jsonlEvent is RateLimitEvent rateLimitEvent)
                {
                    LogRateLimit(rateLimitEvent);
                }
                else if (jsonlEvent is SystemInitEvent systemInitEvent)
                {
                    _logger?.LogInformation(
                        "[Agent:{SessionId}] System initialized - Model: {Model}, Tools: {ToolCount}",
                        CurrentSession?.SessionId,
                        systemInitEvent.Model,
                        systemInitEvent.Tools?.Count ?? 0);

                    if (CurrentSession != null && !string.IsNullOrEmpty(systemInitEvent.SessionId))
                    {
                        CurrentSession = CurrentSession with { SessionId = systemInitEvent.SessionId };
                    }

                    // Yield SystemInitMessage to signal run start
                    yield return new SystemInitMessage
                    {
                        SessionId = systemInitEvent.SessionId,
                        Model = systemInitEvent.Model,
                    };
                }
                else if (jsonlEvent is ResultEvent resultEvent)
                {
                    var failed = !IsSuccessSubtype(resultEvent.Subtype);
                    _logger?.LogTrace(
                        "Received ResultEvent: Subtype={Subtype}, IsError={IsError}, NumTurns={NumTurns}, ApiMs={ApiMs}, SessionId={SessionId}",
                        resultEvent.Subtype,
                        resultEvent.IsError,
                        resultEvent.NumTurns,
                        resultEvent.DurationApiMs,
                        resultEvent.SessionId);

                    if (failed)
                    {
                        LogResultFailure(resultEvent);
                    }

                    // Yield a marker so the caller (e.g. duplex state machine) can react. We
                    // mark IsError based on Subtype, NOT the upstream is_error flag — see
                    // ResultEventMessage docs for why.
                    yield return new ResultEventMessage
                    {
                        Subtype = resultEvent.Subtype,
                        IsError = failed,
                        Result = resultEvent.Result,
                        Errors = resultEvent.Errors ?? [],
                        NumTurns = resultEvent.NumTurns,
                        DurationMs = resultEvent.DurationMs,
                        DurationApiMs = resultEvent.DurationApiMs,
                    };
                }
                else
                {
                    _logger?.LogWarning("Unhandled JSONL event type: {EventType}. Line was not processed.",
                        jsonlEvent.GetType().Name);
                }
            }
        }
        finally
        {
            _subscriptionActive = false;
            _logger?.LogDebug("Subscription ended for stdout messages");
        }
    }

    /// <inheritdoc />
    public async Task SendAsync(IEnumerable<IMessage> messages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (!IsRunning || _stdinWriter == null)
        {
            throw new InvalidOperationException("Client is not running. Call StartAsync first.");
        }

        var userMessages = messages.Where(m => m.Role == Role.User).ToList();
        if (userMessages.Count == 0)
        {
            _logger?.LogWarning("No user message found in the message collection");
            return;
        }

        await _stdinSemaphore.WaitAsync(cancellationToken);
        try
        {
            var inputWrapper = ConvertToInputMessage(userMessages);
            var jsonLine = JsonSerializer.Serialize(inputWrapper, _jsonOptions);

            _logger?.LogDebug(
                "Sending JSONL message to claude-agent-sdk: {Message}",
                jsonLine.Length > 200 ? jsonLine[..200] + "..." : jsonLine);

            var (firstText, lastText) = GetFirstLastTextMessages(userMessages);
            _logger?.LogInformation(
                "[Agent:{SessionId}] Submitting to LLM (Interactive) - MessageCount: {Count}, FirstText: {First}, LastText: {Last}",
                CurrentSession?.SessionId,
                userMessages.Count,
                firstText ?? "(none)",
                lastText ?? "(same as first)");

            await _stdinWriter.WriteLineAsync(jsonLine);
            await _stdinWriter.FlushAsync(cancellationToken);
        }
        finally
        {
            _stdinSemaphore.Release();
        }
    }

    /// <summary>
    ///     Write an empty line to stdin for keepalive.
    ///     Thread-safe: uses semaphore.
    ///     NOTE: Disabled because the claude-agent-sdk CLI expects JSONL input.
    ///     Empty lines cause "SyntaxError: Unexpected end of JSON input" and crash the CLI.
    ///     The CLI stays alive as long as stdin is open, so keepalive is not needed.
    /// </summary>
    private Task WriteEmptyLineAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken; // Unused - keepalive disabled
        // The claude-agent-sdk CLI expects JSONL input - empty lines cause parse errors.
        // Keepalive is not needed as the CLI process stays alive as long as stdin is open.
        _logger?.LogTrace("[Agent:{SessionId}] Keepalive: skipped (empty lines crash CLI)", CurrentSession?.SessionId);
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Run keepalive loop for Interactive mode.
    ///     Sends empty lines periodically to keep the connection alive.
    /// </summary>
    private async Task RunKeepaliveAsync(CancellationToken cancellationToken)
    {
        if (_options.Mode != ClaudeAgentSdkMode.Interactive)
        {
            return;
        }

        _logger?.LogDebug("[Agent:{SessionId}] Keepalive task started with interval: {Interval}", CurrentSession?.SessionId, _options.KeepAliveInterval);

        using var timer = new PeriodicTimer(_options.KeepAliveInterval);

        while (!cancellationToken.IsCancellationRequested && IsRunning)
        {
            try
            {
                await timer.WaitForNextTickAsync(cancellationToken);
                await WriteEmptyLineAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Keepalive failed");
                break; // Let next operation detect dead process
            }
        }

        _logger?.LogDebug("[Agent:{SessionId}] Keepalive task stopped", CurrentSession?.SessionId);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _logger?.LogInformation("[Agent:{SessionId}] Disposing claude-agent-sdk client", CurrentSession?.SessionId);

            // Close stdin to signal process to exit gracefully
            _stdinWriter?.Close();

            // Give process time to exit gracefully
            var exited = _process?.WaitForExit(5000) ?? true;

            // Force kill if still running
            if (_process != null && !_process.HasExited)
            {
                _logger?.LogWarning("Force killing claude-agent-sdk process (PID: {ProcessId})", _process.Id);
                _process.Kill(true);
            }

            // Clean up temp file
            if (_systemPromptTempFile != null && File.Exists(_systemPromptTempFile))
            {
                try
                {
                    File.Delete(_systemPromptTempFile);
                    _logger?.LogDebug("Deleted system prompt temp file: {File}", _systemPromptTempFile);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to delete temp file: {File}", _systemPromptTempFile);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during disposal");
        }
        finally
        {
            _keepaliveCts?.Cancel();
            _keepaliveCts?.Dispose();
            _process?.Dispose();
            _stdinWriter?.Dispose();
            _stdoutReader?.Dispose();
            _stderrReader?.Dispose();
            _stdinSemaphore.Dispose();
            _disposed = true;
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Send /exit command to gracefully terminate the interactive session.
    ///     Only applicable in Interactive mode.
    /// </summary>
    public async Task<bool> SendExitCommandAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRunning || _stdinWriter == null)
        {
            return false;
        }

        if (_options.Mode != ClaudeAgentSdkMode.Interactive)
        {
            _logger?.LogDebug("SendExitCommandAsync: Not in interactive mode, skipping");
            return false;
        }

        try
        {
            _logger?.LogInformation("Sending /exit command to claude-agent-sdk");

            var exitMsg = new InputMessageWrapper
            {
                Type = "user",
                Message = new InputMessage
                {
                    Role = "user",
                    Content = [new InputTextContentBlock { Text = "/exit" }],
                },
            };

            var json = JsonSerializer.Serialize(exitMsg, _jsonOptions);
            await _stdinWriter.WriteLineAsync(json);
            await _stdinWriter.FlushAsync(cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to send /exit command");
            return false;
        }
    }

    /// <summary>
    ///     Initiates graceful shutdown of the underlying process using layered approach:
    ///     1. Send /exit command (graceful)
    ///     2. Close stdin (signal EOF)
    ///     3. Wait for process exit with timeout
    ///     4. Force kill if still running
    ///     5. Wait for stderr monitor task
    /// </summary>
    public async Task ShutdownAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        // State transition: Running -> ShuttingDown
        var currentState = Interlocked.CompareExchange(ref _state, 3, 2);
        if (currentState != 2)
        {
            _logger?.LogDebug("ShutdownAsync: Not in Running state (current: {State}), skipping", currentState);
            return; // Not running or already shutting down
        }

        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(10);
        _logger?.LogInformation("Initiating graceful shutdown (timeout: {Timeout})", effectiveTimeout);

        try
        {
            // Step 1: Send /exit command (graceful)
            var exitSent = await SendExitCommandAsync(cancellationToken);
            if (exitSent)
            {
                // Wait briefly for graceful exit
                await Task.Delay(500, cancellationToken);
                if (_process?.HasExited == true)
                {
                    _logger?.LogInformation("Process exited gracefully after /exit command");
                    goto cleanup;
                }
            }

            // Step 2: Cancel keepalive + shutdown CTS + Close stdin
            _keepaliveCts?.Cancel();
            _shutdownCts?.Cancel();
            _stdinWriter?.Close();
            _stdinWriter = null;

            // Step 3: Wait for process exit with timeout
            if (_process != null && !_process.HasExited)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(effectiveTimeout);

                try
                {
                    await _process.WaitForExitAsync(timeoutCts.Token);
                    _logger?.LogInformation("Process exited with code: {Code}", _process.ExitCode);
                }
                catch (OperationCanceledException)
                {
                    _logger?.LogWarning("Graceful shutdown timed out, force killing");
                }
            }

            // Step 4: Force kill if still running
            if (_process != null && !_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await Task.Delay(100, CancellationToken.None); // Brief wait for kill
            }

        cleanup:
            // Step 5: Wait for stderr monitor task
            if (_stderrMonitorTask != null)
            {
                try
                {
                    await _stderrMonitorTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
                }
                catch
                {
                    // Timeout or cancelled - acceptable
                }
            }

            // Step 6: Wait for keepalive task
            if (_keepaliveTask != null)
            {
                try
                {
                    await _keepaliveTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
                }
                catch
                {
                    // Timeout or cancelled - acceptable
                }

                _keepaliveCts?.Dispose();
                _keepaliveCts = null;
                _keepaliveTask = null;
            }

            // Clean up temp file
            if (_systemPromptTempFile != null && File.Exists(_systemPromptTempFile))
            {
                try
                {
                    File.Delete(_systemPromptTempFile);
                    _logger?.LogDebug("Deleted system prompt temp file: {File}", _systemPromptTempFile);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to delete temp file: {File}", _systemPromptTempFile);
                }

                _systemPromptTempFile = null;
            }
        }
        finally
        {
            // Transition to Stopped state (allows restart)
            _state = 4;
            _logger?.LogInformation("Shutdown complete, state: Stopped");
        }
    }

    /// <summary>
    ///     Async disposal that performs graceful shutdown before cleanup.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            // Perform graceful shutdown first
            await ShutdownAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error during async shutdown");
        }
        finally
        {
            // Clean up resources
            _keepaliveCts?.Dispose();
            _shutdownCts?.Dispose();
            _operationLock.Dispose();
            _stdinSemaphore.Dispose();
            _process?.Dispose();
            _stdinWriter?.Dispose();
            _stdoutReader?.Dispose();
            _stderrReader?.Dispose();
            _disposed = true;
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Build the command-line argument string for the Claude CLI. The flag surface differs
    ///     between distributions:
    ///     <list type="bullet">
    ///         <item><c>claude.exe</c> (from <c>@anthropic-ai/claude-code@2.x</c>) exposes the
    ///         documented <c>--effort low|medium|high|xhigh|max</c> flag.</item>
    ///         <item>Legacy <c>cli.js</c> (from <c>@anthropic-ai/claude-agent-sdk@0.1.x</c>)
    ///         does not — and rejects unknown flags by crashing — so reasoning effort there is
    ///         set out-of-band via the <c>MAX_THINKING_TOKENS</c> env var in <see cref="StartAsync"/>.</item>
    ///     </list>
    /// </summary>
    private string BuildCliArguments(ClaudeAgentSdkRequest request, bool useNativeBinary)
    {
        var args = new List<string>
        {
            $"--output-format {request.OutputFormat}",
            $"--input-format {request.InputFormat}",
            $"--model {request.ModelId}",
            $"--max-turns {request.MaxTurns}",
            $"--permission-mode {request.PermissionMode}",
            $"--setting-sources \"{request.SettingSources}\"",
        };

        if (request.Verbose)
        {
            args.Add("--verbose");
        }

        if (!string.IsNullOrEmpty(request.AllowedTools))
        {
            // Use --tools to restrict which built-in tools are available to the model.
            // Note: --allowedTools only controls permission bypass, not tool availability.
            args.Add($"--tools \"{request.AllowedTools}\"");
        }

        if (request.McpServers != null && request.McpServers.Count > 0)
        {
            var mcpConfig = new { mcpServers = request.McpServers };
            var mcpJson = JsonSerializer.Serialize(mcpConfig);
            // Escape for Windows command line
            var escapedJson = mcpJson.Replace("\"", "\\\"");
            args.Add($"--mcp-config \"{escapedJson}\"");
        }

        if (!string.IsNullOrEmpty(request.SessionId))
        {
            args.Add($"--resume {request.SessionId}");
        }

        if (_systemPromptTempFile != null)
        {
            args.Add($"--system-prompt-file \"{_systemPromptTempFile}\"");
        }

        // Disable checkpoints/snapshots if configured
        if (_options.DisableCheckpoints)
        {
            args.Add("--no-checkpoints");
        }

        // Disable session persistence if configured
        if (_options.DisableSessionPersistence)
        {
            args.Add("--no-session-persistence");
        }

        // Reasoning effort: native binary takes the documented flag, legacy CLI gets the
        // env-var fallback (set in StartAsync). Don't pass --effort to legacy cli.js — it
        // doesn't recognize it and crashes.
        if (useNativeBinary)
        {
            var effort = NormalizeEffortLevel(request.ReasoningEffort);
            if (effort != null)
            {
                args.Add($"--effort {effort}");
            }
        }

        return string.Join(" ", args);
    }

    /// <summary>
    ///     Validate and normalize a reasoning-effort string against the levels accepted by
    ///     <c>claude.exe --effort</c>: <c>low</c>, <c>medium</c>, <c>high</c>, <c>xhigh</c>,
    ///     <c>max</c>. Case-insensitive; returns null for empty/unknown input so the caller
    ///     can omit the flag entirely (CLI then uses its default).
    /// </summary>
    private static string? NormalizeEffortLevel(string? effort)
    {
        if (string.IsNullOrWhiteSpace(effort))
        {
            return null;
        }

        return effort.Trim().ToLowerInvariant() switch
        {
            "low" => "low",
            "medium" => "medium",
            "high" => "high",
            "xhigh" => "xhigh",
            "max" => "max",
            _ => null,
        };
    }

    /// <summary>
    ///     Map an effort level back to a thinking-token budget for the legacy
    ///     <c>MAX_THINKING_TOKENS</c> env var used by <c>cli.js</c> (0.1.x). The mapping is the
    ///     canonical one the CLI applied internally:
    ///     <list type="bullet">
    ///         <item><c>low</c>    → 2 048 (&lt; 2K)</item>
    ///         <item><c>medium</c> → 4 096 (&lt; 4K)</item>
    ///         <item><c>high</c>   → 8 192 (&lt; 8K)</item>
    ///         <item><c>xhigh</c>  → 16 384 (&lt; 16K)</item>
    ///         <item><c>max</c>    → 32 768 (&lt; 32K)</item>
    ///     </list>
    ///     Returns null when the effort string is empty or unrecognized.
    /// </summary>
    private static int? TokenBudgetForEffort(string? effort) =>
        NormalizeEffortLevel(effort) switch
        {
            "low" => 2048,
            "medium" => 4096,
            "high" => 8192,
            "xhigh" => 16384,
            "max" => 32768,
            _ => null,
        };

    /// <summary>
    ///     Read the SDK package's <c>package.json</c> and warn on versions known to misbehave.
    ///     Layouts differ between distributions:
    ///     <list type="bullet">
    ///         <item><c>@anthropic-ai/claude-code</c>: <c>bin/claude(.exe)</c>, <c>package.json</c> two levels up.</item>
    ///         <item><c>@anthropic-ai/claude-agent-sdk@0.1.x</c>: <c>cli.js</c>, <c>package.json</c> one level up.</item>
    ///     </list>
    ///     The 0.1.x bundle ships with a null-deref in <c>x80()</c> on the OAuth <c>client_data</c>
    ///     path that surfaces as <c>ResultEvent.Subtype = "error_during_execution"</c> with no
    ///     model output. The subtype-based error handling in <see cref="SubscribeToMessagesAsync"/>
    ///     surfaces those failures cleanly, but the operator should still upgrade.
    /// </summary>
    private void WarnIfStaleSdkVersion(string cliPath)
    {
        try
        {
            var packageJson = FindNearestPackageJson(cliPath);
            if (packageJson == null)
            {
                return;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(packageJson));
            if (!doc.RootElement.TryGetProperty("version", out var versionEl) ||
                versionEl.ValueKind != JsonValueKind.String)
            {
                return;
            }

            var name = doc.RootElement.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : "(unknown)";
            var version = versionEl.GetString();
            _logger?.LogInformation(
                "Claude CLI package: {Name}@{Version} (path: {CliPath})",
                name,
                version,
                cliPath);

            // Known-bad bundle: claude-agent-sdk@0.1.x. Newer claude-code (2.x) ships its
            // own native binary that isn't affected by the cli.js bug, so don't gate on the
            // claude-code version here.
            var isLegacySdk = string.Equals(name, "@anthropic-ai/claude-agent-sdk", StringComparison.Ordinal);
            if (isLegacySdk && !string.IsNullOrEmpty(version) &&
                version.StartsWith("0.1.", StringComparison.Ordinal))
            {
                _logger?.LogWarning(
                    "claude-agent-sdk {Version} silently fails every turn " +
                    "(subtype=error_during_execution, null-deref on effortLevel). Upgrade: " +
                    "npm install -g @anthropic-ai/claude-code (preferred) or " +
                    "npm install -g @anthropic-ai/claude-agent-sdk@latest.",
                    version);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Could not read package.json near {CliPath}", cliPath);
        }
    }

    /// <summary>
    ///     Walk up from <paramref name="filePath"/> until we hit a directory containing
    ///     <c>package.json</c> (skipping <c>node_modules</c> and <c>@anthropic-ai</c> scope dirs).
    ///     Stops at four levels deep — enough for both layouts we support.
    /// </summary>
    private static string? FindNearestPackageJson(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        for (var i = 0; i < 4 && !string.IsNullOrEmpty(dir); i++)
        {
            var candidate = Path.Combine(dir, "package.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    /// <summary>
    ///     A turn is considered successful only when the SDK reports <c>subtype: "success"</c>.
    ///     Any other value (<c>error_during_execution</c>, <c>error_max_turns</c>, etc.) means
    ///     the run failed even if the legacy <c>is_error</c> flag is <c>false</c>.
    /// </summary>
    private static bool IsSuccessSubtype(string? subtype) =>
        string.Equals(subtype, "success", StringComparison.Ordinal);

    /// <summary>
    ///     Emit a structured warning that surfaces the actual SDK-reported errors, the model-API
    ///     duration (zero indicates the request never reached the model), and an upgrade hint
    ///     for the well-known v0.1.x null-effortLevel signature. Without this, a non-success
    ///     <c>ResultEvent</c> in stream-json mode looks like a clean turn end and the caller
    ///     misattributes the failure to "the model didn't call its tools."
    /// </summary>
    private void LogResultFailure(ResultEvent resultEvent)
    {
        var firstError = resultEvent.Errors?.FirstOrDefault();
        var looksLikeEffortLevelBug = firstError != null
            && firstError.Contains("effortLevel", StringComparison.Ordinal)
            && firstError.Contains("null", StringComparison.Ordinal);

        _logger?.LogError(
            "[Agent:{SessionId}] claude-agent-sdk turn failed (subtype={Subtype}, num_turns={NumTurns}, " +
            "duration_api_ms={ApiMs}). The model {ModelInvoked}. " +
            "SDK errors: {Errors}{UpgradeHint}",
            CurrentSession?.SessionId,
            resultEvent.Subtype,
            resultEvent.NumTurns,
            resultEvent.DurationApiMs,
            resultEvent.DurationApiMs is > 0 ? "was invoked but errored after responding" : "was NEVER invoked",
            resultEvent.Errors is { Count: > 0 } e
                ? string.Join(" | ", e.Select(s => TruncateForLog(s, 240)))
                : "(none)",
            looksLikeEffortLevelBug
                ? ". This signature is the known v0.1.x null-effortLevel deref — run: " +
                  "npm install -g @anthropic-ai/claude-agent-sdk@latest"
                : string.Empty);
    }

    /// <summary>
    ///     Surface a rate-limit advisory from the CLI. Logs at INFO when the request was
    ///     allowed (informational), at WARN when not — overage rejection or throttling are
    ///     things the operator wants to know about. Falls back to DEBUG when the payload
    ///     is missing fields we'd need to make a better decision.
    /// </summary>
    private void LogRateLimit(RateLimitEvent ev)
    {
        var info = ev.RateLimitInfo;
        if (info == null)
        {
            _logger?.LogDebug("[Agent:{SessionId}] rate_limit_event with no rate_limit_info payload", ev.SessionId);
            return;
        }

        var allowed = string.Equals(info.Status, "allowed", StringComparison.Ordinal);
        var resetsAtIso = info.ResetsAt is long resets
            ? DateTimeOffset.FromUnixTimeSeconds(resets).ToString("O")
            : "(unknown)";

        if (allowed)
        {
            _logger?.LogInformation(
                "[Agent:{SessionId}] Rate-limit OK: window={Window}, resetsAt={ResetsAt}, usingOverage={UsingOverage}",
                ev.SessionId,
                info.RateLimitType,
                resetsAtIso,
                info.IsUsingOverage);
        }
        else
        {
            _logger?.LogWarning(
                "[Agent:{SessionId}] Rate-limit NOT allowed: status={Status}, window={Window}, " +
                "resetsAt={ResetsAt}, overageStatus={OverageStatus}, overageDisabledReason={OverageReason}",
                ev.SessionId,
                info.Status,
                info.RateLimitType,
                resetsAtIso,
                info.OverageStatus,
                info.OverageDisabledReason);
        }
    }

    /// <summary>
    ///     Returns the Node.js executable name. The OS will resolve it via PATH.
    ///     If Node.js is not in PATH, Process.Start will throw a clear error.
    /// </summary>
    private static string FindNodeJs()
    {
        // Just return "node" and let the OS resolve it via PATH.
        // On Windows, both "node" and "node.exe" work when the OS searches PATH.
        // This is simpler and more reliable than manually searching directories.
        return "node";
    }

    /// <summary>
    ///     Returns true when <paramref name="path"/> looks like the modern self-contained
    ///     <c>claude</c>/<c>claude.exe</c> binary (from <c>@anthropic-ai/claude-code</c>) rather
    ///     than the legacy <c>cli.js</c> bundle (from <c>@anthropic-ai/claude-agent-sdk@0.1.x</c>).
    ///     The native binary embeds its runtime and is spawned directly; the Node bundle needs
    ///     <c>node</c> as a host process.
    /// </summary>
    private static bool IsNativeClaudeBinary(string path)
    {
        var ext = Path.GetExtension(path);
        return string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".cmd", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".bat", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, string.Empty, StringComparison.Ordinal); // unix `claude` binary, no extension
    }

    /// <summary>
    ///     Locate a Claude CLI to drive in stream-json mode, preferring the modern native binary.
    ///     Search order:
    ///     <list type="number">
    ///         <item>npm global prefix from <c>npm root -g</c> (catches non-default prefixes such as nvm-for-windows or Volta).</item>
    ///         <item>Standard Windows <c>%APPDATA%\npm\node_modules</c> (default npm).</item>
    ///         <item>Windows <c>%ProgramFiles%\nodejs\node_modules</c> (system-wide installs).</item>
    ///         <item>Unix <c>/usr/local/lib/node_modules</c>.</item>
    ///     </list>
    ///     Within each location, prefer <c>@anthropic-ai/claude-code/bin/claude(.exe)</c> over the
    ///     legacy <c>@anthropic-ai/claude-agent-sdk/cli.js</c>.
    /// </summary>
    private string FindClaudeCli()
    {
        var roots = EnumerateNodeModulesRoots().ToList();
        _logger?.LogTrace("Searching for Claude CLI in {Count} npm roots: {Roots}", roots.Count, string.Join("; ", roots));

        var binaryName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "claude.exe" : "claude";
        foreach (var root in roots)
        {
            var nativeBinary = Path.Combine(root, "@anthropic-ai", "claude-code", "bin", binaryName);
            if (File.Exists(nativeBinary))
            {
                return nativeBinary;
            }
        }

        // Legacy fallback: cli.js from the older claude-agent-sdk layout.
        foreach (var root in roots)
        {
            var legacyJs = Path.Combine(root, "@anthropic-ai", "claude-agent-sdk", "cli.js");
            if (File.Exists(legacyJs))
            {
                return legacyJs;
            }
        }

        throw new FileNotFoundException(
            "Claude CLI not found. Install one of:\n" +
            "  npm install -g @anthropic-ai/claude-code        (preferred — native binary)\n" +
            "  npm install -g @anthropic-ai/claude-agent-sdk   (legacy cli.js)\n" +
            "Or set ClaudeAgentSdkOptions.CliPath explicitly.");
    }

    /// <summary>
    ///     Enumerate plausible global <c>node_modules</c> roots in priority order, deduplicated.
    ///     Querying <c>npm root -g</c> is the most reliable way to handle custom prefixes
    ///     (nvm-for-windows, Volta, custom <c>npm config set prefix</c>); we still fall back to
    ///     well-known platform defaults if that probe fails.
    /// </summary>
    private IEnumerable<string> EnumerateNodeModulesRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Highest-confidence source: ask npm directly. Often fails when npm.cmd isn't on
        // PATH for the host .NET process (common with nvm-for-windows / Volta), so we
        // also probe via Node — cli.js needs Node on PATH anyway.
        foreach (var probed in new[] { TryGetNpmGlobalRoot(), TryGetNodeGlobalRoot() })
        {
            if (probed != null && seen.Add(probed))
            {
                yield return probed;
            }
        }

        // node.exe / node living in <root>\nodejs\... ⇒ siblings have node_modules.
        // Catches nvm-for-windows installs at C:\nvm4w\nodejs\, Volta toolchains, etc.
        foreach (var nodeDir in TryFindNodeInstallDirs())
        {
            var siblingModules = Path.Combine(nodeDir, "node_modules");
            if (Directory.Exists(siblingModules) && seen.Add(siblingModules))
            {
                yield return siblingModules;
            }
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var defaultRoaming = Path.Combine(appData, "npm", "node_modules");
            if (seen.Add(defaultRoaming))
            {
                yield return defaultRoaming;
            }

            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var nodejsSysRoot = Path.Combine(programFiles, "nodejs", "node_modules");
            if (seen.Add(nodejsSysRoot))
            {
                yield return nodejsSysRoot;
            }
        }
        else
        {
            var unixDefault = "/usr/local/lib/node_modules";
            if (seen.Add(unixDefault))
            {
                yield return unixDefault;
            }

            var homebrewArm = "/opt/homebrew/lib/node_modules";
            if (seen.Add(homebrewArm))
            {
                yield return homebrewArm;
            }
        }
    }

    /// <summary>
    ///     Probe <c>npm root -g</c>. Returns null on any failure (e.g. <c>npm.cmd</c> not on PATH).
    /// </summary>
    private string? TryGetNpmGlobalRoot() =>
        RunCapture(
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "npm.cmd" : "npm",
            "root -g");

    /// <summary>
    ///     Probe <c>node -p</c> for the global modules path. Reliable when <c>node</c> is on PATH
    ///     (which is required for legacy <c>cli.js</c> anyway), and importantly works even when
    ///     <c>npm.cmd</c> isn't visible to the host process.
    /// </summary>
    private string? TryGetNodeGlobalRoot() =>
        RunCapture(
            "node",
            "-p \"require('module').globalPaths.find(p => p.endsWith('node_modules')) || ''\"");

    /// <summary>
    ///     Discover plausible node install directories by inspecting <c>PATH</c> for entries
    ///     that contain a <c>node</c>/<c>node.exe</c> file. The same directory typically holds
    ///     <c>npm.cmd</c>, <c>claude.cmd</c>, and a sibling <c>node_modules</c>.
    /// </summary>
    private static IEnumerable<string> TryFindNodeInstallDirs()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
        {
            yield break;
        }

        var nodeBin = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "node.exe" : "node";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string fullPath;
            try
            {
                fullPath = Path.Combine(dir.Trim(), nodeBin);
            }
            catch
            {
                continue;
            }

            if (File.Exists(fullPath))
            {
                yield return Path.GetDirectoryName(fullPath)!;
            }
        }
    }

    /// <summary>
    ///     Run a child process with a tight timeout and return trimmed stdout, or null on any
    ///     failure (process not found, non-zero exit, timeout, missing/non-existent output).
    /// </summary>
    private string? RunCapture(string fileName, string arguments)
    {
        try
        {
            using var probe = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            if (!probe.Start())
            {
                return null;
            }

            if (!probe.WaitForExit(3000))
            {
                try { probe.Kill(true); } catch { /* best-effort */ }
                return null;
            }

            if (probe.ExitCode != 0)
            {
                return null;
            }

            var output = probe.StandardOutput.ReadToEnd().Trim();
            return string.IsNullOrEmpty(output) || !Directory.Exists(output) ? null : output;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "{File} {Args} probe failed", fileName, arguments);
            return null;
        }
    }

    private async IAsyncEnumerable<string> ReadStdoutLinesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        if (_stdoutReader == null)
        {
            yield break;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await _stdoutReader.ReadLineAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TaskCanceledException)
            {
                _logger?.LogDebug("stdout reading cancelled");
                yield break;
            }

            if (line == null)
            {
                _logger?.LogDebug("stdout stream ended");
                break;
            }

            yield return line;
        }
    }

    /// <summary>
    ///     Polling-based stderr monitor that handles ReadLineAsync not respecting cancellation.
    ///     Uses Task.WhenAny with timeout to allow periodic cancellation checks.
    /// </summary>
    private async Task MonitorStdErrWithPolling(CancellationToken cancellationToken)
    {
        if (_stderrReader == null)
        {
            return;
        }

        try
        {
            // Track pending read task to avoid concurrent ReadLineAsync calls
            // StreamReader doesn't support concurrent reads
            Task<string?>? pendingReadTask = null;

            while (!cancellationToken.IsCancellationRequested)
            {
                // Use Task.WhenAny with timeout to handle ReadLineAsync not respecting cancellation
                // This is a known .NET issue (dotnet/runtime#28583)
                // Reuse pending read task, or start a new one if none pending
                pendingReadTask ??= _stderrReader.ReadLineAsync(CancellationToken.None).AsTask();

                var completedTask = await Task.WhenAny(
                    pendingReadTask,
                    Task.Delay(500, cancellationToken) // 500ms polling interval
                );

                if (completedTask == pendingReadTask)
                {
                    var line = await pendingReadTask;
                    pendingReadTask = null; // Clear so next iteration starts fresh

                    if (line == null)
                    {
                        break; // Stream ended
                    }

                    _logger?.LogWarning("claude-agent-sdk stderr: {Line}", line);
                }
                // If timeout, loop continues - pendingReadTask stays set for reuse
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
        catch (ObjectDisposedException)
        {
            // Stream closed during shutdown
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error in stderr monitor");
        }
    }

    /// <summary>
    ///     Wait for the OneShot process to complete and clean up resources.
    ///     Called after SendMessagesAsync completes in OneShot mode to ensure
    ///     the process has terminated before returning control to the caller.
    /// </summary>
    private async Task WaitForOneShotCompletionAsync(CancellationToken cancellationToken)
    {
        if (_process == null || _options.Mode != ClaudeAgentSdkMode.OneShot)
        {
            return;
        }

        _logger?.LogDebug("OneShot mode: Waiting for process to terminate");

        // Cancel stderr monitoring
        _shutdownCts?.Cancel();

        // Wait for process to exit (with reasonable timeout)
        if (!_process.HasExited)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

            try
            {
                await _process.WaitForExitAsync(timeoutCts.Token);
                _logger?.LogDebug("OneShot process exited with code: {Code}", _process.ExitCode);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Timeout occurred, not external cancellation
                _logger?.LogWarning("OneShot process did not exit within timeout, force killing");
                _process.Kill(entireProcessTree: true);
            }
        }
        else
        {
            _logger?.LogDebug("OneShot process already exited with code: {Code}", _process.ExitCode);
        }

        // Wait for stderr monitor task
        if (_stderrMonitorTask != null)
        {
            try
            {
                await _stderrMonitorTask.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
            }
            catch (TimeoutException)
            {
                // Timeout waiting for stderr monitor - acceptable
            }
            catch (OperationCanceledException)
            {
                // Task was cancelled - acceptable
            }
        }

        // Clean up temp file
        if (_systemPromptTempFile != null && File.Exists(_systemPromptTempFile))
        {
            try
            {
                File.Delete(_systemPromptTempFile);
                _logger?.LogDebug("Deleted system prompt temp file: {File}", _systemPromptTempFile);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to delete temp file: {File}", _systemPromptTempFile);
            }

            _systemPromptTempFile = null;
        }

        // Reset all resource fields to allow clean restart
        _logger?.LogDebug(
            "WaitForOneShotCompletionAsync: Cleaning up resources. ProcessId: {Pid}, HasExited: {HasExited}",
            _process?.Id,
            _process?.HasExited);

        _shutdownCts?.Dispose();
        _shutdownCts = null;
        _stderrMonitorTask = null;
        _process?.Dispose();
        _process = null;
        _stdinWriter = null;
        _stdoutReader = null;
        _stderrReader = null;

        _logger?.LogDebug("WaitForOneShotCompletionAsync: Resources cleaned. _process=null, _shutdownCts=null");

        // Transition to Stopped state (allows restart)
        _ = Interlocked.Exchange(ref _state, 4);
        _logger?.LogInformation("OneShot mode: Process terminated, state: Stopped");
    }

    /// <summary>
    ///     Convert multiple IMessage instances to a single InputMessageWrapper for JSONL stdin format.
    ///     Combines all messages into one InputMessage with multiple content blocks (text + images).
    /// </summary>
    internal static InputMessageWrapper ConvertToInputMessage(IEnumerable<IMessage> messages)
    {
        var contentBlocks = new List<InputContentBlock>();

        foreach (var message in messages)
        {
            switch (message)
            {
                case TextMessage textMsg when !string.IsNullOrEmpty(textMsg.Text):
                    contentBlocks.Add(new InputTextContentBlock { Text = textMsg.Text });
                    break;

                case ImageMessage imageMsg:
                    // Convert BinaryData to base64 with proper media type
                    var imageBytes = imageMsg.ImageData.ToArray();
                    var base64Data = Convert.ToBase64String(imageBytes);

                    // Ensure MediaType is set - if null, detect from bytes
                    var mediaType = imageMsg.ImageData.MediaType;
                    if (string.IsNullOrEmpty(mediaType))
                    {
                        mediaType = DetectImageMimeTypeFromBytes(imageBytes);
                    }

                    contentBlocks.Add(
                        new InputImageContentBlock
                        {
                            Source = new ImageSource
                            {
                                Type = "base64",
                                MediaType = mediaType,
                                Data = base64Data,
                            },
                        }
                    );
                    break;

                case ICanGetText textProvider:
                    // Fallback for other message types that can provide text
                    var text = textProvider.GetText();
                    if (!string.IsNullOrEmpty(text))
                    {
                        contentBlocks.Add(new InputTextContentBlock { Text = text });
                    }

                    break;

                default:
                    // Skip unsupported message types (e.g., assistant messages in multi-turn history)
                    // This allows the caller to pass all messages without pre-filtering
                    break;
            }
        }

        return new InputMessageWrapper
        {
            Type = "user",
            Message = new InputMessage { Role = "user", Content = contentBlocks },
        };
    }

    /// <summary>
    ///     Detects image MIME type from byte signature (magic bytes).
    ///     Supports PNG, JPEG, GIF, and WebP formats.
    /// </summary>
    private static string DetectImageMimeTypeFromBytes(byte[] bytes)
    {
        if (bytes.Length >= 8)
        {
            // PNG: 89 50 4E 47
            if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            {
                return "image/png";
            }

            // JPEG: FF D8 FF
            if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            {
                return "image/jpeg";
            }

            // GIF: 47 49 46 38
            if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38)
            {
                return "image/gif";
            }

            // WebP: 52 49 46 46 ... 57 45 42 50
            if (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
                bytes.Length >= 12 && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
            {
                return "image/webp";
            }
        }

        // Default to image/png as a safe fallback (most providers accept PNG)
        return "image/png";
    }

    /// <summary>
    ///     Format a message for trace logging, including content preview.
    ///     Uses multiline format for readability.
    /// </summary>
    private static string FormatMessageForLog(IMessage msg, int maxContentLength = 80)
    {
        return msg switch
        {
            TextMessage text => $"\n  [Text] ({text.Text?.Length ?? 0} chars)\n    {TruncateForLog(text.Text, maxContentLength)}",
            ReasoningMessage reasoning => $"\n  [Thinking] ({reasoning.Reasoning?.Length ?? 0} chars)\n    {TruncateForLog(reasoning.Reasoning, maxContentLength)}",
            ToolCallMessage toolCall => $"\n  [ToolCall] {toolCall.FunctionName}\n    args={TruncateForLog(toolCall.FunctionArgs, 80)}",
            ToolCallResultMessage toolResult => $"\n  [ToolResult] id={toolResult.ToolCallId}\n    result={TruncateForLog(toolResult.Result, maxContentLength)}",
            ImageMessage image => $"\n  [Image]\n    mediaType={image.ImageData.MediaType}\n    size={image.ImageData.ToMemory().Length} bytes",
            UsageMessage usage => $"\n  [Usage] prompt={usage.Usage?.PromptTokens}, completion={usage.Usage?.CompletionTokens}, total={usage.Usage?.TotalTokens}, cacheRead={usage.Usage?.TotalCachedTokens}",
            _ => $"\n  [{msg.GetType().Name}]",
        };
    }

    /// <summary>
    ///     Truncate a string for logging, adding ellipsis if truncated.
    /// </summary>
    private static string TruncateForLog(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "(empty)";
        }

        // Collapse all whitespace and newlines to single spaces for preview
        var preview = text.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");

        // Trim leading/trailing whitespace
        preview = preview.Trim();

        return string.IsNullOrEmpty(preview)
            ? "(whitespace only)"
            : preview.Length <= maxLength
                ? preview
                : preview[..maxLength] + "...";
    }

    /// <summary>
    ///     Extract first and last text messages from a collection for logging.
    /// </summary>
    private static (string? First, string? Last) GetFirstLastTextMessages(
        IEnumerable<IMessage> messages,
        int maxLength = 100)
    {
        var textMessages = messages.OfType<TextMessage>().ToList();
        if (textMessages.Count == 0)
        {
            return (null, null);
        }

        var first = TruncateForLog(textMessages[0].Text, maxLength);
        var last = textMessages.Count > 1
            ? TruncateForLog(textMessages[^1].Text, maxLength)
            : null;
        return (first, last);
    }
}
