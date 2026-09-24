using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.Sandbox.Wire;

namespace AchieveAi.LmDotnetTools.Sandbox;

public sealed partial class SandboxClient
{
    private const int MaxStreamStdinBytes = 1024 * 1024;
    private const int MaxStreamFrameBytes = 128 * 1024;
    private const long MaxStreamOutputBytes = 256L * 1024 * 1024;
    private const int StreamExecutionTimeoutSeconds = 30;

    /// <summary>Probes the owned session's streaming protocol without running workspace code.</summary>
    public async Task<bool> SupportsStreamingAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        using var response = await SendRestAsync(
                HttpMethod.Get,
                $"api/v1/sandboxes/{Uri.EscapeDataString(sessionId)}/operations/capabilities",
                body: null,
                sessionId,
                ct
            )
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }
        try
        {
            using var document = await JsonDocument
                .ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct)
                .ConfigureAwait(false);
            var root = document.RootElement;
            return root.TryGetProperty("streaming", out var streaming)
                && streaming.ValueKind == JsonValueKind.True
                && root.TryGetProperty("protocol_version", out var version)
                && version.ValueKind == JsonValueKind.Number
                && version.TryGetInt32(out var protocol)
                && protocol == 4;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Runs one native command and passes each stdout/stderr byte chunk to an awaited callback as soon
    /// as it arrives. The callback supplies backpressure; no complete output is buffered in this SDK.
    /// Cancelling the token closes the HTTP response, which asks the gateway to terminate the process.
    /// </summary>
    public async Task<SandboxStreamResult> ExecuteStreamingAsync(
        string sessionId,
        SandboxCommand command,
        Func<SandboxOutputChunk, CancellationToken, ValueTask> onOutput,
        ReadOnlyMemory<byte> stdin = default,
        IReadOnlyDictionary<string, string>? environment = null,
        long maxOutputBytes = 8L * 1024 * 1024,
        CancellationToken ct = default
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(onOutput);
        if (command.OperationId is not null)
        {
            throw new ArgumentException("Streaming commands do not support operation IDs or replay.", nameof(command));
        }
        if (stdin.Length > MaxStreamStdinBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(stdin), "Streaming command input exceeds 1 MiB.");
        }
        if (maxOutputBytes is < 1 or > MaxStreamOutputBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxOutputBytes),
                "Streaming command output cap must be between 1 byte and 256 MiB."
            );
        }

        var mountId = await ResolveWorkspaceMountIdAsync(sessionId, ct).ConfigureAwait(false);
        var relativeUri = $"api/v1/sandboxes/{Uri.EscapeDataString(sessionId)}/operations/stream";
        var requestBody = new
        {
            executable = command.Arguments[0],
            args = command.Arguments.Skip(1).ToArray(),
            env = environment is null ? null : new Dictionary<string, string>(environment),
            cwd = new OperationCwdDto(mountId, command.NormalizedWorkingDirectory),
            timeout_secs = StreamExecutionTimeoutSeconds,
            max_output_bytes = maxOutputBytes,
            stdin_base64 = Convert.ToBase64String(stdin.Span),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, ResolveRequestUri(relativeUri))
        {
            Content = JsonContent.Create(requestBody, options: SandboxJson.RestOptions),
        };
        request.Headers.Accept.ParseAdd("application/x-ndjson");
        StampAuthHeaders(request);
        _ = request.Headers.TryAddWithoutValidation(SessionIdHeader, sessionId);

        // Allow the gateway's full execution deadline plus transport grace. A normal per-request
        // transport timeout could cut off an otherwise valid long-running, quiet process.
        var executionLimit = TimeSpan.FromSeconds(StreamExecutionTimeoutSeconds);
        var streamDeadline =
            executionLimit <= TimeSpan.MaxValue - _options.TransportTimeout
                ? executionLimit + _options.TransportTimeout
                : TimeSpan.MaxValue;
        using var budgetCts = new CancellationTokenSource(streamDeadline, TransportClock);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, budgetCts.Token);
        var linkedToken = linkedCts.Token;
        try
        {
            using var response = await Transport
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw await MapDirectErrorAsync(response, "starting a streaming operation", sessionId, ct, linkedToken)
                    .ConfigureAwait(false);
            }

            await using var responseStream = await response
                .Content.ReadAsStreamAsync(linkedToken)
                .ConfigureAwait(false);
            long stdoutBytes = 0;
            long stderrBytes = 0;
            SandboxStreamResult? result = null;
            await foreach (var line in ReadStreamFramesAsync(responseStream, linkedToken).ConfigureAwait(false))
            {
                var frame = ParseStreamFrame(line);
                if (result is not null)
                {
                    throw StreamProtocolError("received data after the terminal result");
                }
                if (frame.Type == "result")
                {
                    result = ResolveStreamResult(frame.Result!, stdoutBytes, stderrBytes);
                    continue;
                }

                var outputStream = frame.Type == "stdout" ? SandboxOutputStream.Stdout : SandboxOutputStream.Stderr;
                var data = frame.Data!;
                if (data.Length > maxOutputBytes - stdoutBytes - stderrBytes)
                {
                    throw StreamProtocolError("exceeded the requested output cap");
                }
                if (outputStream == SandboxOutputStream.Stdout)
                {
                    stdoutBytes += data.Length;
                }
                else
                {
                    stderrBytes += data.Length;
                }
                await onOutput(new SandboxOutputChunk(outputStream, data), linkedToken).ConfigureAwait(false);
            }

            return result ?? throw StreamProtocolError("ended without a terminal result");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SandboxException(
                SandboxErrorKind.TransportTimeout,
                "Sandbox streaming request exceeded its transport deadline."
            );
        }
        catch (HttpRequestException ex)
        {
            throw new SandboxException(
                SandboxErrorKind.TransportTimeout,
                $"Could not reach the sandbox gateway at '{_options.ServerAddress}'.",
                innerException: ex
            );
        }
    }

    private static async IAsyncEnumerable<byte[]> ReadStreamFramesAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        var buffer = new byte[8192];
        using var line = new MemoryStream();
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            var start = 0;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != (byte)'\n')
                {
                    continue;
                }
                Append(buffer.AsSpan(start, i - start));
                yield return line.ToArray();
                line.SetLength(0);
                start = i + 1;
            }
            Append(buffer.AsSpan(start, read - start));
        }

        if (line.Length != 0)
        {
            throw StreamProtocolError("ended with an incomplete frame");
        }

        void Append(ReadOnlySpan<byte> bytes)
        {
            if (line.Length + bytes.Length > MaxStreamFrameBytes)
            {
                throw StreamProtocolError("sent an oversized frame");
            }
            line.Write(bytes);
        }
    }

    private static StreamFrame ParseStreamFrame(byte[] line)
    {
        try
        {
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            if (
                root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String
            )
            {
                throw StreamProtocolError("sent an invalid frame");
            }
            var type = typeElement.GetString();
            if (type is "stdout" or "stderr")
            {
                if (
                    !root.TryGetProperty("data_base64", out var dataElement)
                    || dataElement.ValueKind != JsonValueKind.String
                )
                {
                    throw StreamProtocolError("sent an output frame without bytes");
                }
                return new StreamFrame(type, Convert.FromBase64String(dataElement.GetString()!), null);
            }
            if (type == "result")
            {
                if (
                    !root.TryGetProperty("result", out var resultElement)
                    || resultElement.ValueKind != JsonValueKind.Object
                )
                {
                    throw StreamProtocolError("sent a result frame without a result");
                }
                var result =
                    JsonSerializer.Deserialize<StreamTerminalDto>(resultElement.GetRawText(), SandboxJson.RestOptions)
                    ?? throw StreamProtocolError("sent an empty result");
                return new StreamFrame(type, null, result);
            }
            throw StreamProtocolError("sent an unknown frame type");
        }
        catch (JsonException)
        {
            throw StreamProtocolError("sent malformed JSON");
        }
        catch (FormatException)
        {
            throw StreamProtocolError("sent malformed base64 data");
        }
    }

    private static SandboxStreamResult ResolveStreamResult(StreamTerminalDto result, long stdoutBytes, long stderrBytes)
    {
        if (result.ProtocolVersion != 4 || result.StdoutBytes != stdoutBytes || result.StderrBytes != stderrBytes)
        {
            throw StreamProtocolError("reported inconsistent output totals or protocol version");
        }
        return result.Outcome switch
        {
            "succeeded" or "failed" when result.ExitCode is { } exitCode => new SandboxStreamResult(
                exitCode,
                stdoutBytes,
                stderrBytes
            ),
            "timed_out" => throw new SandboxException(
                SandboxErrorKind.ExecutionTimeout,
                "The sandbox gateway's execution timeout elapsed before the streaming command completed."
            ),
            "output_limit_exceeded" => throw new SandboxException(
                SandboxErrorKind.OutputLimitExceeded,
                "The streaming command exceeded its output cap."
            ),
            "internal_failure" or "cancelled" => throw new SandboxException(
                SandboxErrorKind.OperationFailed,
                "The sandbox gateway did not complete the streaming command."
            ),
            _ => throw StreamProtocolError("reported an unknown outcome or no exit code"),
        };
    }

    private static SandboxException StreamProtocolError(string detail) =>
        new(SandboxErrorKind.Protocol, $"Sandbox gateway streaming response {detail}.");

    private sealed record StreamFrame(string Type, byte[]? Data, StreamTerminalDto? Result);

    private sealed record StreamTerminalDto(
        int? ProtocolVersion,
        string? Outcome,
        int? ExitCode,
        long? StdoutBytes,
        long? StderrBytes
    );
}
