using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.Sandbox.Command;

namespace AchieveAi.LmDotnetTools.Sandbox;

public sealed partial class SandboxClient
{
    /// <summary>The gateway tool name for POSIX command execution (see the pinned gateway's <c>execute_tool</c> dispatch).</summary>
    private const string BashToolName = "Bash";

    /// <summary>Bounded number of idempotent probes a single recovery/poll pass makes before giving up.</summary>
    private const int CommandPollAttempts = 20;

    private static readonly TimeSpan S_commandPollDelay = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// Runs a non-interactive command in a gateway Bash/POSIX-capable sandbox and returns its exact
    /// captured output, recovering transparently from an ambiguous lost response. Exactly one
    /// side-effecting Bash submission is ever made for a given operation; everything else (state
    /// probes, chunked output reads, cleanup) is an idempotent read, so the command is never re-run by
    /// the SDK.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Outcomes.</b> A gateway execution-timeout surfaces as
    /// <see cref="SandboxErrorKind.ExecutionTimeout"/>; a client-side transport timeout that leaves the
    /// result unconfirmed surfaces as <see cref="SandboxErrorKind.TransportTimeout"/> carrying the
    /// recoverable <see cref="SandboxException.OperationId"/> (artifacts are retained for a later
    /// same-id call); a caller cancellation surfaces as a plain <see cref="OperationCanceledException"/>.
    /// Neither timeout claims the remote process tree was terminated.
    /// </para>
    /// <para>
    /// <b>At-least-once.</b> The SDK submits once and never resubmits, but the gateway may rematerialize
    /// a lost container and retry the underlying invocation once, so a non-idempotent command can run
    /// more than once even though this returns a single result.
    /// </para>
    /// </remarks>
    /// <exception cref="SandboxException">
    /// Execution timeout, transport timeout, a digest mismatch on a reused operation id
    /// (<see cref="SandboxErrorKind.Integrity"/>), a failed output integrity check, or a malformed
    /// gateway response (<see cref="SandboxErrorKind.Protocol"/>).
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task<SandboxCommandResult> ExecuteAsync(
        string sessionId,
        SandboxCommand command,
        CancellationToken ct = default
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(command);

        var operationId = CommandOperation.ResolveOperationId(command.OperationId);
        var executionTimeoutSeconds = GatewayExecutionTimeoutSeconds();
        var operationDirectory = CommandOperation.OperationDirectoryName(sessionId, operationId);
        var digest = CommandOperation.CanonicalDigest(
            sessionId,
            command.Arguments,
            command.NormalizedWorkingDirectory,
            executionTimeoutSeconds
        );
        var quotedArgv = PosixArgv.Join(command.Arguments);

        // Pre-probe: recover a retained result, or reject a digest mismatch, WITHOUT submitting. Only a
        // completed manifest short-circuits here; a PENDING (a peer holds the claim) or absent state —
        // and any transport failure probing — falls through to the single RUN, whose atomic claim
        // elects one submitter and takes over a claim whose lease has expired. That keeps a crashed
        // submitter from blocking the operation until stale cleanup, while a live peer still makes the
        // wrapper report PENDING so this caller polls instead of re-running.
        ProbeResult preProbe;
        try
        {
            preProbe = await ProbeAsync(sessionId, operationDirectory, operationId, ct).ConfigureAwait(false);
        }
        catch (SandboxException ex) when (ex.Kind == SandboxErrorKind.TransportTimeout)
        {
            preProbe = ProbeResult.NotPresent;
        }

        if (preProbe.State == ProbeCompletionState.Completed)
        {
            return await AssembleAndCleanupAsync(
                    sessionId,
                    operationDirectory,
                    operationId,
                    digest,
                    preProbe.Manifest!,
                    ct
                )
                .ConfigureAwait(false);
        }

        // The single side-effecting submission.
        var runScript = CommandScripts.BuildRun(
            operationDirectory,
            digest,
            quotedArgv,
            command.NormalizedWorkingDirectory,
            executionTimeoutSeconds
        );
        JsonElement runResult;
        try
        {
            runResult = await SubmitBashAsync(sessionId, runScript, ct).ConfigureAwait(false);
        }
        catch (SandboxException ex) when (ex.Kind == SandboxErrorKind.TransportTimeout)
        {
            // Ambiguous lost response: never resubmit. Recover from the persisted manifest if the
            // command actually ran; otherwise surface a recoverable transport timeout and retain
            // artifacts for a later same-id call.
            var recovered =
                await TryPollManifestAsync(sessionId, operationDirectory, operationId, ct).ConfigureAwait(false)
                ?? throw new SandboxException(
                    SandboxErrorKind.TransportTimeout,
                    ex.Message,
                    ex.StatusCode,
                    ex,
                    operationId
                );

            return await AssembleAndCleanupAsync(sessionId, operationDirectory, operationId, digest, recovered, ct)
                .ConfigureAwait(false);
        }

        var (text, isError) = ExtractBashResult(runResult, "command execution");
        if (isError)
        {
            throw ClassifyRunError(text, operationId);
        }

        var sentinel = ParseProbeText(text, operationId);
        var manifest = sentinel.State switch
        {
            ProbeCompletionState.Completed => sentinel.Manifest!,
            ProbeCompletionState.Pending => await PollManifestAsync(sessionId, operationDirectory, operationId, ct)
                .ConfigureAwait(false),
            _ => throw new SandboxException(
                SandboxErrorKind.Protocol,
                "Sandbox command wrapper reported no manifest after running.",
                operationId: operationId
            ),
        };

        return await AssembleAndCleanupAsync(sessionId, operationDirectory, operationId, digest, manifest, ct)
            .ConfigureAwait(false);
    }

    /// <summary>The gateway execution-timeout, in whole seconds (at least 1), used both as the Bash timeout and in the canonical digest.</summary>
    private long GatewayExecutionTimeoutSeconds() =>
        Math.Max(1, (long)Math.Ceiling(_options.ExecutionTimeout.TotalSeconds));

    /// <summary>Submits one Bash tool call carrying <paramref name="script"/> and the gateway execution timeout.</summary>
    private Task<JsonElement> SubmitBashAsync(string sessionId, string script, CancellationToken ct)
    {
        var arguments = new { command = script, timeout = GatewayExecutionTimeoutSeconds() };
        return SendMcpToolCallAsync(sessionId, BashToolName, arguments, ct);
    }

    /// <summary>Issues one idempotent PROBE and parses the resulting state.</summary>
    private async Task<ProbeResult> ProbeAsync(
        string sessionId,
        string operationDirectory,
        string operationId,
        CancellationToken ct
    )
    {
        var result = await SubmitBashAsync(sessionId, CommandScripts.BuildProbe(operationDirectory), ct)
            .ConfigureAwait(false);
        var (text, isError) = ExtractBashResult(result, "command probe");
        if (isError)
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                "Sandbox command probe returned an error.",
                operationId: operationId
            );
        }

        return ParseProbeText(text, operationId);
    }

    /// <summary>Polls for a completed manifest, throwing a recoverable transport timeout if it never appears within the bound.</summary>
    private async Task<CommandManifest> PollManifestAsync(
        string sessionId,
        string operationDirectory,
        string operationId,
        CancellationToken ct
    )
    {
        var manifest = await TryPollManifestAsync(sessionId, operationDirectory, operationId, ct).ConfigureAwait(false);
        return manifest
            ?? throw new SandboxException(
                SandboxErrorKind.TransportTimeout,
                "Sandbox command did not report completion within the recovery window.",
                operationId: operationId
            );
    }

    /// <summary>
    /// Bounded idempotent poll for a completed manifest. Returns <c>null</c> if the bound is exhausted
    /// without one. A transient transport failure on an individual probe is retried (not fatal) so a
    /// recovery poll survives a flaky gateway; caller cancellation still propagates.
    /// </summary>
    private async Task<CommandManifest?> TryPollManifestAsync(
        string sessionId,
        string operationDirectory,
        string operationId,
        CancellationToken ct
    )
    {
        for (var attempt = 0; attempt < CommandPollAttempts; attempt++)
        {
            try
            {
                var probe = await ProbeAsync(sessionId, operationDirectory, operationId, ct).ConfigureAwait(false);
                if (probe.State == ProbeCompletionState.Completed)
                {
                    return probe.Manifest;
                }
            }
            catch (SandboxException ex) when (ex.Kind == SandboxErrorKind.TransportTimeout)
            {
                // Retry a transient probe failure within the bound.
            }

            await Task.Delay(S_commandPollDelay, ct).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>
    /// Verifies the manifest digest, materializes both streams (verifying length + SHA-256), and on
    /// success deletes the operation's artifacts immediately plus runs a bounded stale sweep. A digest
    /// or integrity mismatch throws BEFORE any deletion, so the artifacts are retained for diagnosis or
    /// a later attempt.
    /// </summary>
    private async Task<SandboxCommandResult> AssembleAndCleanupAsync(
        string sessionId,
        string operationDirectory,
        string operationId,
        string expectedDigest,
        CommandManifest manifest,
        CancellationToken ct
    )
    {
        if (!string.Equals(manifest.Digest, expectedDigest, StringComparison.Ordinal))
        {
            throw new SandboxException(
                SandboxErrorKind.Integrity,
                "The operation id was reused with a different command (canonical digest mismatch).",
                operationId: operationId
            );
        }

        var standardOutput = await MaterializeStreamAsync(
                sessionId,
                operationDirectory,
                "stdout",
                manifest.Stdout,
                operationId,
                ct
            )
            .ConfigureAwait(false);
        var standardError = await MaterializeStreamAsync(
                sessionId,
                operationDirectory,
                "stderr",
                manifest.Stderr,
                operationId,
                ct
            )
            .ConfigureAwait(false);

        var result = new SandboxCommandResult
        {
            ExitCode = manifest.ExitCode,
            StandardOutput = standardOutput,
            StandardError = standardError,
            OperationId = operationId,
        };

        await BestEffortCleanupAsync(sessionId, operationDirectory, ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>Reassembles one stream from its inline copy or chunked reads, then verifies it against the manifest.</summary>
    private async Task<string> MaterializeStreamAsync(
        string sessionId,
        string operationDirectory,
        string streamName,
        CommandStreamManifest streamManifest,
        string operationId,
        CancellationToken ct
    )
    {
        var bytes = streamManifest.Inline is { } inline
            ? DecodeBase64OrThrow(inline, operationId)
            : await ReadStreamChunkedAsync(
                    sessionId,
                    operationDirectory,
                    streamName,
                    streamManifest.Length,
                    operationId,
                    ct
                )
                .ConfigureAwait(false);

        if (bytes.Length != streamManifest.Length)
        {
            throw new SandboxException(
                SandboxErrorKind.Integrity,
                $"Sandbox command {streamName} length mismatch (expected {streamManifest.Length}, got {bytes.Length}).",
                operationId: operationId
            );
        }

        if (!string.Equals(Sha256Hex(bytes), streamManifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new SandboxException(
                SandboxErrorKind.Integrity,
                $"Sandbox command {streamName} digest mismatch.",
                operationId: operationId
            );
        }

        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Reads a stream back in bounded base64 chunks that each stay under the gateway's <c>exec</c>
    /// truncation limits, resuming from the last verified byte offset. The gateway's unstable
    /// <c>output_*.txt</c> path is never consulted; every byte comes from the stable, command-scoped
    /// artifact file.
    /// </summary>
    private async Task<byte[]> ReadStreamChunkedAsync(
        string sessionId,
        string operationDirectory,
        string streamName,
        long totalLength,
        string operationId,
        CancellationToken ct
    )
    {
        using var buffer = new MemoryStream();
        long offset = 0;
        while (offset < totalLength)
        {
            var length = (int)Math.Min(CommandArtifactLayout.ReadChunkBytes, totalLength - offset);
            var script = CommandScripts.BuildRead(operationDirectory, streamName, offset, length);
            var result = await SubmitBashAsync(sessionId, script, ct).ConfigureAwait(false);
            var (text, isError) = ExtractBashResult(result, "command output read");
            if (isError)
            {
                throw new SandboxException(
                    SandboxErrorKind.Protocol,
                    "Sandbox command output read returned an error.",
                    operationId: operationId
                );
            }

            var chunk = DecodeBase64OrThrow(text, operationId);
            if (chunk.Length != length)
            {
                // A short chunk means the file no longer matches the manifest — fail rather than
                // silently return partial or mixed content.
                throw new SandboxException(
                    SandboxErrorKind.Integrity,
                    $"Sandbox command {streamName} chunk at offset {offset} returned {chunk.Length} of {length} expected bytes.",
                    operationId: operationId
                );
            }

            buffer.Write(chunk, 0, chunk.Length);
            offset += length;
        }

        return buffer.ToArray();
    }

    /// <summary>Deletes the verified operation's artifacts, then runs a bounded stale sweep. Both are best-effort and never fail the command.</summary>
    private async Task BestEffortCleanupAsync(string sessionId, string operationDirectory, CancellationToken ct)
    {
        try
        {
            _ = await SubmitBashAsync(sessionId, CommandScripts.BuildClean(operationDirectory), ct)
                .ConfigureAwait(false);
        }
        catch (SandboxException)
        {
            // Best-effort: the operation already succeeded; a failed cleanup must not fail it.
        }
        catch (OperationCanceledException)
        {
            // The verified result is already assembled; cancellation simply skips maintenance.
        }

        await StaleCleanupAsync(sessionId, ct).ConfigureAwait(false);
    }

    /// <summary>Bounded, session-scoped stale-artifact sweep: list at most a fixed number of directories, delete those both expired and 24h+ old.</summary>
    private async Task StaleCleanupAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            var listing = await SubmitBashAsync(
                    sessionId,
                    CommandScripts.BuildGc(CommandArtifactLayout.StaleScanLimit),
                    ct
                )
                .ConfigureAwait(false);
            var (text, isError) = ExtractBashResult(listing, "stale cleanup listing");
            if (isError)
            {
                return;
            }

            var entries = CommandSentinel.ParseGcListing(text);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var name in CommandStaleCleanup.SelectStale(entries, now))
            {
                _ = await SubmitBashAsync(sessionId, CommandScripts.BuildClean(name), ct).ConfigureAwait(false);
            }
        }
        catch (SandboxException)
        {
            // Best-effort maintenance.
        }
        catch (OperationCanceledException)
        {
            // Best-effort maintenance.
        }
    }

    /// <summary>Classifies an <c>isError</c> RUN result. The gateway text is never copied into the exception (it may carry command output/secrets).</summary>
    private static SandboxException ClassifyRunError(string text, string operationId)
    {
        if (text.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return new SandboxException(
                SandboxErrorKind.ExecutionTimeout,
                "The sandbox gateway's execution timeout elapsed before the command completed.",
                operationId: operationId
            );
        }

        return new SandboxException(
            SandboxErrorKind.Protocol,
            "The sandbox gateway reported an error running the command wrapper.",
            operationId: operationId
        );
    }

    private ProbeResult ParseProbeText(string text, string operationId)
    {
        var (kind, payload) = CommandSentinel.Parse(text);
        return kind switch
        {
            CommandSentinel.KindManifest => new ProbeResult(
                ProbeCompletionState.Completed,
                DecodeManifest(payload, operationId)
            ),
            CommandSentinel.KindPending => new ProbeResult(ProbeCompletionState.Pending, null),
            CommandSentinel.KindNone => ProbeResult.NotPresent,
            _ => throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox command wrapper returned an unknown status '{kind}'.",
                operationId: operationId
            ),
        };
    }

    private static CommandManifest DecodeManifest(string? base64Payload, string operationId)
    {
        if (base64Payload is null)
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                "Sandbox command manifest payload was empty.",
                operationId: operationId
            );
        }

        try
        {
            var json = Convert.FromBase64String(base64Payload);
            var manifest = JsonSerializer.Deserialize<CommandManifest>(json, CommandManifest.Json);
            return manifest
                ?? throw new SandboxException(
                    SandboxErrorKind.Protocol,
                    "Sandbox command manifest deserialized to null.",
                    operationId: operationId
                );
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                "Sandbox command manifest was malformed.",
                operationId: operationId
            );
        }
    }

    private static byte[] DecodeBase64OrThrow(string base64, string operationId)
    {
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                "Sandbox command output was not valid base64.",
                operationId: operationId
            );
        }
    }

    /// <summary>Extracts the text content and error flag from a Bash tool result, mapping any unexpected shape to <see cref="SandboxErrorKind.Protocol"/>.</summary>
    private static (string Text, bool IsError) ExtractBashResult(JsonElement result, string phase)
    {
        if (result.ValueKind != JsonValueKind.Object)
        {
            throw MalformedBashResult(phase);
        }

        var isError =
            result.TryGetProperty("isError", out var errorElement) && errorElement.ValueKind == JsonValueKind.True;

        if (
            !result.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array
            || content.GetArrayLength() == 0
        )
        {
            throw MalformedBashResult(phase);
        }

        var first = content[0];
        if (
            first.ValueKind != JsonValueKind.Object
            || !first.TryGetProperty("text", out var textElement)
            || textElement.ValueKind != JsonValueKind.String
        )
        {
            throw MalformedBashResult(phase);
        }

        return (textElement.GetString() ?? string.Empty, isError);
    }

    private static SandboxException MalformedBashResult(string phase) =>
        new(SandboxErrorKind.Protocol, $"Sandbox gateway returned a malformed Bash result for {phase}.");

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>Whether an operation's artifacts exist and, if completed, the parsed manifest.</summary>
    private enum ProbeCompletionState
    {
        NotPresent,
        Pending,
        Completed,
    }

    private readonly record struct ProbeResult(ProbeCompletionState State, CommandManifest? Manifest)
    {
        public static readonly ProbeResult NotPresent = new(ProbeCompletionState.NotPresent, null);
    }
}
