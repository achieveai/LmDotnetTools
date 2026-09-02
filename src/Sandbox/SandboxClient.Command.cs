using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.Sandbox.Command;
using AchieveAi.LmDotnetTools.Sandbox.Wire;

namespace AchieveAi.LmDotnetTools.Sandbox;

public sealed partial class SandboxClient
{
    /// <summary>Initial delay of the operation-status poll's exponential backoff.</summary>
    private static readonly TimeSpan S_commandPollInitialDelay = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// Upper bound the operation-status poll's exponential backoff is capped at, so a long-running
    /// command's poll stays responsive without busy-waiting.
    /// </summary>
    private static readonly TimeSpan S_commandPollMaxDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Slack added past the gateway execution timeout when computing the poll deadline: it lets a
    /// command's terminal status become visible immediately after it finishes, without the SDK
    /// giving up a moment too early on a command that legitimately runs right up to the deadline.
    /// </summary>
    private static readonly TimeSpan S_commandPollGrace = TimeSpan.FromSeconds(1);

    /// <summary>Strict UTF-8 (throwing) decoder used for downloaded stdout/stderr artifacts — never the replacement-fallback <see cref="Encoding.UTF8"/>.</summary>
    private static readonly UTF8Encoding S_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    /// <summary>
    /// Runs one native, non-interactive command (an executable plus argv — no shell) in the sandbox
    /// via the gateway's direct operations API (ADR 0031 / issue #119) and returns its exact captured
    /// output.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Flow.</b> The SDK submits <c>POST .../operations</c> carrying the resolved operation id. A
    /// fresh submission is answered <c>202 Accepted</c>; an identical-request replay of an existing
    /// operation id is answered <c>200 OK</c> — both carry the same status-snapshot shape. When the
    /// snapshot is not yet terminal, the SDK polls <c>GET .../operations/{operation_id}</c> with a
    /// bounded exponential backoff until the gateway reports a terminal status or a deadline (the
    /// configured <see cref="SandboxClientOptions.ExecutionTimeout"/> plus a short grace) elapses.
    /// Once terminal, the command's stdout/stderr artifacts are downloaded verbatim through the files
    /// API and decoded as strict UTF-8.
    /// </para>
    /// <para>
    /// <b>The operation record is released on the success path.</b> Once both artifacts have been
    /// downloaded, this call issues <c>DELETE .../operations/{operation_id}</c> (ADR 0031 §5) so the
    /// gateway stops holding a record — and the artifact files — for a command whose output is already in
    /// hand. That is what keeps a long-lived session under the gateway's per-session record cap
    /// (<c>OPERATION_MAX_RECORDS_PER_SESSION</c>, default 256), whose terminal-TTL reaper would otherwise
    /// take an hour to free each slot. The release is best-effort: it never throws and never changes the
    /// command's outcome, and <see cref="SandboxCommandResult.OperationRecordReleased"/> reports whether
    /// it happened. A FAILING call releases nothing — the operation id stays a usable replay handle.
    /// </para>
    /// <para>
    /// <b>Idempotency is gateway-scoped, not durable.</b> Reusing the same
    /// <see cref="SandboxCommand.OperationId"/> re-submits the same request, and the gateway answers
    /// with the existing operation's current (or terminal) status rather than running it again — but
    /// only while the gateway retains that operation's state. A gateway restart drops it, so reusing
    /// an operation id after a restart may start a genuinely new execution. This SDK keeps no local
    /// manifest, digest, or lease bookkeeping of its own; the gateway is the sole source of truth.
    /// </para>
    /// <para>
    /// <b>Outcomes.</b> A gateway execution timeout, or the SDK's own poll deadline elapsing while the
    /// operation is still running, surfaces as <see cref="SandboxErrorKind.ExecutionTimeout"/>; an
    /// output-cap violation surfaces as <see cref="SandboxErrorKind.OutputLimitExceeded"/>; a
    /// non-UTF-8 artifact surfaces as <see cref="SandboxErrorKind.Integrity"/>; a malformed or
    /// unrecognized gateway response surfaces as <see cref="SandboxErrorKind.Protocol"/>. A caller
    /// cancellation surfaces as a plain <see cref="OperationCanceledException"/>.
    /// </para>
    /// <para>
    /// <b>Cancellation is best-effort and does not reach the remote process.</b> Cancelling
    /// <paramref name="ct"/> only abandons the SDK's local wait for a result; it does not ask the
    /// gateway to terminate the remote command, which may keep running (or may already have
    /// completed) regardless. Terminating the remote process tree is out of scope for V1.
    /// </para>
    /// </remarks>
    /// <exception cref="SandboxException">
    /// A gateway execution timeout or an unresolved poll deadline, an output-limit violation, a
    /// non-UTF-8 artifact (<see cref="SandboxErrorKind.Integrity"/>), or a malformed/unrecognized
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
        var mountId = await ResolveWorkspaceMountIdAsync(sessionId, ct).ConfigureAwait(false);

        var requestDto = new CreateOperationRequestDto(
            operationId,
            command.Arguments[0],
            command.Arguments.Count > 1 ? command.Arguments.Skip(1).ToList() : null,
            null,
            new OperationCwdDto(mountId, command.NormalizedWorkingDirectory),
            GatewayExecutionTimeoutSeconds(),
            null
        );

        var status = await SubmitOperationAsync(sessionId, operationId, requestDto, ct).ConfigureAwait(false);
        if (IsRunning(status.Status))
        {
            status = await PollOperationAsync(sessionId, operationId, ct).ConfigureAwait(false);
        }

        var result = await ResolveResultAsync(sessionId, operationId, status, ct).ConfigureAwait(false);

        // The terminal result is fully consumed here — both artifacts are downloaded and decoded — so the
        // gateway's record has nothing left to serve and is released (ADR 0031 §5's explicit lifecycle
        // exit). Doing it in the SDK is what keeps a long-lived session under the gateway's
        // OPERATION_MAX_RECORDS_PER_SESSION cap (default 256): the reaper prunes a terminal record only
        // after OPERATION_TERMINAL_TTL_SECS (default 3600), so a session running more than 256 commands
        // within that hour would otherwise have every later submit refused with
        // 503 operation_capacity_exhausted (issue #725). The release happens AFTER the downloads because
        // the same DELETE also removes the operation's generation-scoped artifact directory.
        //
        // Only the SUCCESS path releases. A failure exit deliberately leaves the record alone: the
        // operation id is this SDK's documented recovery handle (re-submitting it replays the existing
        // operation instead of running the command again), and deleting the record plus its artifacts
        // under, say, a lost artifact download would turn a recoverable failure into a forced re-run of a
        // side-effecting command.
        return result with
        {
            OperationRecordReleased = await ReleaseOperationRecordAsync(sessionId, operationId, ct)
                .ConfigureAwait(false),
        };
    }

    /// <summary>
    /// Best-effort release of a fully-consumed operation's gateway record. Returns whether the record is
    /// gone; NEVER throws, because the command it belongs to has already succeeded and its output is
    /// already in the caller's hands — a failed cleanup must not be promoted into the caller's failure.
    /// The outcome is reported on <see cref="SandboxCommandResult.OperationRecordReleased"/> instead.
    /// </summary>
    private async Task<bool> ReleaseOperationRecordAsync(string sessionId, string operationId, CancellationToken ct)
    {
        try
        {
            await DeleteOperationAsync(sessionId, operationId, ct).ConfigureAwait(false);
            return true;
        }
        catch (SandboxException ex) when (ex.Kind == SandboxErrorKind.NotFound)
        {
            // ADR 0031 §6's no-existence-oracle boundary makes one uniform 404 cover an already-deleted
            // record, a TTL-pruned one, and one dropped by a gateway restart alike. Every one of those is
            // the outcome this call wanted: the record is not holding a slot in the session's cap.
            return true;
        }
        catch (Exception ex) when (ex is SandboxException or OperationCanceledException)
        {
            // Includes the caller cancelling while this DELETE is in flight: the result is already
            // resolved, so surfacing that cancellation here would discard a completed command's output.
            return false;
        }
    }

    /// <summary>
    /// Deletes ONE completed command operation from the gateway — its in-memory record AND, best-effort,
    /// its generation-scoped stdout/stderr artifact directory under the session's writable workspace
    /// (<c>DELETE .../operations/{operation_id}</c>, ADR 0031 §5 / issue #464).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> Every <see cref="ExecuteAsync"/> leaves
    /// <c>.mcp-gateway/operations/&lt;operation_id&gt;/&lt;generation&gt;/{stdout,stderr}</c> on disk in
    /// the caller's writable workspace, and ADR 0031 §5's reaper prunes the terminal <b>in-memory record
    /// only</b> — it never deletes those files. They persist for the sandbox's lifetime unless this call
    /// removes them. <see cref="ExecuteAsync"/> already issues it for you on its success path, once the
    /// command's output has been consumed; this public entry point is for the cases it deliberately leaves
    /// alone — a command that FAILED (whose operation id stays a replay handle) that the caller has since
    /// given up on, or an operation the caller submitted and tracked itself.
    /// <see cref="DeleteAsync"/> (tearing down the whole sandbox) remains the bulk cleanup and is
    /// unaffected.
    /// </para>
    /// <para>
    /// <b>This is not cancellation.</b> ADR 0031 puts cancellation explicitly out of scope: a still-RUNNING
    /// operation is refused with <c>409 operation_running</c>, which surfaces here as
    /// <see cref="SandboxErrorKind.Conflict"/> with <see cref="SandboxException.ErrorCode"/> set to
    /// <c>operation_running</c>. That code is the discriminator — the status alone is not, since
    /// <c>idempotency_conflict</c> and <c>target_locked</c> are also <c>409</c>/<see cref="SandboxErrorKind.Conflict"/>
    /// and are NOT cleared by waiting. Branch on the code, wait for the operation to reach a terminal
    /// state, and delete then.
    /// </para>
    /// <para>
    /// <b>It is not idempotent.</b> The gateway answers <c>204 No Content</c> for a record it removed and a
    /// uniform <c>404 operation_not_found</c> for one it does not hold — which covers an already-deleted
    /// operation, a TTL-pruned record, a record dropped by a gateway restart, and an id that never existed
    /// alike (ADR 0031 §6's no-existence-oracle boundary). A best-effort cleanup caller should therefore
    /// treat <see cref="SandboxErrorKind.NotFound"/> as "nothing left to reclaim" rather than as an error.
    /// Note the converse too: because the reaper drops records without touching files, a record already
    /// pruned by TTL can no longer be deleted through this route at all, and its artifacts then live until
    /// the sandbox does — so delete promptly rather than in a nightly sweep.
    /// </para>
    /// </remarks>
    /// <param name="sessionId">The sandbox session that ran the operation.</param>
    /// <param name="operationId">
    /// The operation to remove — <see cref="SandboxCommandResult.OperationId"/> from the command's result,
    /// or <see cref="SandboxException.OperationId"/> from a failure that carried one.
    /// </param>
    /// <param name="ct">
    /// Abandons the single in-flight request — there is no poll loop here to leave running; a cancelled
    /// delete may still have been applied.
    /// </param>
    /// <exception cref="SandboxException">
    /// The operation is still running (<see cref="SandboxErrorKind.Conflict"/>, <c>operation_running</c>),
    /// no such session/operation (<see cref="SandboxErrorKind.NotFound"/>), the credential was refused
    /// (<see cref="SandboxErrorKind.Authorization"/>), the gateway was busy
    /// (<see cref="SandboxErrorKind.Unavailable"/>), or the transport deadline elapsed
    /// (<see cref="SandboxErrorKind.TransportTimeout"/>). Every one of these carries
    /// <see cref="SandboxException.OperationId"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task DeleteOperationAsync(string sessionId, string operationId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);

        // The operation id rides along on every failure exit, exactly as it does on submit/poll: a delete
        // whose response was lost leaves the caller unable to tell whether the artifacts were reclaimed,
        // and the id is the only handle for re-issuing it.
        using var response = await SendDirectAsync(
                HttpMethod.Delete,
                $"api/v1/sandboxes/{Uri.EscapeDataString(sessionId)}/operations/{Uri.EscapeDataString(operationId)}",
                null,
                sessionId,
                ct,
                operationId
            )
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw await MapDirectErrorAsync(
                    response,
                    $"deleting operation '{operationId}'",
                    sessionId,
                    ct,
                    operationId: operationId
                )
                .ConfigureAwait(false);
        }

        // Any success status is a completed delete, not just the 204 the gateway sends today: there is no
        // body worth parsing either way, and nothing to return — the caller learns the outcome from the
        // absence of an exception. Narrowing this to exactly-204 would turn a successful cleanup into a
        // reported failure the moment the gateway answered 200.
    }

    /// <summary>The gateway execution timeout, in whole seconds (at least 1), sent as the operation's <c>timeout_secs</c>.</summary>
    private long GatewayExecutionTimeoutSeconds() =>
        Math.Max(1, (long)Math.Ceiling(_options.ExecutionTimeout.TotalSeconds));

    /// <summary>Submits the operation and returns its initial status snapshot (a fresh <c>202</c> or an idempotent-replay <c>200</c>).</summary>
    private async Task<OperationStatusDto> SubmitOperationAsync(
        string sessionId,
        string operationId,
        CreateOperationRequestDto requestDto,
        CancellationToken ct
    )
    {
        // The operation id rides along so EVERY failure exit of the submit — a lost response, an
        // unreachable gateway, or a gateway-reported error — carries the recovery handle. A submit whose
        // response is lost is precisely the case where the caller cannot tell whether the command ran, and
        // when the SDK generated the id this exception is the only place it is ever surfaced.
        using var response = await SendDirectAsync(
                HttpMethod.Post,
                $"api/v1/sandboxes/{Uri.EscapeDataString(sessionId)}/operations",
                JsonContent.Create(requestDto, options: SandboxJson.RestOptions),
                sessionId,
                ct,
                operationId
            )
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw await MapDirectErrorAsync(
                    response,
                    $"submitting operation '{operationId}'",
                    sessionId,
                    ct,
                    operationId: operationId
                )
                .ConfigureAwait(false);
        }

        return await ReadOperationStatusOrThrowAsync(response, $"submitting operation '{operationId}'", operationId, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Bounded poll for a terminal operation status using deadline-based exponential backoff. The
    /// deadline is the configured <see cref="SandboxClientOptions.ExecutionTimeout"/> plus a short
    /// grace, and honours caller cancellation; it deliberately does not busy-poll a fixed tiny window.
    /// </summary>
    private async Task<OperationStatusDto> PollOperationAsync(
        string sessionId,
        string operationId,
        CancellationToken ct
    )
    {
        var deadline = DateTimeOffset.UtcNow + _options.ExecutionTimeout + S_commandPollGrace;
        var delay = S_commandPollInitialDelay;
        while (true)
        {
            var status = await GetOperationStatusAsync(sessionId, operationId, ct).ConfigureAwait(false);
            if (!IsRunning(status.Status))
            {
                return status;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                throw new SandboxException(
                    SandboxErrorKind.ExecutionTimeout,
                    "The sandbox gateway's execution timeout elapsed before the command completed.",
                    operationId: operationId
                );
            }

            await Task.Delay(delay < remaining ? delay : remaining, ct).ConfigureAwait(false);
            delay = delay + delay < S_commandPollMaxDelay ? delay + delay : S_commandPollMaxDelay;
        }
    }

    /// <summary>Issues one idempotent <c>GET .../operations/{operation_id}</c> poll and returns the parsed status.</summary>
    private async Task<OperationStatusDto> GetOperationStatusAsync(
        string sessionId,
        string operationId,
        CancellationToken ct
    )
    {
        using var response = await SendDirectAsync(
                HttpMethod.Get,
                $"api/v1/sandboxes/{Uri.EscapeDataString(sessionId)}/operations/{Uri.EscapeDataString(operationId)}",
                null,
                sessionId,
                ct,
                operationId
            )
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw await MapDirectErrorAsync(
                    response,
                    $"polling operation '{operationId}'",
                    sessionId,
                    ct,
                    operationId: operationId
                )
                .ConfigureAwait(false);
        }

        return await ReadOperationStatusOrThrowAsync(response, $"polling operation '{operationId}'", operationId, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Whether <paramref name="status"/> is the gateway's non-terminal <c>running</c> state.</summary>
    private static bool IsRunning(string status) => string.Equals(status, "running", StringComparison.Ordinal);

    /// <summary>
    /// Deserializes a 2xx operation-status response body, mapping a malformed or empty body to
    /// <see cref="SandboxErrorKind.Protocol"/> and rejecting a snapshot whose <c>operation_id</c> does not
    /// correlate to the operation this call is tracking (a proxy/gateway returning a valid snapshot for a
    /// DIFFERENT operation must never be attributed to this command).
    /// </summary>
    private static async Task<OperationStatusDto> ReadOperationStatusOrThrowAsync(
        HttpResponseMessage response,
        string operation,
        string operationId,
        CancellationToken ct
    )
    {
        OperationStatusDto? status;
        try
        {
            status = await response
                .Content.ReadFromJsonAsync<OperationStatusDto>(SandboxJson.RestOptions, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned a malformed response for {operation}.",
                (int)response.StatusCode,
                ex,
                operationId
            );
        }

        var resolved =
            status
            ?? throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned an empty response for {operation}.",
                (int)response.StatusCode,
                operationId: operationId
            );

        // Correlate the snapshot to THIS operation — a valid-but-mismatched operation_id (e.g. a proxy
        // stitching in another operation's response) is a protocol violation, not this command's result.
        // The returned id is not echoed into the message (avoid surfacing unrelated response content).
        if (!string.Equals(resolved.OperationId, operationId, StringComparison.Ordinal))
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned a status snapshot for a different operation than requested for {operation}.",
                (int)response.StatusCode,
                operationId: operationId
            );
        }

        return resolved;
    }

    /// <summary>
    /// Maps a terminal <see cref="OperationStatusDto"/> to its <see cref="SandboxCommandResult"/>,
    /// downloading the stdout/stderr artifacts once the exit disposition is resolved.
    /// </summary>
    private async Task<SandboxCommandResult> ResolveResultAsync(
        string sessionId,
        string operationId,
        OperationStatusDto status,
        CancellationToken ct
    )
    {
        var exitCode = status.Status switch
        {
            "succeeded" => status.ExitCode
                ?? throw new SandboxException(
                    SandboxErrorKind.Protocol,
                    $"Sandbox gateway reported a succeeded operation '{operationId}' with no exit code.",
                    operationId: operationId
                ),
            "failed" => status.ExitCode
                ?? throw new SandboxException(
                    SandboxErrorKind.Protocol,
                    $"Sandbox gateway reported a failed operation '{operationId}' with no exit code.",
                    operationId: operationId
                ),
            "timed_out" => throw new SandboxException(
                SandboxErrorKind.ExecutionTimeout,
                "The sandbox gateway's execution timeout elapsed before the command completed.",
                operationId: operationId
            ),
            "output_limit_exceeded" => throw new SandboxException(
                SandboxErrorKind.OutputLimitExceeded,
                "The command's combined stdout+stderr exceeded the operation's output cap.",
                operationId: operationId
            ),
            "internal_failure" => throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway reported an internal failure running operation '{operationId}'.",
                operationId: operationId
            ),
            _ => throw new SandboxException(
                SandboxErrorKind.Protocol,
                "Sandbox gateway returned an unrecognized operation status.",
                operationId: operationId
            ),
        };

        var artifacts =
            status.Artifacts
            ?? throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway did not report artifacts for terminal operation '{operationId}'.",
                operationId: operationId
            );

        // Guard the artifact paths before they reach Uri.EscapeDataString: a JSON null/empty stdout_path
        // or stderr_path would otherwise throw a raw ArgumentNullException that escapes the SandboxException
        // contract. A terminal operation must always name both artifacts.
        if (string.IsNullOrEmpty(artifacts.StdoutPath) || string.IsNullOrEmpty(artifacts.StderrPath))
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway reported terminal operation '{operationId}' with a missing stdout/stderr artifact path.",
                operationId: operationId
            );
        }

        var standardOutput = await DownloadArtifactAsync(
                sessionId,
                artifacts.MountId,
                artifacts.StdoutPath,
                "stdout",
                operationId,
                ct
            )
            .ConfigureAwait(false);
        var standardError = await DownloadArtifactAsync(
                sessionId,
                artifacts.MountId,
                artifacts.StderrPath,
                "stderr",
                operationId,
                ct
            )
            .ConfigureAwait(false);

        return new SandboxCommandResult
        {
            ExitCode = exitCode,
            StandardOutput = standardOutput,
            StandardError = standardError,
            OperationId = operationId,
        };
    }

    /// <summary>
    /// Downloads one terminal operation's artifact verbatim (<c>GET .../files/{mount_id}?path=...</c>)
    /// and decodes it as strict UTF-8. A zero-byte artifact decodes to the empty string.
    /// </summary>
    private async Task<string> DownloadArtifactAsync(
        string sessionId,
        long mountId,
        string path,
        string streamName,
        string operationId,
        CancellationToken ct
    )
    {
        var bytes = await DownloadCappedBytesAsync(
                $"api/v1/sandboxes/{Uri.EscapeDataString(sessionId)}/files/{mountId}?path={Uri.EscapeDataString(path)}",
                sessionId,
                $"downloading {streamName} for operation '{operationId}'",
                operationId,
                maxBytes: null,
                ct
            )
            .ConfigureAwait(false);
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
}
