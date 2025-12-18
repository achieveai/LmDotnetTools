using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Configuration;
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
        _parser = new JsonlStreamParser();
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

            // 1. Locate Node.js
            var nodePath = _options.NodeJsPath ?? FindNodeJs();
            _logger?.LogDebug("Using Node.js at: {NodePath}", nodePath);

            // 2. Locate CLI
            var cliPath = _options.CliPath ?? FindClaudeAgentSdkCli();
            _logger?.LogDebug("Using claude-agent-sdk CLI at: {CliPath}", cliPath);

            // 3. Create system prompt temp file if provided
            if (!string.IsNullOrEmpty(request.SystemPrompt))
            {
                _systemPromptTempFile = Path.GetTempFileName();
                await File.WriteAllTextAsync(_systemPromptTempFile, request.SystemPrompt, cancellationToken);
                _logger?.LogDebug("Created system prompt temp file: {File}", _systemPromptTempFile);
            }

            // 4. Build CLI arguments
            var args = BuildCliArguments(request);
            _logger?.LogDebug("CLI arguments: node \"{CliPath}\" {Args}", cliPath, args);

            // 5. Configure process
            var startInfo = new ProcessStartInfo
            {
                FileName = nodePath,
                Arguments = $"\"{cliPath}\" {args}",
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
            _stderrMonitorTask = Task.Run(() => MonitorStdErrWithPolling(_shutdownCts.Token));

            // 8. Start keepalive task (Interactive mode only)
            if (_options.Mode == ClaudeAgentSdkMode.Interactive)
            {
                _keepaliveCts = new CancellationTokenSource();
                _keepaliveTask = Task.Run(() => RunKeepaliveAsync(_keepaliveCts.Token));
            }

            // 9. Create session info
            CurrentSession = new SessionInfo
            {
                SessionId = request.SessionId ?? Guid.NewGuid().ToString(),
                CreatedAt = DateTime.UtcNow,
                ProjectRoot = _options.ProjectRoot,
            };

            _logger?.LogInformation(
                "claude-agent-sdk CLI started successfully. SessionId: {SessionId}, PID: {ProcessId}",
                CurrentSession.SessionId,
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
                "Submitting to LLM - MessageCount: {Count}, FirstText: {First}, LastText: {Last}",
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

        bool isWorking = false;

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
                var eventMessages = JsonlStreamParser.ConvertToMessages(assistantEvent).ToList();
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
            // System init events contain session info and available tools
            else if (jsonlEvent is SystemInitEvent systemInitEvent)
            {
                _logger?.LogInformation(
                    "System initialized - SessionId: {SessionId}, Model: {Model}, Tools: {ToolCount}, MCP Servers: {McpServers}",
                    systemInitEvent.SessionId,
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
                _logger?.LogTrace("Received ResultEvent: IsError={IsError}, NumTurns={NumTurns}, SessionId={SessionId}",
                    resultEvent.IsError, resultEvent.NumTurns, resultEvent.SessionId);
                if (resultEvent.IsError)
                {
                    if (isWorking && _stdinWriter != null)
                    {
                        // Send "retry...." message to the agent
                        _logger?.LogWarning(
                            "Error encountered during agent execution: {Error}. Sending retry message to agent.",
                            resultEvent.Result ?? "Unknown error"
                        );

                        isWorking = false;
                        var jsonLine = JsonSerializer.Serialize(
                            new InputMessageWrapper {
                                Type = "user",
                                Message = new InputMessage {
                                    Role = "user",
                                    Content = [new InputTextContentBlock {
                                        Text = "retry...."
                                    }]
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
                    "{Mode} mode: ResultEvent received, ending current turn",
                    _options.Mode
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
                    var eventMessages = JsonlStreamParser.ConvertToMessages(assistantEvent).ToList();
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
                else if (jsonlEvent is SystemInitEvent systemInitEvent)
                {
                    _logger?.LogInformation(
                        "System initialized - SessionId: {SessionId}, Model: {Model}, Tools: {ToolCount}",
                        systemInitEvent.SessionId,
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
                    _logger?.LogTrace("Received ResultEvent: IsError={IsError}, NumTurns={NumTurns}, SessionId={SessionId}",
                        resultEvent.IsError, resultEvent.NumTurns, resultEvent.SessionId);

                    // Yield a marker message so caller knows turn is complete
                    yield return new ResultEventMessage
                    {
                        IsError = resultEvent.IsError,
                        Result = resultEvent.Result,
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
                "Submitting to LLM (Interactive) - MessageCount: {Count}, FirstText: {First}, LastText: {Last}",
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
        _logger?.LogTrace("Keepalive: skipped (empty lines crash CLI)");
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

        _logger?.LogDebug("Keepalive task started with interval: {Interval}", _options.KeepAliveInterval);

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

        _logger?.LogDebug("Keepalive task stopped");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _logger?.LogInformation("Disposing claude-agent-sdk client");

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
                    await _stderrMonitorTask.WaitAsync(TimeSpan.FromSeconds(2));
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
                    await _keepaliveTask.WaitAsync(TimeSpan.FromSeconds(2));
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

    private string BuildCliArguments(ClaudeAgentSdkRequest request)
    {
        var args = new List<string>
        {
            $"--output-format {request.OutputFormat}",
            $"--input-format {request.InputFormat}",
            $"--model {request.ModelId}",
            $"--max-turns {request.MaxTurns}",
            $"--max-thinking-tokens {request.MaxThinkingTokens}",
            $"--permission-mode {request.PermissionMode}",
            $"--setting-sources \"{request.SettingSources}\"",
        };

        if (request.Verbose)
        {
            args.Add("--verbose");
        }

        if (!string.IsNullOrEmpty(request.AllowedTools))
        {
            args.Add($"--allowedTools {request.AllowedTools}");
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

        return string.Join(" ", args);
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

    private static string FindClaudeAgentSdkCli()
    {
        // Try to find in global npm modules
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Windows: AppData\Roaming\npm\node_modules
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var npmGlobalPath = Path.Combine(
                appData,
                "npm",
                "node_modules",
                "@anthropic-ai",
                "claude-agent-sdk",
                "cli.js"
            );
            if (File.Exists(npmGlobalPath))
            {
                return npmGlobalPath;
            }

            // Also check ProgramFiles for system-wide installations
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var systemPath = Path.Combine(
                programFiles,
                "nodejs",
                "node_modules",
                "@anthropic-ai",
                "claude-agent-sdk",
                "cli.js"
            );
            if (File.Exists(systemPath))
            {
                return systemPath;
            }
        }
        else
        {
            // Unix-like: /usr/local/lib/node_modules
            var globalPath = "/usr/local/lib/node_modules/@anthropic-ai/claude-agent-sdk/cli.js";
            if (File.Exists(globalPath))
            {
                return globalPath;
            }
        }

        throw new FileNotFoundException(
            "claude-agent-sdk CLI not found. Please install: npm install -g @anthropic-ai/claude-agent-sdk"
        );
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

        if (string.IsNullOrEmpty(preview))
        {
            return "(whitespace only)";
        }

        if (preview.Length <= maxLength)
        {
            return preview;
        }

        return preview[..maxLength] + "...";
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
