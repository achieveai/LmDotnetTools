using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.Sandbox.Command;

namespace AchieveAi.LmDotnetTools.Sandbox;

public sealed partial class SandboxClient
{
    /// <summary>The gateway tool name for POSIX command execution (see the pinned gateway's <c>execute_tool</c> dispatch).</summary>
    private const string BashToolName = "Bash";

    /// <summary>Initial delay of the manifest poll's exponential backoff.</summary>
    private static readonly TimeSpan S_commandPollInitialDelay = TimeSpan.FromMilliseconds(25);

    /// <summary>Upper bound the manifest poll's exponential backoff is capped at, so a long poll stays responsive without busy-waiting.</summary>
    private static readonly TimeSpan S_commandPollMaxDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Slack added past the gateway execution timeout when computing the manifest-poll deadline: it lets
    /// a just-completed command's manifest become visible after the command itself finishes, without
    /// waiting the full lease grace. A command that legitimately runs longer than this window is not
    /// lost — it surfaces a recoverable <see cref="SandboxErrorKind.TransportTimeout"/> and is recovered
    /// by a later same-id call while its artifacts are still retained.
    /// </summary>
    private static readonly TimeSpan S_commandPollGrace = TimeSpan.FromSeconds(1);

    /// <summary>Maximum number of times an idempotent chunk read is retried (from the highest verified offset) before failing.</summary>
    private const int CommandReadRetryLimit = 4;

    /// <summary>Delay between idempotent chunk-read retries.</summary>
    private static readonly TimeSpan S_commandReadRetryDelay = TimeSpan.FromMilliseconds(25);

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
    /// <para>
    /// <b>Same-id reuse never re-runs — within a bounded retention window.</b> A verified success
    /// reclaims the operation's large output but retains a bounded, credential-free completion marker
    /// (the manifest plus lease/created). For the operation-id idempotency/recovery retention window
    /// (24&#160;hours from creation, <see cref="CommandArtifactLayout.StaleAgeSeconds"/>) a later call
    /// with the same operation id is answered from that marker — the small output is returned verbatim,
    /// and a reclaimed large output is rejected as <see cref="SandboxErrorKind.Integrity"/> — without
    /// ever submitting a second RUN. Reusing an operation id with a <i>different</i> command fails
    /// <see cref="SandboxErrorKind.Integrity"/> (canonical digest mismatch), also without submitting.
    /// The window is inclusive of its 24&#160;hour boundary; once it elapses the bounded stale sweep may
    /// reclaim the marker, after which reusing the id is treated as a NEW operation that may re-execute.
    /// The SDK does not promise idempotency forever.
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

        // Pre-probe: recover a retained result (or its bounded completion marker), or reject a digest
        // mismatch, WITHOUT submitting. Only a completed manifest short-circuits here; a PENDING (a peer
        // holds the claim) or absent state — and any transport failure probing — falls through to the
        // single RUN, whose atomic claim elects one submitter. A live peer keeps the wrapper reporting
        // PENDING so this caller polls instead of re-running.
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

        var manifest = await SubmitRunAndResolveManifestAsync(
                sessionId,
                operationDirectory,
                operationId,
                digest,
                quotedArgv,
                command.NormalizedWorkingDirectory,
                executionTimeoutSeconds,
                ct
            )
            .ConfigureAwait(false);

        return await AssembleAndCleanupAsync(sessionId, operationDirectory, operationId, digest, manifest, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Makes the single side-effecting RUN submission and resolves the completion manifest, never
    /// resubmitting. Every outcome after a RUN that may have executed preserves the recoverable
    /// operation id: a clear gateway execution-timeout maps to <see cref="SandboxErrorKind.ExecutionTimeout"/>;
    /// an already-committed manifest is returned; a PENDING enters bounded manifest polling; and any
    /// ambiguous or malformed response (a lost transport response, an unexpected error, an unparsable
    /// body, or an unrecognized status) also enters manifest polling, because the command may still have
    /// run.
    /// </summary>
    private async Task<CommandManifest> SubmitRunAndResolveManifestAsync(
        string sessionId,
        string operationDirectory,
        string operationId,
        string digest,
        string quotedArgv,
        string normalizedWorkingDirectory,
        long executionTimeoutSeconds,
        CancellationToken ct
    )
    {
        var runScript = CommandScripts.BuildRun(
            operationDirectory,
            digest,
            quotedArgv,
            normalizedWorkingDirectory,
            executionTimeoutSeconds
        );

        JsonElement runResult;
        try
        {
            runResult = await SubmitBashAsync(sessionId, runScript, ct, operationId).ConfigureAwait(false);
        }
        catch (SandboxException ex)
            when (ex.Kind is SandboxErrorKind.TransportTimeout or SandboxErrorKind.Protocol)
        {
            // Ambiguous lost/malformed response (a transport timeout, a 5xx, or a torn MCP body) AFTER the
            // gateway may already have run the command: never resubmit. Recover from the persisted
            // manifest if the command ran; otherwise surface the recoverable failure, which
            // SubmitBashAsync has already tagged with the operation id for a later same-id retry.
            var recovered = await TryPollManifestAsync(sessionId, operationDirectory, operationId, ct)
                .ConfigureAwait(false);
            if (recovered is not null)
            {
                return recovered;
            }

            throw;
        }

        // A malformed RUN result shape is ambiguous (the command may have run): poll the manifest.
        string text;
        bool isError;
        try
        {
            (text, isError) = ExtractBashResult(runResult, "command execution", operationId);
        }
        catch (SandboxException)
        {
            return await PollManifestAsync(sessionId, operationDirectory, operationId, ct).ConfigureAwait(false);
        }

        if (isError)
        {
            if (IsGatewayExecutionTimeout(text))
            {
                throw new SandboxException(
                    SandboxErrorKind.ExecutionTimeout,
                    "The sandbox gateway's execution timeout elapsed before the command completed.",
                    operationId: operationId
                );
            }

            // A non-timeout gateway error running the wrapper is ambiguous: the command may have run, so
            // poll rather than fail outright — still without ever resubmitting.
            return await PollManifestAsync(sessionId, operationDirectory, operationId, ct).ConfigureAwait(false);
        }

        var sentinel = ParseSentinel(text, operationId);
        return sentinel.Kind switch
        {
            SentinelKind.Completed => sentinel.Manifest!,
            // PENDING, an absent op right after RUN, and any unrecognized status are all ambiguous
            // (the command may still be running or its status momentarily unreadable): poll the manifest.
            _ => await PollManifestAsync(sessionId, operationDirectory, operationId, ct).ConfigureAwait(false),
        };
    }

    /// <summary>The gateway execution-timeout, in whole seconds (at least 1), used both as the Bash timeout and in the canonical digest.</summary>
    private long GatewayExecutionTimeoutSeconds() =>
        Math.Max(1, (long)Math.Ceiling(_options.ExecutionTimeout.TotalSeconds));

    /// <summary>
    /// Submits one Bash tool call carrying <paramref name="script"/> and the gateway execution timeout.
    /// A command-flow submission passes its <paramref name="operationId"/>: because the transport layer
    /// is operation-agnostic (<see cref="SendMcpToolCallAsync"/> tags no id), any transport/protocol
    /// failure it raises is re-tagged here with the recoverable operation id — so a failure after the
    /// side-effecting RUN (or during a post-RUN probe/read) always carries the id needed to recover. The
    /// best-effort stale sweep passes <c>null</c> (no single operation is recoverable from it).
    /// </summary>
    private async Task<JsonElement> SubmitBashAsync(
        string sessionId,
        string script,
        CancellationToken ct,
        string? operationId = null
    )
    {
        var arguments = new { command = script, timeout = GatewayExecutionTimeoutSeconds() };
        try
        {
            return await SendMcpToolCallAsync(sessionId, BashToolName, arguments, ct).ConfigureAwait(false);
        }
        catch (SandboxException ex) when (operationId is not null && ex.OperationId is null)
        {
            throw new SandboxException(ex.Kind, ex.Message, ex.StatusCode, ex.InnerException, operationId);
        }
    }

    /// <summary>Issues one idempotent PROBE and parses the resulting state.</summary>
    private async Task<ProbeResult> ProbeAsync(
        string sessionId,
        string operationDirectory,
        string operationId,
        CancellationToken ct
    )
    {
        var result = await SubmitBashAsync(sessionId, CommandScripts.BuildProbe(operationDirectory), ct, operationId)
            .ConfigureAwait(false);
        var (text, isError) = ExtractBashResult(result, "command probe", operationId);
        if (isError)
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                "Sandbox command probe returned an error.",
                operationId: operationId
            );
        }

        var sentinel = ParseSentinel(text, operationId);
        return sentinel.Kind switch
        {
            SentinelKind.Completed => new ProbeResult(ProbeCompletionState.Completed, sentinel.Manifest),
            SentinelKind.Pending => new ProbeResult(ProbeCompletionState.Pending, null),
            SentinelKind.NotPresent => ProbeResult.NotPresent,
            // An unrecognized status on a plain probe is a protocol violation. The raw status token is
            // never echoed into the message — it is gateway-controlled text that could carry secrets.
            _ => throw UnrecognizedStatus(operationId),
        };
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
    /// Bounded idempotent poll for a completed manifest using deadline-based exponential backoff. The
    /// deadline is derived from the gateway execution timeout plus a short grace (the longest a
    /// legitimately-still-running command needs before its manifest is visible) and honours caller
    /// cancellation; it deliberately does NOT busy-poll a fixed tiny window. Returns <c>null</c> when the
    /// deadline elapses without a manifest. A transient transport failure on an individual probe is
    /// retried (not fatal) so a recovery poll survives a flaky gateway.
    /// </summary>
    private async Task<CommandManifest?> TryPollManifestAsync(
        string sessionId,
        string operationDirectory,
        string operationId,
        CancellationToken ct
    )
    {
        var deadline = DateTimeOffset.UtcNow + _options.ExecutionTimeout + S_commandPollGrace;
        var delay = S_commandPollInitialDelay;
        while (true)
        {
            try
            {
                var probe = await ProbeAsync(sessionId, operationDirectory, operationId, ct).ConfigureAwait(false);
                if (probe.State == ProbeCompletionState.Completed)
                {
                    return probe.Manifest;
                }
            }
            catch (SandboxException ex)
                when (ex.Kind is SandboxErrorKind.TransportTimeout or SandboxErrorKind.Protocol)
            {
                // Retry a transient probe transport/protocol failure within the deadline.
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return null;
            }

            await Task.Delay(delay < remaining ? delay : remaining, ct).ConfigureAwait(false);
            delay = delay + delay < S_commandPollMaxDelay ? delay + delay : S_commandPollMaxDelay;
        }
    }

    /// <summary>
    /// Verifies the manifest digest, materializes both streams (verifying length + SHA-256), and on
    /// success reclaims the operation's large output while retaining its bounded completion marker, then
    /// runs a bounded stale sweep. A digest or integrity mismatch throws BEFORE any reclaim, so the
    /// artifacts are retained for diagnosis or a later attempt.
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

        await BestEffortReclaimAsync(sessionId, operationDirectory, operationId, ct).ConfigureAwait(false);
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

        return DecodeUtf8StrictOrThrow(bytes, streamName, operationId);
    }

    /// <summary>
    /// Reads a stream back in bounded base64 chunks that each stay under the gateway's <c>exec</c>
    /// truncation limits, resuming from the last verified byte offset. Each idempotent chunk read is
    /// retried from that highest verified offset within a bounded limit, so a transient transport,
    /// protocol, or integrity hiccup does not lose already-verified bytes or force a resubmission. The
    /// gateway's unstable <c>output_*.txt</c> path is never consulted; every byte comes from the stable,
    /// command-scoped artifact file.
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
            var chunk = await ReadChunkWithRetryAsync(
                    sessionId,
                    operationDirectory,
                    streamName,
                    offset,
                    length,
                    operationId,
                    ct
                )
                .ConfigureAwait(false);

            buffer.Write(chunk, 0, chunk.Length);
            offset += length;
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Reads exactly one verified chunk at <paramref name="offset"/>, retrying the idempotent read from
    /// the same offset on a transient transport/protocol/integrity failure up to
    /// <see cref="CommandReadRetryLimit"/> times before surfacing the failure (always carrying the
    /// recoverable operation id). A short chunk means the artifact no longer matches the manifest — for
    /// example the operation completed and its large output was reclaimed — and is treated as an
    /// integrity failure rather than silently returning partial or mixed content.
    /// </summary>
    private async Task<byte[]> ReadChunkWithRetryAsync(
        string sessionId,
        string operationDirectory,
        string streamName,
        long offset,
        int length,
        string operationId,
        CancellationToken ct
    )
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var script = CommandScripts.BuildRead(operationDirectory, streamName, offset, length);
                var result = await SubmitBashAsync(sessionId, script, ct, operationId).ConfigureAwait(false);
                var (text, isError) = ExtractBashResult(result, "command output read", operationId);
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
                    throw new SandboxException(
                        SandboxErrorKind.Integrity,
                        $"Sandbox command {streamName} chunk at offset {offset} returned {chunk.Length} of "
                            + $"{length} expected bytes (the operation's output may have been reclaimed after completion).",
                        operationId: operationId
                    );
                }

                return chunk;
            }
            catch (SandboxException ex) when (IsRetriableRead(ex) && attempt < CommandReadRetryLimit)
            {
                // Idempotent read: retry from this same highest-verified offset within the bound.
                await Task.Delay(S_commandReadRetryDelay, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>A chunk read is safe to retry from the same offset for a transient transport, protocol, or integrity condition (reads never mutate state).</summary>
    private static bool IsRetriableRead(SandboxException ex) =>
        ex.Kind
            is SandboxErrorKind.TransportTimeout
                or SandboxErrorKind.Protocol
                or SandboxErrorKind.Integrity;

    /// <summary>
    /// Reclaims the verified operation's large output while retaining its bounded completion marker,
    /// then runs a bounded stale sweep. Both are best-effort and never fail the command: the result is
    /// already assembled, so a failed reclaim/sweep must not turn a success into a failure.
    /// </summary>
    private async Task BestEffortReclaimAsync(
        string sessionId,
        string operationDirectory,
        string operationId,
        CancellationToken ct
    )
    {
        try
        {
            _ = await SubmitBashAsync(sessionId, CommandScripts.BuildReclaim(operationDirectory), ct, operationId)
                .ConfigureAwait(false);
        }
        catch (SandboxException)
        {
            // Best-effort: the operation already succeeded; a failed reclaim must not fail it.
        }
        catch (OperationCanceledException)
        {
            // The verified result is already assembled; cancellation simply skips maintenance.
        }

        await StaleCleanupAsync(sessionId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Bounded, session-scoped stale-artifact sweep: list at most a fixed number of directories, then
    /// issue a re-validated purge for those the snapshot suggests are both expired and 24h+ old. The
    /// purge re-checks each directory's CURRENT lease/created at delete time in the shell, so a directory
    /// that was refreshed (re-active) between the listing and the purge is never deleted.
    /// </summary>
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
            var (text, isError) = ExtractBashResult(listing, "stale cleanup listing", operationId: null);
            if (isError)
            {
                return;
            }

            var entries = CommandSentinel.ParseGcListing(text);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var name in CommandStaleCleanup.SelectStale(entries, now))
            {
                _ = await SubmitBashAsync(sessionId, CommandScripts.BuildGcPurge(name), ct).ConfigureAwait(false);
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

    /// <summary>Whether a gateway <c>isError</c> RUN text denotes the gateway-side execution timeout (never echoed into an exception).</summary>
    private static bool IsGatewayExecutionTimeout(string text) =>
        text.Contains("timed out", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parses the single sentinel status line into a discriminated result. A recognized MANIFEST decodes
    /// and fully validates the manifest (a malformed payload is a genuine protocol failure and
    /// propagates); PENDING/NONE map to their states; and both an absent marker line and an unrecognized
    /// status map to <see cref="SentinelKind.Unrecognized"/> so the caller decides whether it is a
    /// pollable ambiguity (after a RUN) or a hard protocol violation (on a plain probe).
    /// </summary>
    private ParsedSentinel ParseSentinel(string text, string operationId)
    {
        string kind;
        string? payload;
        try
        {
            (kind, payload) = CommandSentinel.Parse(text);
        }
        catch (SandboxException)
        {
            return new ParsedSentinel(SentinelKind.Unrecognized, null);
        }

        return kind switch
        {
            CommandSentinel.KindManifest => new ParsedSentinel(
                SentinelKind.Completed,
                DecodeManifest(payload, operationId)
            ),
            CommandSentinel.KindPending => new ParsedSentinel(SentinelKind.Pending, null),
            CommandSentinel.KindNone => new ParsedSentinel(SentinelKind.NotPresent, null),
            _ => new ParsedSentinel(SentinelKind.Unrecognized, null),
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

        CommandManifest? manifest;
        try
        {
            var json = Convert.FromBase64String(base64Payload);
            manifest = JsonSerializer.Deserialize<CommandManifest>(json, CommandManifest.Json);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                "Sandbox command manifest was malformed.",
                operationId: operationId
            );
        }

        if (manifest is null)
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                "Sandbox command manifest deserialized to null.",
                operationId: operationId
            );
        }

        CommandManifestValidator.Validate(manifest, operationId);
        return manifest;
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

    /// <summary>
    /// Decodes verified stream bytes as STRICT UTF-8. The bytes have already passed their length + SHA-256
    /// check, so they are exactly what the command produced; if they are nevertheless not well-formed
    /// UTF-8 this raises <see cref="SandboxErrorKind.Integrity"/> (carrying the operation id) rather than
    /// silently substituting U+FFFD replacement characters and returning corrupted text. V1 exposes
    /// output as text, so non-UTF-8 output is surfaced as a failure by design.
    /// </summary>
    private static string DecodeUtf8StrictOrThrow(byte[] bytes, string streamName, string operationId)
    {
        try
        {
            return S_strictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new SandboxException(
                SandboxErrorKind.Integrity,
                $"Sandbox command {streamName} is not valid UTF-8; V1 command output must be UTF-8 text.",
                operationId: operationId
            );
        }
    }

    /// <summary>Extracts the text content and error flag from a Bash tool result, mapping any unexpected shape to <see cref="SandboxErrorKind.Protocol"/> (carrying the operation id when one is known).</summary>
    private static (string Text, bool IsError) ExtractBashResult(JsonElement result, string phase, string? operationId)
    {
        if (result.ValueKind != JsonValueKind.Object)
        {
            throw MalformedBashResult(phase, operationId);
        }

        var isError =
            result.TryGetProperty("isError", out var errorElement) && errorElement.ValueKind == JsonValueKind.True;

        if (
            !result.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array
            || content.GetArrayLength() == 0
        )
        {
            throw MalformedBashResult(phase, operationId);
        }

        var first = content[0];
        if (
            first.ValueKind != JsonValueKind.Object
            || !first.TryGetProperty("text", out var textElement)
            || textElement.ValueKind != JsonValueKind.String
        )
        {
            throw MalformedBashResult(phase, operationId);
        }

        return (textElement.GetString() ?? string.Empty, isError);
    }

    private static SandboxException MalformedBashResult(string phase, string? operationId) =>
        new(
            SandboxErrorKind.Protocol,
            $"Sandbox gateway returned a malformed Bash result for {phase}.",
            operationId: operationId
        );

    /// <summary>The fixed Protocol failure for an unrecognized status line. The raw, gateway-controlled status token is deliberately never included — it could carry secret-bearing text.</summary>
    private static SandboxException UnrecognizedStatus(string operationId) =>
        new(
            SandboxErrorKind.Protocol,
            "Sandbox command wrapper returned an unrecognized status line.",
            operationId: operationId
        );

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>Strict UTF-8 (throwing) decoder used for materialized stream text — never the replacement-fallback <see cref="Encoding.UTF8"/>.</summary>
    private static readonly UTF8Encoding S_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

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

    /// <summary>The classification of a parsed sentinel line, distinguishing a hard protocol violation from a pollable ambiguity.</summary>
    private enum SentinelKind
    {
        Completed,
        Pending,
        NotPresent,
        Unrecognized,
    }

    private readonly record struct ParsedSentinel(SentinelKind Kind, CommandManifest? Manifest);
}
