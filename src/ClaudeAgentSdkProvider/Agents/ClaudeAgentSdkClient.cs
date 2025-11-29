using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models.JsonlEvents;
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

    private Process? _process;
    private StreamReader? _stderrReader;
    private StreamWriter? _stdinWriter;
    private StreamReader? _stdoutReader;
    private string? _systemPromptTempFile;

    public ClaudeAgentSdkClient(ClaudeAgentSdkOptions options, ILogger<ClaudeAgentSdkClient>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        _parser = new JsonlStreamParser();
    }

    public bool IsRunning => _process != null && !_process.HasExited;
    public SessionInfo? CurrentSession { get; private set; }

    public async Task StartAsync(ClaudeAgentSdkRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (IsRunning)
        {
            throw new InvalidOperationException("Client is already running");
        }

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

        // 7. Start stderr monitor in background
        _ = Task.Run(() => MonitorStdErr(cancellationToken), cancellationToken);

        // 8. Create session info
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
    }

    public async IAsyncEnumerable<IMessage> SendMessagesAsync(
        IEnumerable<IMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (!IsRunning)
        {
            throw new InvalidOperationException("Client is not running. Call StartAsync first.");
        }

        // Extract the last user message (claude-agent-sdk maintains its own conversation history)
        // We only send the latest user input, ignoring previous messages
        var lastUserMessage = messages.LastOrDefault(m => m.Role == Role.User);

        if (lastUserMessage == null)
        {
            _logger?.LogWarning("No user message found in the message collection");
            yield break;
        }

        // Convert to JSONL input format and send to stdin
        if (_stdinWriter != null)
        {
            var inputWrapper = ConvertToInputMessage(lastUserMessage);
            var jsonLine = JsonSerializer.Serialize(inputWrapper, _jsonOptions);

            _logger?.LogDebug(
                "Sending JSONL message to claude-agent-sdk: {Message}",
                jsonLine.Length > 200 ? jsonLine[..200] + "..." : jsonLine
            );

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

        // Read and parse stdout for the response
        await foreach (var line in ReadStdoutLinesAsync(cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            _logger?.LogTrace("Received JSONL line: {Line}", line.Length > 200 ? line[..200] + "..." : line);

            var jsonlEvent = _parser.ParseLine(line);

            if (jsonlEvent is AssistantMessageEvent assistantEvent)
            {
                var eventMessages = JsonlStreamParser.ConvertToMessages(assistantEvent);
                foreach (var msg in eventMessages)
                {
                    yield return msg;
                }
            }
            // User messages contain tool results and other user inputs
            else if (jsonlEvent is UserMessageEvent userEvent)
            {
                var eventMessages = _parser.ConvertToMessages(userEvent);
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
                _logger?.LogInformation(
                    "Execution completed - Status: {Subtype}, Turns: {NumTurns}, Duration: {DurationMs}ms, Cost: ${TotalCostUsd:F4}",
                    resultEvent.Subtype,
                    resultEvent.NumTurns,
                    resultEvent.DurationMs,
                    resultEvent.TotalCostUsd
                );

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
                yield break;
            }
        }

        // In OneShot mode, when stdout ends, the process has exited
        if (_options.Mode == ClaudeAgentSdkMode.OneShot)
        {
            _logger?.LogInformation("OneShot mode: Process completed and exited");
        }
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

    private static string FindNodeJs()
    {
        var nodeExe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "node.exe" : "node";

        // Check PATH environment variable
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in pathDirs)
        {
            var nodePath = Path.Combine(dir, nodeExe);
            if (File.Exists(nodePath))
            {
                return nodePath;
            }
        }

        // Check common installation locations on Windows
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var possiblePaths = new[]
            {
                Path.Combine(programFiles, "nodejs", "node.exe"),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "nodejs",
                    "node.exe"
                ),
            };

            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        throw new FileNotFoundException(
            "Node.js not found. Please install Node.js or specify NodeJsPath in ClaudeAgentSdkOptions."
        );
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

    private async Task MonitorStdErr(CancellationToken cancellationToken)
    {
        if (_stderrReader == null)
        {
            return;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await _stderrReader.ReadLineAsync(cancellationToken);
                if (line == null)
                {
                    break;
                }

                // Log stderr output as warnings
                _logger?.LogWarning("claude-agent-sdk stderr: {Line}", line);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not TaskCanceledException)
        {
            _logger?.LogError(ex, "Error reading stderr");
        }
    }

    /// <summary>
    ///     Convert IMessage to InputMessageWrapper for JSONL stdin format
    ///     Supports both TextMessage and ImageMessage with base64 encoding
    /// </summary>
    private static InputMessageWrapper ConvertToInputMessage(IMessage message)
    {
        var contentBlocks = new List<InputContentBlock>();

        switch (message)
        {
            case TextMessage textMsg when !string.IsNullOrEmpty(textMsg.Text):
                contentBlocks.Add(new InputTextContentBlock { Text = textMsg.Text });
                break;

            case ImageMessage imageMsg:
                // Convert BinaryData to base64 with proper media type
                var imageBytes = imageMsg.ImageData.ToArray();
                var base64Data = Convert.ToBase64String(imageBytes);
                var mediaType = imageMsg.ImageData.MediaType ?? "image/jpeg";

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
                throw new NotSupportedException(
                    $"Message type {message.GetType().Name} is not supported for claude-agent-sdk input"
                );
        }

        return new InputMessageWrapper
        {
            Type = "user",
            Message = new InputMessage { Role = "user", Content = contentBlocks },
        };
    }
}
